using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Calcpad.AI;
using Calcpad.Core;
using Calcpad.OpenXml;
using WebViewControl;

namespace Calcpad.Avalonia
{
    public partial class MainWindow : Window
    {
        private const string NewDocument = "#md full\n";
        private const string SampleSource =
            "#md full\n" +
            "# Simply-supported beam\n" +
            "Span and load:\n" +
            "L := 6*m\n" +
            "q := 10*kN/m\n" +
            "Maximum bending moment:\n" +
            "M := q*L^2/8 | kN*m\n";

        private readonly TextEditor _editor;
        private readonly WebView _webView;
        private readonly TextBlock _status;
        private readonly MenuItem _snippetsMenu;
        private readonly Border _keypadHost;
        private readonly Border _formatBar;
        private readonly Border _filePanel;
        private readonly TreeView _fileTree;
        private readonly Border _optionsBar;
        private readonly Border _aiPanel;
        private TextBox? _aiKey;
        private TextBox? _aiLog;
        private TextBox? _aiInput;
        private readonly List<ChatMessage> _aiHistory = new();
        private readonly List<ChatAttachment> _aiAttachments = new();
        private readonly List<AgentMessage> _agentHistory = new();
        private IChatHistoryStore? _aiStore;
        private TextBlock? _aiAttachInfo;
        private string _lastAiReply = string.Empty;
        private bool _aiBusy;
        private bool _agentMode;
        private readonly DispatcherTimer _debounce;
        private readonly string _template;   // worksheet HTML head + CSS
        private string _lastHtml = string.Empty;
        private string? _currentFile;
        private readonly string[] _previewFiles;
        private int _previewSlot;
        private bool _ready;
        private bool _dirty;
        private bool _snippetsMenuMarkdown;
        private int _decimals = 6;
        private string? _selectedFilePath;
        // Values entered into #input fields in the preview, applied on the next render.
        private readonly List<(int line, string value)> _inputValues = new();

        // Options-bar state (mapped onto Settings in BuildSettings).
        private bool _substitute = true;
        private bool _formatEquations = true;
        private bool _complex;
        private bool _adaptivePlot = true;
        private bool _embedImages = true;     // base64 plots in the HTML (needed for the preview)
        private int _degrees;                 // 0 = deg, 1 = rad, 2 = grad
        private string _units = "m";
        private PlotSettings.ColorScales _palette = PlotSettings.ColorScales.Rainbow;

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _editor = this.FindControl<TextEditor>("Editor")!;
            _webView = this.FindControl<WebView>("WebView")!;
            _status = this.FindControl<TextBlock>("StatusText")!;
            _snippetsMenu = this.FindControl<MenuItem>("SnippetsMenu")!;
            _keypadHost = this.FindControl<Border>("KeypadHost")!;
            _formatBar = this.FindControl<Border>("FormatBar")!;
            _filePanel = this.FindControl<Border>("FilePanel")!;
            _fileTree = this.FindControl<TreeView>("FileTree")!;
            _optionsBar = this.FindControl<Border>("OptionsBar")!;
            _aiPanel = this.FindControl<Border>("AiPanel")!;
            _fileTree.DoubleTapped += (_, _) => OpenSelectedFile();
            BuildAiPanel();

            _template = LoadTemplate();
            var previewDir = Path.Combine(Path.GetTempPath(), "CalcpadAvalonia");
            Directory.CreateDirectory(previewDir);
            _previewFiles = new[] { Path.Combine(previewDir, "preview-a.html"), Path.Combine(previewDir, "preview-b.html") };
            LoadHighlighting();
            BuildKeypad();
            BuildFormatBar();
            BuildOptionsBar();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };

            _editor.Text = SampleSource;
            _editor.TextChanged += (_, _) =>
            {
                if (!_ready) return;
                _dirty = true;
                _inputValues.Clear(); // edits invalidate the #input line mapping
                UpdateTitle();
                RefreshSnippetsMenuIfModeChanged();
                ScheduleRender();
            };
            KeyDown += OnKeyDown;

            PopulateSnippetsMenu();
            UpdateTitle();

