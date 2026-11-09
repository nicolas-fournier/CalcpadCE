using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Avalonia.Controls;
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
        private readonly DispatcherTimer _debounce;
        private readonly string _template;   // worksheet HTML head + CSS
        private string _lastHtml = string.Empty;
        private string? _currentFile;
        private bool _ready;
        private bool _dirty;
        private bool _snippetsMenuMarkdown;
        private int _decimals = 6;

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

            _template = LoadTemplate();
            LoadHighlighting();
            BuildKeypad();
            BuildFormatBar();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };

            _editor.Text = SampleSource;
            _editor.TextChanged += (_, _) =>
            {
                if (!_ready) return;
                _dirty = true;
                UpdateTitle();
                RefreshSnippetsMenuIfModeChanged();
                ScheduleRender();
            };
            KeyDown += OnKeyDown;

            PopulateSnippetsMenu();
            UpdateTitle();

            // The embedded Chromium browser initializes asynchronously; first render once shown.
            Loaded += (_, _) => { _ready = true; Render(); };
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
                _fileTree.ItemsSource = null;
                _fileTree.Items.Clear();
                var root = BuildFileNode(new DirectoryInfo(dir));
                if (root is not null)
                {
                    root.IsExpanded = true;
                    _fileTree.Items.Add(root);
                }
            }
            catch (Exception ex) { _status.Text = "Folder failed: " + ex.Message; }
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

        private async void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_fileTree.SelectedItem is not TreeViewItem item || item.Tag is not string path) return;
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
            if (string.IsNullOrEmpty(text)) return true;
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
            return true;
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
                _webView.LoadHtml(BuildPage(_lastHtml));
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

            var parser = new ExpressionParser { Settings = BuildSettings() };
            parser.Parse(unwrapped ?? source, true, false);
            return parser.HtmlResult ?? string.Empty;
        }

        private Settings BuildSettings()
        {
            var settings = new Settings();
            settings.Math.Decimals = _decimals;
            settings.Markdown = MarkdownMode.Full; // a #md directive in the source overrides this
            return settings;
        }

        private string BuildPage(string bodyHtml)
        {
            var page = new StringBuilder(_template.Length + bodyHtml.Length + 32);
            page.Append(_template);
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
