using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Calcpad.AI;
using Microsoft.Win32;

namespace Calcpad.Wpf.Ai
{
    /// <summary>
    /// Dockable chat panel that talks to Claude through <see cref="IAiClient"/> and can insert
    /// generated markdown-first CalcpadCE into the editor. Network stays off until the user sends.
    /// </summary>
    public partial class AssistantPanel : UserControl
    {
        private readonly ISecretStore _secrets = new DpapiSecretStore();
        private readonly List<ChatMessage> _history = new();
        private readonly List<ChatAttachment> _pendingAttachments = new();
        private readonly IChatHistoryStore _historyStore = new JsonChatHistoryStore(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CalcpadCE", "chat-history.json"));
        private CancellationTokenSource? _cts;
        private string _lastReply = string.Empty;

        /// <summary>Raised when the user asks to insert text (a snippet or a reply) into the worksheet.</summary>
        public event Action<string>? InsertRequested;

        /// <summary>Set by the host so the panel can optionally include the current worksheet as context.</summary>
        public Func<string>? GetWorksheetText { get; set; }

        /// <summary>Set by the host so agent mode can evaluate a Calcpad snippet and return plain text.</summary>
        public Func<string, string>? EvaluateWorksheet { get; set; }

        /// <summary>Set by the host so agent mode can read the current computed worksheet output.</summary>
        public Func<string>? GetResultText { get; set; }

        public AssistantPanel()
        {
            InitializeComponent();
            SnippetBox.ItemsSource = CalcpadSnippets.All;
            SnippetBox.SelectedIndex = 0;
            KeyBox.Password = _secrets.Load() ?? string.Empty;
            if (!_secrets.HasKey)
                SettingsExpander.IsExpanded = true;

            LoadHistory();
        }

        private void LoadHistory()
        {
            var saved = _historyStore.Load();
            foreach (var m in saved)
            {
                _history.Add(m);
                AppendMessage(
                    m.Role == AiRole.User ? "You" : "Claude",
                    m.Text,
                    m.Role == AiRole.User ? Brushes.SteelBlue : Brushes.SeaGreen);
            }
            if (_history.Count > 0)
                InsertReplyButton.IsEnabled =
                    _history[^1].Role == AiRole.Assistant &&
                    (_lastReply = _history[^1].Text).Length > 0;
        }

        private void SaveKeyButton_Click(object sender, RoutedEventArgs e)
        {
            _secrets.Save(KeyBox.Password);
            AppendSystemLine(_secrets.HasKey ? "API key saved." : "API key cleared.");
        }

        private void InsertSnippetButton_Click(object sender, RoutedEventArgs e)
        {
            if (SnippetBox.SelectedItem is CalcpadSnippet s)
                InsertRequested?.Invoke(s.Body);
        }

        private void InsertReplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastReply))
                InsertRequested?.Invoke(ExtractCode(_lastReply));
        }

        private void PromptBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V with an image on the clipboard → attach it (vision).
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Clipboard.ContainsImage())
            {
                AddClipboardImage();
                e.Handled = true;
                return;
            }
            // Enter sends; Shift+Enter inserts a newline.
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

        private async Task SendAsync()
        {
            var prompt = PromptBox.Text?.Trim() ?? string.Empty;
            var attachments = _pendingAttachments.ToList();
            if (prompt.Length == 0 && attachments.Count == 0)
                return;

            var options = new AiOptions
            {
                ApiKey = _secrets.Load() ?? string.Empty,
                Model = SelectedModel()
            };
            if (!options.IsConfigured)
            {
                AppendSystemLine("Set your Anthropic API key in Settings first.");
                SettingsExpander.IsExpanded = true;
                return;
            }

            var agent = AgentModeCheck.IsChecked == true;
            var userText = prompt;
            if (!agent && SendContextCheck.IsChecked == true && GetWorksheetText is not null)
            {
                var worksheet = GetWorksheetText();
                if (!string.IsNullOrWhiteSpace(worksheet))
                    userText = "Current worksheet:\n```\n" + worksheet + "\n```\n\n" + prompt;
            }

            _history.Add(attachments.Count > 0
                ? ChatMessage.FromUser(userText, attachments)
                : ChatMessage.FromUser(userText));

            AppendMessage("You", prompt.Length > 0 ? prompt : "(attachment)", Brushes.SteelBlue);
            if (attachments.Count > 0)
                AppendSystemLine($"📎 {attachments.Count} attachment(s) sent");

            _pendingAttachments.Clear();
            UpdateAttachmentsLabel();
            PromptBox.Clear();
            SetBusy(true);
            try
            {
                if (agent)
                    await RunAgentAsync(options);
                else
                    await RunChatAsync(options);
            }
            finally
            {
                SetBusy(false);
                _historyStore.Save(_history);
            }
        }

        private async Task RunChatAsync(AiOptions options)
        {
            IAiClient client;
            try
            {
                client = new AnthropicAiClient(options);
            }
            catch (Exception ex)
            {
                AppendSystemLine("Error: " + ex.Message);
                return;
            }

            _cts = new CancellationTokenSource();
            var reply = new StringBuilder();
            BeginStream();
            try
            {
                await foreach (var chunk in client.StreamReplyAsync(_history, _cts.Token))
                {
                    reply.Append(chunk);
                    AppendStream(chunk);
                }
            }
            catch (OperationCanceledException)
            {
                reply.Append("\n[cancelled]");
            }
            catch (Exception ex)
            {
                AppendSystemLine("[error] " + ex.Message);
            }
            finally
            {
                _lastReply = reply.ToString();
                EndStream(_lastReply);
                if (_lastReply.Length > 0)
                {
                    _history.Add(ChatMessage.FromAssistant(_lastReply));
                    InsertReplyButton.IsEnabled = true;
                }
            }
        }

        private async Task RunAgentAsync(AiOptions options)
        {
            AnthropicModelCaller caller;
            try
            {
                caller = new AnthropicModelCaller(options);
            }
            catch (Exception ex)
            {
                AppendSystemLine("Error: " + ex.Message);
                return;
            }

            var tools = CalcpadAgentTools.Create(
                getWorksheet: () => Dispatcher.Invoke(() => GetWorksheetText?.Invoke() ?? string.Empty),
                insertText: t => Dispatcher.Invoke(() => ApplyAgentEdit(t)),
                evaluate: s => Dispatcher.Invoke(() => EvaluateWorksheet?.Invoke(s) ?? "(no evaluator configured)"),
                getResult: () => Dispatcher.Invoke(() => GetResultText?.Invoke() ?? "(no result available)"));

            var convo = new AgenticConversation(caller, tools);
            convo.ToolInvoked += (call, result) =>
                Dispatcher.Invoke(() => AppendSystemLine($"🔧 {call.Name} → {Shorten(result)}"));

            var agentHistory = new List<AgentMessage>(_history.Count);
            foreach (var m in _history)
                agentHistory.Add(m.Role == AiRole.User
                    ? AgentMessage.User(m.Text)
                    : AgentMessage.Assistant(m.Text, Array.Empty<AgentToolCall>()));

            _cts = new CancellationTokenSource();
            try
            {
                var final = await convo.RunAsync(agentHistory, _cts.Token);
                _lastReply = final ?? string.Empty;
                AppendMessage("Claude", _lastReply, Brushes.SeaGreen);
                if (_lastReply.Length > 0)
                {
                    _history.Add(ChatMessage.FromAssistant(_lastReply));
                    InsertReplyButton.IsEnabled = true;
                }
            }
            catch (OperationCanceledException)
            {
                AppendSystemLine("[cancelled]");
            }
            catch (Exception ex)
            {
                AppendSystemLine("[error] " + ex.Message);
            }
        }

        // Approval gate for agent edits: unless "Auto-apply" is ticked, the user confirms each one.
        private string ApplyAgentEdit(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "Nothing to insert: the text was empty.";

            if (AutoApplyCheck.IsChecked != true)
            {
                var preview = text.Length <= 400 ? text : text[..400] + "…";
                var answer = MessageBox.Show(
                    "The assistant wants to add this to your worksheet:\n\n" + preview + "\n\nApply it?",
                    "AI edit — approve?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    AppendSystemLine("✗ edit declined");
                    return "The user declined this edit. Do not retry it.";
                }
            }
            InsertRequested?.Invoke(text);
            AppendSystemLine("✓ edit applied");
            return "Applied to the worksheet.";
        }

        private string SelectedModel()
        {
            if (ModelBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "claude-opus-5-5";
        }

        private void SetBusy(bool busy)
        {
            SendButton.IsEnabled = !busy;
            PromptBox.IsEnabled = !busy;
        }

        // ---- Attachments ----

        private void AttachPdfButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Multiselect = true };
            if (dlg.ShowDialog() != true)
                return;

            foreach (var f in dlg.FileNames)
            {
                try
                {
                    var bytes = File.ReadAllBytes(f);
                    if (bytes.Length > 28 * 1024 * 1024)
                    {
                        AppendSystemLine($"{Path.GetFileName(f)} is too large (>28 MB).");
                        continue;
                    }
                    _pendingAttachments.Add(ChatAttachment.Pdf(bytes, Path.GetFileName(f)));
                }
                catch (Exception ex)
                {
                    AppendSystemLine("Could not attach: " + ex.Message);
                }
            }
            UpdateAttachmentsLabel();
        }

        private void AddClipboardImage()
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img is null)
                    return;

                using var ms = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                enc.Save(ms);
                _pendingAttachments.Add(ChatAttachment.Image(ms.ToArray(), "image/png", "pasted.png"));
                UpdateAttachmentsLabel();
                AppendSystemLine("📎 image pasted");
            }
            catch (Exception ex)
            {
                AppendSystemLine("Could not paste image: " + ex.Message);
            }
        }

        private void ClearAttachmentsButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingAttachments.Clear();
            UpdateAttachmentsLabel();
        }

        private void UpdateAttachmentsLabel()
        {
            if (_pendingAttachments.Count == 0)
            {
                AttachmentsLabel.Visibility = Visibility.Collapsed;
                ClearAttachmentsButton.Visibility = Visibility.Collapsed;
                return;
            }
            AttachmentsLabel.Text = "📎 " + string.Join(", ", _pendingAttachments
                .ConvertAll(a => string.IsNullOrEmpty(a.FileName) ? a.Kind.ToString() : a.FileName));
            AttachmentsLabel.Visibility = Visibility.Visible;
            ClearAttachmentsButton.Visibility = Visibility.Visible;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _history.Clear();
            _historyStore.Clear();
            ChatDoc.Blocks.Clear();
            _pendingAttachments.Clear();
            UpdateAttachmentsLabel();
            _lastReply = string.Empty;
            InsertReplyButton.IsEnabled = false;
            AppendSystemLine("Conversation cleared.");
        }

        // ---- Chat rendering (markdown) ----

        private Paragraph _streamParagraph;
        private Run _streamRun;

        private void AddHeader(string sender, Brush brush)
        {
            var p = new Paragraph(new Bold(new Run(sender) { Foreground = brush }))
            {
                Margin = new Thickness(0, 8, 0, 2)
            };
            ChatDoc.Blocks.Add(p);
        }

        private void AppendMessage(string sender, string text, Brush brush)
        {
            AddHeader(sender, brush);
            foreach (var b in MarkdownFlow.ToBlocks(text))
                ChatDoc.Blocks.Add(b);
            ChatBox.ScrollToEnd();
        }

        private void AppendSystemLine(string text)
        {
            ChatDoc.Blocks.Add(new Paragraph(new Run(text)
            {
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray
            })
            { Margin = new Thickness(0, 2, 0, 2) });
            ChatBox.ScrollToEnd();
        }

        private void BeginStream()
        {
            AddHeader("Claude", Brushes.SeaGreen);
            _streamRun = new Run(string.Empty);
            _streamParagraph = new Paragraph(_streamRun)
            {
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = Brushes.DimGray
            };
            ChatDoc.Blocks.Add(_streamParagraph);
            ChatBox.ScrollToEnd();
        }

        private void AppendStream(string chunk)
        {
            if (_streamRun is not null)
            {
                _streamRun.Text += chunk;
                ChatBox.ScrollToEnd();
            }
        }

        private void EndStream(string full)
        {
            if (_streamParagraph is not null)
            {
                ChatDoc.Blocks.Remove(_streamParagraph);
                _streamParagraph = null;
                _streamRun = null;
            }
            foreach (var b in MarkdownFlow.ToBlocks(full))
                ChatDoc.Blocks.Add(b);
            ChatBox.ScrollToEnd();
        }

        private static string Shorten(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 120 ? s : s[..117] + "…");

        // Returns the first fenced code block if present, otherwise the whole text.
        private static string ExtractCode(string reply)
        {
            const string fence = "```";
            var start = reply.IndexOf(fence, StringComparison.Ordinal);
            if (start < 0)
                return reply;

            var afterOpen = reply.IndexOf('\n', start);
            if (afterOpen < 0)
                return reply;

            var end = reply.IndexOf(fence, afterOpen, StringComparison.Ordinal);
            if (end < 0)
                return reply;

            return reply[(afterOpen + 1)..end].TrimEnd();
        }
    }
}