            // The embedded Chromium browser initializes asynchronously; first render once shown.
            Loaded += (_, _) => { _ready = true; RestoreLastFolder(); LoadStartupFileOrSample(); };
        }

        // Opens a .cpd passed on the command line (file association / "open with"), else keeps
        // the sample already in the editor.
        private void LoadStartupFileOrSample()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && File.Exists(args[1]))
                {
                    _currentFile = args[1];
                    _editor.Text = File.ReadAllText(_currentFile).Replace("\r\n", "\n").Replace("\r", "\n");
                    _dirty = false;
                    UpdateTitle();
                    RefreshSnippetsMenuIfModeChanged();
                }
            }
            catch { /* fall back to the sample */ }
            Render();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) { Render(); e.Handled = true; return; }
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var args = new RoutedEventArgs();
            switch (e.Key)
            {
                case Key.N: New_Click(this, args); e.Handled = true; break;
                case Key.O: Open_Click(this, args); e.Handled = true; break;
                case Key.S:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) SaveAs_Click(this, args);
                    else Save_Click(this, args);
                    e.Handled = true;
                    break;
            }
        }

        private void Decimals_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
        {
            _decimals = (int)(e.NewValue ?? 6m);
            if (_ready) Render();
        }

        private void UpdateTitle()
        {
            var name = _currentFile is null ? "untitled" : Path.GetFileName(_currentFile);
            Title = $"CalcpadCE (Avalonia) — {name}{(_dirty ? " *" : string.Empty)}";
        }

        // ---- Editor services ----

        private void LoadHighlighting()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Calcpad.xshd");
                if (!File.Exists(path)) return;
                using var reader = new XmlTextReader(path);
                _editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch { /* highlighting is optional */ }
        }

        private void ScheduleRender()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // Inserts text at the caret. A '‸' marks where the caret should land; any selected text
        // is placed at that marker (so e.g. "**‸**" wraps the selection in bold).
        private void InsertAtCaret(string text)
        {
            var mark = text.IndexOf('‸');
            var selected = _editor.SelectedText ?? string.Empty;

            string clean;
            int caretRel;
            if (mark >= 0)
            {
                clean = text.Substring(0, mark) + selected + text.Substring(mark + 1);
                caretRel = mark + selected.Length;
            }
            else
            {
                clean = text;
                caretRel = text.Length;
            }

            int start;
            if (_editor.SelectionLength > 0)
            {
                start = _editor.SelectionStart;
                _editor.Document.Replace(start, _editor.SelectionLength, clean);
            }
            else
            {
                start = _editor.CaretOffset;
                _editor.Document.Insert(start, clean);
            }
            _editor.CaretOffset = start + caretRel;
            _editor.Focus();
            ScheduleRender();
        }

        // ---- Keypad ----

        private void BuildKeypad()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

            root.Children.Add(KeyGroup("Numbers",
                Kp("7", "7"), Kp("8", "8"), Kp("9", "9"), Kp("4", "4"), Kp("5", "5"), Kp("6", "6"),
                Kp("1", "1"), Kp("2", "2"), Kp("3", "3"), Kp("0", "0"), Kp(".", ".")));

            root.Children.Add(KeyGroup("Operators",
                Kp("+", " + "), Kp("−", " - "), Kp("·", "*"), Kp("/", "/"), Kp("^", "^"),
                // '=' is handled specially (markdown-first assignment is ':=').
                ("=", (Action)(() => InsertAtCaret(CurrentMarkdownFull() ? " := " : " = "))),
                Kp("≠", " ≠ "), Kp("≤", " ≤ "), Kp("≥", " ≥ "), Kp("(", "("), Kp(")", ")"), Kp("|", " | ")));

            root.Children.Add(KeyGroup("Functions",
                Kp("sin", "sin(‸)"), Kp("cos", "cos(‸)"), Kp("tan", "tan(‸)"),
                Kp("asin", "asin(‸)"), Kp("acos", "acos(‸)"), Kp("atan", "atan(‸)"),
                Kp("√", "sqrt(‸)"), Kp("ln", "ln(‸)"), Kp("log", "log(‸)"),
                Kp("exp", "exp(‸)"), Kp("abs", "abs(‸)"), Kp("round", "round(‸)")));

            root.Children.Add(KeyGroup("Greek",
                Kp("π", "π"), Kp("α", "α"), Kp("β", "β"), Kp("γ", "γ"), Kp("δ", "δ"), Kp("ε", "ε"),
                Kp("θ", "θ"), Kp("λ", "λ"), Kp("μ", "μ"), Kp("ρ", "ρ"), Kp("σ", "σ"), Kp("φ", "φ"), Kp("ω", "ω")));

            _keypadHost.Child = root;
        }

        // A keypad key: a label and the action it performs (insert its text by default).
        private (string label, Action action) Kp(string label, string insert)
            => (label, () => InsertAtCaret(insert));

        private Control KeyGroup(string title, params (string label, Action action)[] keys)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, action) in keys)
                panel.Children.Add(MakeButton(label, action));

            var header = new TextBlock
            {
                Text = title,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                Width = 72,
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(header);
            row.Children.Add(panel);
            return row;
        }

        private static Button MakeButton(string label, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 34,
                Margin = new global::Avalonia.Thickness(2),
                Padding = new global::Avalonia.Thickness(6, 3),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        private void ToggleKeypad_Click(object? sender, RoutedEventArgs e)
            => _keypadHost.IsVisible = !_keypadHost.IsVisible;

        private void ToggleFiles_Click(object? sender, RoutedEventArgs e)
            => _filePanel.IsVisible = !_filePanel.IsVisible;

        private void ToggleAi_Click(object? sender, RoutedEventArgs e)
            => _aiPanel.IsVisible = !_aiPanel.IsVisible;

        // ---- AI assistant (Anthropic streaming chat) ----

        private void BuildAiPanel()
        {
            var root = new DockPanel();

            var header = new TextBlock { Text = "AI Assistant", FontWeight = FontWeight.SemiBold, Margin = new global::Avalonia.Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _aiKey = new TextBox
            {
                Watermark = "ANTHROPIC_API_KEY",
                PasswordChar = '•',
                Text = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty,
                Margin = new global::Avalonia.Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(_aiKey, Dock.Top);
            root.Children.Add(_aiKey);

            // Mode row: agent toggle + attach.
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new global::Avalonia.Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(modeRow, Dock.Top);
            var agent = new CheckBox { Content = "Agent mode", IsChecked = _agentMode, VerticalAlignment = VerticalAlignment.Center };
            agent.IsCheckedChanged += (_, _) => _agentMode = agent.IsChecked == true;
            ToolTip.SetTip(agent, "Let the assistant read, evaluate and insert into the worksheet via tools");
            var attach = new Button { Content = "Attach…" };
            attach.Click += (_, _) => AiAttach();
            _aiAttachInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), FontSize = 11 };
            modeRow.Children.Add(agent);
            modeRow.Children.Add(attach);
            modeRow.Children.Add(_aiAttachInfo);
            root.Children.Add(modeRow);

            // Input + buttons at the bottom.
            var bottom = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _aiInput = new TextBox { Watermark = "Ask about your worksheet…", AcceptsReturn = true, MinHeight = 60, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var send = new Button { Content = "Send" };
            send.Click += (_, _) => AiSend();
            var insert = new Button { Content = "Insert reply" };
            insert.Click += (_, _) => { if (!string.IsNullOrEmpty(_lastAiReply)) InsertAtCaret(_lastAiReply); };
            var clear = new Button { Content = "Clear" };
            clear.Click += (_, _) => AiClear();
            buttons.Children.Add(send);
            buttons.Children.Add(insert);
            buttons.Children.Add(clear);
            bottom.Children.Add(_aiInput);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

            // Conversation log fills the middle (last child), with its own scrollbar.
            _aiLog = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new global::Avalonia.Thickness(0, 6, 0, 6),
                FontSize = 12
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_aiLog, ScrollBarVisibility.Auto);
            root.Children.Add(_aiLog);

            _aiPanel.Child = root;

            // Persistent memory: reload the previous conversation.
            var storePath = Path.Combine(Path.GetDirectoryName(StateFile)!, "ai-history.json");
            _aiStore = new JsonChatHistoryStore(storePath);
            foreach (var m in _aiStore.Load())
            {
                _aiHistory.Add(m);
                AppendAi($"{(m.Role == AiRole.User ? "You" : "AI")}: {m.Text}\n\n");
            }
        }

        private void AiClear()
        {
            _aiHistory.Clear();
            _agentHistory.Clear();
            _aiAttachments.Clear();
            UpdateAttachInfo();
            _aiStore?.Clear();
            if (_aiLog is not null) _aiLog.Text = string.Empty;
        }

        private async void AiAttach()
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Attach an image or PDF",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Images & PDF") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.pdf" } }
                    }
                });
                foreach (var f in files)
                {
                    var path = f.Path.LocalPath;
                    var bytes = await File.ReadAllBytesAsync(path);
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    _aiAttachments.Add(ext == ".pdf"
                        ? ChatAttachment.Pdf(bytes, Path.GetFileName(path))
                        : ChatAttachment.Image(bytes, ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png" }, Path.GetFileName(path)));
                }
                UpdateAttachInfo();
            }
            catch (Exception ex) { AppendAi($"[attach failed: {ex.Message}]\n"); }
        }

        private void UpdateAttachInfo()
        {
            if (_aiAttachInfo is not null)
                _aiAttachInfo.Text = _aiAttachments.Count == 0 ? string.Empty : $"{_aiAttachments.Count} attached";
        }

        private async void AiSend()
        {
            if (_aiBusy || _aiInput is null || _aiLog is null || _aiKey is null) return;
            var prompt = _aiInput.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) return;
            var key = _aiKey.Text?.Trim();
            if (string.IsNullOrEmpty(key)) { AppendAi("⚠ Set your Anthropic API key (or the ANTHROPIC_API_KEY env var).\n"); return; }

            _aiBusy = true;
            _aiInput.Text = string.Empty;
            AppendAi($"You: {prompt}\n");
            var options = new AiOptions { ApiKey = key, SystemPrompt = CalcpadSystemPrompt.Default };

            try
            {
                if (_agentMode)
                    await RunAgentAsync(prompt, options);
                else
                    await RunChatAsync(prompt, options);
            }
            catch (Exception ex) { AppendAi($"\n[error: {ex.Message}]"); }

            _aiStore?.Save(_aiHistory);
            AppendAi("\n\n");
            _aiBusy = false;
        }

        // Plain streaming chat (optionally multimodal with attachments).
        private async Task RunChatAsync(string prompt, AiOptions options)
        {
            var userText = $"Current worksheet:\n```\n{_editor.Text}\n```\n\n{prompt}";
            _aiHistory.Add(_aiAttachments.Count > 0
                ? ChatMessage.FromUser(userText, _aiAttachments.ToList())
                : ChatMessage.FromUser(userText));
            _aiAttachments.Clear();
            UpdateAttachInfo();

            AppendAi("\nAI: ");
            var sb = new StringBuilder();
            var client = new AnthropicAiClient(options);
            await foreach (var token in client.StreamReplyAsync(_aiHistory))
            {
                sb.Append(token);
                AppendAi(token);
            }
            _lastAiReply = sb.ToString();
            _aiHistory.Add(ChatMessage.FromAssistant(_lastAiReply));
        }

        // Agent mode: the assistant can read, evaluate and insert into the worksheet via tools.
        private async Task RunAgentAsync(string prompt, AiOptions options)
        {
            var tools = CalcpadAgentTools.Create(
                getWorksheet: () => _editor.Text ?? string.Empty,
                insertText: text => { InsertAtCaret(text); return "Inserted at the caret."; },
                evaluate: EvaluateForAssistant,
                getResult: () => _lastHtml);

            var caller = new AnthropicModelCaller(options);
            var conv = new AgenticConversation(caller, tools);
            conv.ToolInvoking += call => AppendAi($"\n[tool: {call.Name}] ");
            conv.AssistantText += t => AppendAi(t);

            _agentHistory.Add(AgentMessage.User(prompt));
            AppendAi("\nAI: ");
            var reply = await conv.RunAsync(_agentHistory);
            _agentHistory.Add(AgentMessage.Assistant(reply, Array.Empty<AgentToolCall>()));
            _lastAiReply = reply;
            _aiHistory.Add(ChatMessage.FromUser(prompt));
            _aiHistory.Add(ChatMessage.FromAssistant(reply));
        }

        // Evaluates a Calcpad snippet and returns a plain-text result for the agent to inspect.
        private string EvaluateForAssistant(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "(empty)";
            try
            {
                var macro = new MacroParser { Include = static (_, _) => string.Empty };
                macro.Parse(source, out var unwrapped, null, 0, false);
                var parser = new ExpressionParser { Settings = BuildSettings() };
                parser.Parse(unwrapped ?? source, true, false);
                return parser.HtmlResult ?? "(no result)";
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        private void AppendAi(string text)
        {
            if (_aiLog is null) return;
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => AppendAi(text)); return; }
            _aiLog.Text += text;
            _aiLog.CaretIndex = _aiLog.Text?.Length ?? 0; // keep the latest visible
        }

        // ---- File browser ----

        private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Open a folder of Calcpad worksheets",
                    AllowMultiple = false
                });
                if (folders.Count == 0) return;
                var dir = folders[0].Path.LocalPath;
                if (LoadFolder(dir))
                    SaveLastFolder(dir); // remember it for next launch
            }
            catch (Exception ex) { _status.Text = "Folder failed: " + ex.Message; }
        }

        private bool LoadFolder(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            _fileTree.ItemsSource = null;
            _fileTree.Items.Clear();
            var root = BuildFileNode(new DirectoryInfo(dir));
            if (root is null) return false;
            root.IsExpanded = true;
            _fileTree.Items.Add(root);
            return true;
        }

        // ---- Persisted app state (last folder) ----

        private static string StateFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CalcpadAvalonia", "state.json");

        private void SaveLastFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(new AppState { LastFolder = dir }));
            }
            catch { /* best effort */ }
        }

        private void RestoreLastFolder()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile));
                if (state?.LastFolder is string dir && Directory.Exists(dir))
                    LoadFolder(dir);
            }
            catch { /* ignore a corrupt state file */ }
        }

        private sealed class AppState
        {
            public string? LastFolder { get; set; }
        }

        // Builds a TreeViewItem for a directory: sub-folders first, then *.cpd files.
        private static TreeViewItem? BuildFileNode(DirectoryInfo dir)
        {
            TreeViewItem node;
            try
            {
                node = new TreeViewItem { Header = dir.Name };
                foreach (var sub in dir.GetDirectories().OrderBy(d => d.Name))
                {
                    if ((sub.Attributes & FileAttributes.Hidden) != 0) continue;
                    var child = BuildFileNode(sub);
                    if (child is not null) node.Items.Add(child);
                }
                foreach (var file in dir.GetFiles("*.cpd").OrderBy(f => f.Name))
                    node.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
            }
            catch { return null; } // unreadable folders are skipped
            return node;
        }

        // Selecting a file only tracks it; double-click opens it, the Insert button includes it.
        private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _selectedFilePath = (_fileTree.SelectedItem as TreeViewItem)?.Tag as string;
        }

        private async void OpenSelectedFile()
        {
            if (_selectedFilePath is not string path) return;
            try
            {
                _currentFile = path;
                _editor.Text = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace("\r", "\n");
                _dirty = false;
                UpdateTitle();
                RefreshSnippetsMenuIfModeChanged();
                Render();
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        // Inserts an #include directive for the selected file at the caret (like the WPF library).
        private void InsertFile_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedFilePath is not string path) { _status.Text = "Select a file first"; return; }
            InsertAtCaret($"#include {path}\n");
        }

        // ---- Options bar ----

        private void BuildOptionsBar()
        {
            var bar = new WrapPanel { Orientation = Orientation.Horizontal };

            CheckBox Check(string label, bool initial, Action<bool> set)
            {
                var cb = new CheckBox
                {
                    Content = label,
                    IsChecked = initial,
                    Margin = new global::Avalonia.Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); if (_ready) Render(); };
                return cb;
            }

            bar.Children.Add(Check("Substitute", _substitute, v => _substitute = v));
            bar.Children.Add(Check("Format equations", _formatEquations, v => _formatEquations = v));
            bar.Children.Add(Check("Complex", _complex, v => _complex = v));
            bar.Children.Add(Check("Adaptive plots", _adaptivePlot, v => _adaptivePlot = v));
            var embed = Check("Embed images", _embedImages, v => _embedImages = v);
            ToolTip.SetTip(embed, "Embed plots/images in the HTML as base64; save to files if unchecked");
            bar.Children.Add(embed);

            bar.Children.Add(LabeledCombo("Angles", new[] { "Deg", "Rad", "Grad" }, _degrees, i => _degrees = i));
            bar.Children.Add(LabeledCombo("Units", new[] { "m", "cm", "mm" }, UnitsIndex(_units), i => _units = i switch { 1 => "cm", 2 => "mm", _ => "m" }));

            var palettes = Enum.GetNames<PlotSettings.ColorScales>();
            bar.Children.Add(LabeledCombo("Palette", palettes, Array.IndexOf(palettes, _palette.ToString()),
                i => _palette = Enum.Parse<PlotSettings.ColorScales>(palettes[i])));

            _optionsBar.Child = bar;
        }

        private static int UnitsIndex(string u) => u switch { "cm" => 1, "mm" => 2, _ => 0 };

        private Control LabeledCombo(string label, string[] items, int selected, Action<int> set)
        {
            var combo = new ComboBox { MinWidth = 100, VerticalAlignment = VerticalAlignment.Center };
            foreach (var it in items) combo.Items.Add(new ComboBoxItem { Content = it });
            combo.SelectedIndex = selected < 0 ? 0 : selected; // after items are added
            combo.SelectionChanged += (_, _) => { set(combo.SelectedIndex); if (_ready) Render(); };
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new global::Avalonia.Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            wrap.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new global::Avalonia.Thickness(0, 0, 4, 0), Foreground = new SolidColorBrush(Color.Parse("#4B5563")) });
            wrap.Children.Add(combo);
            return wrap;
        }

        // ---- Formatting toolbar (markdown-aware) ----

        private void BuildFormatBar()
        {
            var bar = new WrapPanel { Orientation = Orientation.Horizontal };

            void Add(string label, string md, string classic, string? tip = null)
            {
                var b = MakeButton(label, () => InsertAtCaret(CurrentMarkdownFull() ? md : classic));
                b.MinWidth = 32;
                if (tip is not null) ToolTip.SetTip(b, tip);
                bar.Children.Add(b);
            }

            Add("H1", "# ‸", "<h1>‸</h1>", "Heading 1");
            Add("H2", "## ‸", "<h2>‸</h2>", "Heading 2");
            Add("H3", "### ‸", "<h3>‸</h3>", "Heading 3");
            bar.Children.Add(new Border { Width = 10 });
            Add("B", "**‸**", "<strong>‸</strong>", "Bold");
            Add("I", "*‸*", "<em>‸</em>", "Italic");
            Add("S", "~~‸~~", "<del>‸</del>", "Strikethrough");
            bar.Children.Add(new Border { Width = 10 });
            Add("• List", "- ‸", "<ul><li>‸</li></ul>", "Bullet list");
            Add("Table", "| A | B |\n| --- | --- |\n| ‸ |  |", "<table><tr><td>‸</td></tr></table>", "Table");
            Add("❝ Quote", "> ‸", "<blockquote>‸</blockquote>", "Quote");
            bar.Children.Add(new Border { Width = 10 });
            Add("$x$", "$‸$", "‸", "Inline math");
            Add("$$", "$$ ‸ $$", "‸", "Block math");
            Add("Rule", "\n---\n", "<hr/>", "Horizontal rule");

            _formatBar.Child = bar;
        }

        // ---- Snippets (mode-aware) ----

        private void PopulateSnippetsMenu()
        {
            _snippetsMenuMarkdown = CurrentMarkdownFull();
            _snippetsMenu.Items.Clear();
            foreach (var snippet in CalcpadSnippets.ForMode(_snippetsMenuMarkdown))
            {
                var body = snippet.Body;
                var item = new MenuItem { Header = snippet.Title };
                item.Click += (_, _) => InsertAtCaret(body);
                _snippetsMenu.Items.Add(item);
            }
        }

        private void RefreshSnippetsMenuIfModeChanged()
        {
            if (CurrentMarkdownFull() != _snippetsMenuMarkdown)
                PopulateSnippetsMenu();
        }

        // Whether the worksheet is currently in markdown-first mode (#md full): the last #md
        // directive in the source, else markdown-first by default (new docs start with #md full).
        private bool CurrentMarkdownFull()
        {
            var text = _editor?.Text;
            if (string.IsNullOrEmpty(text)) return false;
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t[0] != '#') continue;
                if (t.StartsWith("#md", StringComparison.OrdinalIgnoreCase))
                {
                    var arg = t.Substring(3).Trim();
                    if (arg.Equals("full", StringComparison.OrdinalIgnoreCase)) return true;
                    if (arg.Length == 0 || arg.Equals("on", StringComparison.OrdinalIgnoreCase)
                        || arg.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            return false; // no #md directive → classic nomenclature
        }

        // ---- Rendering ----

        private void Run_Click(object? sender, RoutedEventArgs e) => Render();

        // Loads the quick-reference help page into the preview pane.
        private void ShowHelp_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "help.html");
                if (File.Exists(path))
                {
                    _webView.LoadHtml(File.ReadAllText(path));
                    _status.Text = "Quick reference — View ▸ Show preview to return";
                }
                else _status.Text = "Help page not found";
            }
            catch (Exception ex) { _status.Text = "Help failed: " + ex.Message; }
        }

        private void Render()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                _lastHtml = RenderHtml(_editor.Text ?? string.Empty);
                sw.Stop();

                // Write to a toggled file and navigate to it so the page has a file:// origin:
                // this lets relative <img src="./…"> in the worksheet resolve (via the base href)
                // and load, which an about:/data: LoadHtml page cannot do.
                _previewSlot ^= 1;
                var path = _previewFiles[_previewSlot];
                File.WriteAllText(path, BuildPage(_lastHtml), new UTF8Encoding(false));
                _webView.LoadUrl(new Uri(path).AbsoluteUri);

                _status.Text = $"{(_currentFile is null ? "untitled" : Path.GetFileName(_currentFile))} · rendered in {sw.ElapsedMilliseconds} ms";
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
            }
        }

        private string RenderHtml(string source)
        {
            var macro = new MacroParser { Include = static (_, _) => string.Empty };
            macro.Parse(source, out var unwrapped, null, 0, false);

            var code = unwrapped ?? source;
            if (_inputValues.Count > 0)
                code = ApplyInputValues(code, _inputValues);

            var parser = new ExpressionParser { Settings = BuildSettings() };
            parser.Parse(code, true, false);
            return parser.HtmlResult ?? string.Empty;
        }

        // Injects interactive #input values back into the source by line (ported from the CLI
        // PreviewServer), so the parser recalculates with the entered numbers.
        private static string ApplyInputValues(string source, List<(int line, string value)> inputs)
        {
            var byLine = new Dictionary<int, Queue<string>>();
            foreach (var (line, value) in inputs)
            {
                if (line <= 0) continue;
                if (!byLine.TryGetValue(line, out var queue))
                    byLine[line] = queue = new Queue<string>();
                queue.Enqueue((value ?? string.Empty).Trim());
            }
            if (byLine.Count == 0) return source;

            var lines = source.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Length; ++i)
            {
                if (byLine.TryGetValue(i + 1, out var queue) && queue.Count != 0)
                {
                    sb.Clear();
                    if (MacroParser.SetLineInputFields(lines[i].TrimEnd(), sb, queue, true))
                        lines[i] = sb.ToString();
                }
            }
            return string.Join('\n', lines);
        }

        // Reads the #input field values from the preview DOM and recalculates with them.
        private async void RecalcInputs_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                const string js =
                    "JSON.stringify(Array.prototype.slice.call(document.getElementsByTagName('input'))" +
                    ".filter(function(el){return /(^|\\s)input-\\d+/.test(el.className);})" +
                    ".map(function(el){var m=el.className.match(/input-(\\d+)/);" +
                    "return {line: parseInt(m[1],10), value: el.value};}))";
                var json = await _webView.EvaluateScript<string>(js);
                _inputValues.Clear();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var el in doc.RootElement.EnumerateArray())
                        _inputValues.Add((el.GetProperty("line").GetInt32(),
                                          el.GetProperty("value").GetString() ?? string.Empty));
                }
                if (_inputValues.Count == 0) { _status.Text = "No #input fields in the preview"; return; }
                Render();
                _status.Text = $"Recalculated with {_inputValues.Count} input value(s)";
            }
            catch (Exception ex) { _status.Text = "Inputs failed: " + ex.Message; }
        }

        private Settings BuildSettings()
        {
            // Seed Off (the engine default); a #md on|full directive in the source switches it on.
            // Forcing Full would make classic worksheets (no directive) render as prose.
            var settings = new Settings { Units = _units, Markdown = MarkdownMode.Off };
            settings.Math.Decimals = _decimals;
            settings.Math.Substitute = _substitute;
            settings.Math.FormatEquations = _formatEquations;
            settings.Math.IsComplex = _complex;
            settings.Math.Degrees = _degrees;
            settings.Plot.IsAdaptive = _adaptivePlot;
            settings.Plot.ColorScale = _palette;
            if (_embedImages)
            {
                settings.Plot.ImagePath = string.Empty;   // empty ⇒ plots embedded as base64
                settings.Plot.ImageUri = string.Empty;
            }
            else
            {
                var dir = Path.Combine(Path.GetTempPath(), "CalcpadAvalonia", "img");
                Directory.CreateDirectory(dir);
                settings.Plot.ImagePath = dir;
                settings.Plot.ImageUri = "file:///" + dir.Replace('\\', '/') + "/";
            }
            return settings;
        }

        private string BuildPage(string bodyHtml)
        {
            // Resolve the worksheet's relative image/resource paths against its own folder.
            var template = _template;
            var dir = _currentFile is null ? null : Path.GetDirectoryName(_currentFile);
            if (!string.IsNullOrEmpty(dir))
            {
                var baseHref = new Uri(dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar).AbsoluteUri;
                var baseTag = $"<base href=\"{baseHref}\">";
                var headIdx = template.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                template = headIdx >= 0
                    ? template.Insert(headIdx + "<head>".Length, "\n    " + baseTag)
                    : baseTag + template;
            }

            var page = new StringBuilder(template.Length + bodyHtml.Length + 32);
            page.Append(template);
            page.Append(bodyHtml);
            page.Append(" </body></html>");
            return page.ToString();
        }

        // ---- File commands ----

        private void New_Click(object? sender, RoutedEventArgs e)
        {
            _currentFile = null;
            _editor.Text = NewDocument;
            _editor.CaretOffset = _editor.Document.TextLength;
            _dirty = false;
            UpdateTitle();
            PopulateSnippetsMenu();
            Render();
        }

        private async void Open_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Calcpad worksheet",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Calcpad") { Patterns = new[] { "*.cpd", "*.txt" } },
                        FilePickerFileTypes.All
                    }
                });
                if (files.Count == 0) return;
                _currentFile = files[0].Path.LocalPath;
                _editor.Text = (await File.ReadAllTextAsync(_currentFile)).Replace("\r\n", "\n").Replace("\r", "\n");
                _dirty = false;
                UpdateTitle();
                RefreshSnippetsMenuIfModeChanged();
                Render();
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        private async void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFile)) { await SaveAs(); return; }
            await SaveTo(_currentFile);
        }

        private async void SaveAs_Click(object? sender, RoutedEventArgs e) => await SaveAs();

        private async System.Threading.Tasks.Task SaveAs()
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Calcpad worksheet",
                    DefaultExtension = "cpd",
                    SuggestedFileName = _currentFile is null ? "worksheet.cpd" : Path.GetFileName(_currentFile),
                    FileTypeChoices = new[] { new FilePickerFileType("Calcpad") { Patterns = new[] { "*.cpd" } } }
                });
                if (file is null) return;
                _currentFile = file.Path.LocalPath;
                await SaveTo(_currentFile);
            }
            catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
        }

        private async System.Threading.Tasks.Task SaveTo(string path)
        {
            await File.WriteAllTextAsync(path, _editor.Text ?? string.Empty);
            _dirty = false;
            UpdateTitle();
            _status.Text = "Saved " + Path.GetFileName(path);
        }

        // Opens the styled page in the default browser (also the cross-platform Print/PDF path:
        // the browser's Print dialog offers "Save as PDF" on Windows, macOS and Linux).
        private void OpenInBrowser_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "calcpad-preview.html");
                File.WriteAllText(path, BuildPage(_lastHtml), new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        private async void ExportHtml_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export as HTML",
                    DefaultExtension = "html",
                    SuggestedFileName = SuggestedName("html"),
                    FileTypeChoices = new[] { new FilePickerFileType("HTML") { Patterns = new[] { "*.html" } } }
                });
                if (file is null) return;
                await File.WriteAllTextAsync(file.Path.LocalPath, BuildPage(_lastHtml), new UTF8Encoding(false));
                _status.Text = "Exported " + Path.GetFileName(file.Path.LocalPath);
            }
            catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
        }

        private async void ExportDocx_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export as Word document",
                    DefaultExtension = "docx",
                    SuggestedFileName = SuggestedName("docx"),
                    FileTypeChoices = new[] { new FilePickerFileType("Word") { Patterns = new[] { "*.docx" } } }
                });
                if (file is null) return;

                // DOCX needs the OpenXml expression list, produced when parsing with getXml = true.
                var macro = new MacroParser { Include = static (_, _) => string.Empty };
                macro.Parse(_editor.Text ?? string.Empty, out var unwrapped, null, 0, false);
                var parser = new ExpressionParser { Settings = BuildSettings() };
                parser.Parse(unwrapped ?? string.Empty, true, true);

                new OpenXmlWriter(parser.OpenXmlExpressions).Convert(parser.HtmlResult, file.Path.LocalPath);
                _status.Text = "Exported " + Path.GetFileName(file.Path.LocalPath);
            }
            catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
        }

        private string SuggestedName(string ext)
        {
            var baseName = _currentFile is null ? "worksheet" : Path.GetFileNameWithoutExtension(_currentFile);
            return baseName + "." + ext;
        }

        private static string LoadTemplate()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "template.html");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch { /* fall back to a minimal shell */ }
            return "<!DOCTYPE html><html><head><meta charset=\"UTF-8\"></head><body>";
        }
    }
}
