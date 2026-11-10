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
using AvaloniaEdit.Document;
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
        private readonly StackPanel _tabStrip;
        private readonly List<OpenDoc> _docs = new();
        private OpenDoc? _active;
        private bool _switching;
        private readonly WebView _webView;
        private readonly TextBlock _status;
        private readonly MenuItem _snippetsMenu;
        private readonly Border _keypadHost;
        private WrapPanel? _keypadVars;   // the keypad's live Variables column
        private Border _tableOverlay = null!; // inline table editor floating over the code editor
        private readonly Border _formatBar;
        private readonly Border _filePanel;
        private readonly TreeView _fileTree;
        private readonly Border _optionsBar;
        private readonly ToggleButton _filesToggle;
        private readonly ToggleButton _keypadToggle;
        private readonly ToggleButton _aiToggle;
        private readonly Border _aiPanel;
        private TextBox? _aiKey;
        private global::Markdown.Avalonia.MarkdownScrollViewer? _aiLog;
        private readonly StringBuilder _aiLogText = new();
        private bool _aiRenderQueued;
        private bool _agentTextSeen;
        private TextBox? _aiInput;
        private readonly List<ChatMessage> _aiHistory = new();
        private readonly List<ChatAttachment> _aiAttachments = new();
        private readonly List<AgentMessage> _agentHistory = new();
        private IChatHistoryStore? _aiStore;
        private TextBlock? _aiAttachInfo;
        private string _lastAiReply = string.Empty;
        private bool _aiBusy;
        private bool _agentMode = true;   // agent mode on by default
        private readonly DispatcherTimer _debounce;
        private readonly string _template;   // worksheet HTML head + CSS
        private string _lastHtml = string.Empty;
        private string? _currentFile;
        private string? _packagePath;     // the open .cpdz, when editing a package (Save updates it)
        private string? _packageWorkDir;  // its extracted working folder
        private readonly string[] _previewFiles;
        private int _previewSlot;
        private bool _ready;
        private bool _dirty;
        private bool _snippetsMenuMarkdown;
        private int _decimals = 6;
        private object? _selectedTag; // selected file path (string) or FunctionRef
        // Values entered into #input fields in the preview, applied on the next render.
        private readonly List<(int line, string value)> _inputValues = new();

        // Options-bar state (mapped onto Settings in BuildSettings).
        private bool _substitute = true;
        private bool _formatEquations = true;
        private bool _complex;
        private bool _adaptivePlot = true;
        private bool _embedImages = true;     // base64 plots in the HTML (needed for the preview)
        private bool _relativePaths = true;   // insert #include/image paths relative to the worksheet
        private bool _autoCalc;               // typing 'name =' on a prose line auto-upgrades to ':='
        private bool _useTemplate;            // Word export clones a .dotx/.docx template for branding
        private CheckBox? _templateCheck;     // the "Word template" options-bar toggle (for its tooltip)
        private int _degrees;                 // 0 = deg, 1 = rad, 2 = grad
        private string _units = "m";
        private PlotSettings.ColorScales _palette = PlotSettings.ColorScales.Rainbow;

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _editor = this.FindControl<TextEditor>("Editor")!;
            _tabStrip = this.FindControl<StackPanel>("TabStrip")!;
            _webView = this.FindControl<WebView>("WebView")!;
            _status = this.FindControl<TextBlock>("StatusText")!;
            _snippetsMenu = this.FindControl<MenuItem>("SnippetsMenu")!;
            _keypadHost = this.FindControl<Border>("KeypadHost")!;
            _formatBar = this.FindControl<Border>("FormatBar")!;
            _tableOverlay = this.FindControl<Border>("TableOverlay")!;
            _filePanel = this.FindControl<Border>("FilePanel")!;
            _fileTree = this.FindControl<TreeView>("FileTree")!;
            _optionsBar = this.FindControl<Border>("OptionsBar")!;
            _filesToggle = this.FindControl<ToggleButton>("FilesToggle")!;
            _keypadToggle = this.FindControl<ToggleButton>("KeypadToggle")!;
            _aiToggle = this.FindControl<ToggleButton>("AiToggle")!;
            _aiPanel = this.FindControl<Border>("AiPanel")!;
            _fileTree.DoubleTapped += (_, _) => OpenSelectedFile();
            BuildAiPanel();

            _template = LoadTemplate();
            var previewDir = Path.Combine(Path.GetTempPath(), "CalcpadAvalonia");
            Directory.CreateDirectory(previewDir);
            _previewFiles = new[] { Path.Combine(previewDir, "preview-a.html"), Path.Combine(previewDir, "preview-b.html") };
            LoadHighlighting();
            InitHoverTooltips();
            InitCompletion();
            InitTableEditor();
            InitLineNumberToggle();
            try { _editor.TextArea.LeftMargins.Add(new CalcGutterMargin(ClassifyLineBadge, ToggleLineByOffset)); } catch { }
            BuildEditorContextMenu();
            BuildKeypad();
            BuildFormatBar();
            LoadState();
            BuildOptionsBar();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };

            _editor.TextChanged += (_, _) =>
            {
                if (!_ready || _switching) return;
                _dirty = true;
                if (_active is not null) _active.Dirty = true;
                _inputValues.Clear(); // edits invalidate the #input line mapping
                UpdateTitle();
                UpdateTabLabels();
                RefreshSnippetsMenuIfModeChanged();
                ScheduleRender();
            };
            KeyDown += OnKeyDown;

            PopulateSnippetsMenu();

            // The embedded Chromium browser initializes asynchronously; first render once shown.
            Loaded += (_, _) => { _ready = true; RestoreLastFolder(); LoadStartupFileOrSample(); };
        }

        // Opens a .cpd passed on the command line (file association / "open with") in the first
        // tab, else starts with the sample worksheet.
        private void LoadStartupFileOrSample()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && File.Exists(args[1]))
                {
                    if (args[1].EndsWith(CpdPackage.Extension, StringComparison.OrdinalIgnoreCase))
                        OpenPackage(args[1]); // a .cpdz package passed on the command line
                    else
                        OpenOrActivate(args[1], File.ReadAllText(args[1]).Replace("\r\n", "\n").Replace("\r", "\n"));
                    return;
                }
            }
            catch { /* fall back to the sample */ }
            AddTab(null, SampleSource);
        }

        // ---- Tabs (one TextDocument per open file) ----

        private sealed class OpenDoc
        {
            public string? Path;
            public TextDocument Doc = new();
            public bool Dirty;
            public string Title => Path is null ? "untitled" : System.IO.Path.GetFileName(Path);
        }

        // Opens the path in an existing tab if already open, otherwise a new tab.
        private void OpenOrActivate(string path, string text)
        {
            var existing = _docs.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { ActivateDoc(existing); return; }
            AddTab(path, text);
        }

        private void AddTab(string? path, string text)
        {
            var doc = new OpenDoc { Path = path, Doc = new TextDocument(text), Dirty = false };
            _docs.Add(doc);
            ActivateDoc(doc);
        }

        private void ActivateDoc(OpenDoc doc)
        {
            _switching = true;
            _active = doc;
            _editor.Document = doc.Doc;
            _currentFile = doc.Path;
            _dirty = doc.Dirty;
            _inputValues.Clear();
            _switching = false;

            RebuildTabs();
            UpdateTitle();
            RefreshSnippetsMenuIfModeChanged();
            if (_ready) Render();
        }

        private void CloseDoc(OpenDoc doc)
        {
            var idx = _docs.IndexOf(doc);
            if (idx < 0) return;
            _docs.Remove(doc);
            if (_docs.Count == 0) { AddTab(null, SampleSource); return; }
            if (_active == doc)
                ActivateDoc(_docs[Math.Min(idx, _docs.Count - 1)]);
            else
                RebuildTabs();
        }

        private void RebuildTabs()
        {
            _tabStrip.Children.Clear();
            foreach (var doc in _docs)
            {
                var active = doc == _active;
                var label = new TextBlock { Text = doc.Title + (doc.Dirty ? " *" : string.Empty), VerticalAlignment = VerticalAlignment.Center };
                var close = new Button { Content = "✕", Padding = new global::Avalonia.Thickness(4, 0), Background = global::Avalonia.Media.Brushes.Transparent, FontSize = 10, Margin = new global::Avalonia.Thickness(4, 0, 0, 0) };
                close.Click += (_, _) => CloseDoc(doc);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                row.Children.Add(label);
                row.Children.Add(close);
                var tab = new Border
                {
                    Child = row,
                    Padding = new global::Avalonia.Thickness(10, 4),
                    Margin = new global::Avalonia.Thickness(2, 3, 0, 0),
                    Background = active ? global::Avalonia.Media.Brushes.White : new SolidColorBrush(Color.Parse("#E5E7EB")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB")),
                    BorderThickness = new global::Avalonia.Thickness(1, 1, 1, active ? 0 : 1),
                    Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand)
                };
                var captured = doc;
                tab.PointerPressed += (_, _) => ActivateDoc(captured);
                _tabStrip.Children.Add(tab);
            }
        }

        private void UpdateTabLabels()
        {
            // Cheap: only the active tab's dirty marker changes on a keystroke.
            RebuildTabs();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) { Render(); e.Handled = true; return; }
            if (e.Key == Key.F12) { GoToDefinition(); e.Handled = true; return; }
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (e.Key is Key.OemPlus or Key.Add) { ToggleCalc(); e.Handled = true; return; } // Ctrl+= toggles calc/prose
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

        // ---- Autocompletion (snippets / functions / units / variables / directives) ----

        private AvaloniaEdit.CodeCompletion.CompletionWindow? _completion;
        private bool _completionEnabled = true;

        private void InitCompletion()
        {
            _editor.TextArea.TextEntered += OnTextEntered;
            // Make Tab accept the selected completion (Avalonia would otherwise insert a tab).
            _editor.TextArea.AddHandler(global::Avalonia.Input.InputElement.KeyDownEvent,
                (object? _, global::Avalonia.Input.KeyEventArgs e) =>
                {
                    if (_completion is not null && e.Key == Key.Tab)
                    {
                        _completion.CompletionList.RequestInsertion(e);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.V && _completion is null
                             && (e.KeyModifiers == global::Avalonia.Input.KeyModifiers.Control
                                 || e.KeyModifiers == global::Avalonia.Input.KeyModifiers.Meta))
                    {
                        // Ctrl/Cmd+V: paste an image from the clipboard as a saved <img>, else text.
                        e.Handled = true;
                        _ = HandlePasteAsync();
                    }
                },
                global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        private void OnTextEntered(object? sender, global::Avalonia.Input.TextInputEventArgs e)
        {
            if (!_completionEnabled || _completion is not null || string.IsNullOrEmpty(e.Text)) return;
            var ch = e.Text[0];
            if (ch == '=' && _autoCalc && TryAutoCalc()) return;
            if (ch is '=' or '/' or '*' or '+' or '-') { MaybeProposeCalc(); return; }
            if (!char.IsLetter(ch) && ch != '#') return;

            var caret = _editor.CaretOffset;
            // Re-open from the start of the current word, so completion resumes even mid-word
            // (e.g. after dismissing it and typing again).
            var start = caret - 1;
            if (ch != '#')
                while (start > 0 && IsWordChar(_editor.Document.GetCharAt(start - 1))) start--;

            var items = BuildCompletionItems(ch == '#');
            if (items.Count == 0) return;

            var w = new AvaloniaEdit.CodeCompletion.CompletionWindow(_editor.TextArea)
            {
                StartOffset = start,
                CloseWhenCaretAtBeginning = true
            };
            // Enable substring matching so a unit/function is also found by words in its description
            // (e.g. typing "celsius" matches the "… Celsius …" text of the °C entry).
            w.CompletionList.IsFiltering = true;
            foreach (var it in items) w.CompletionList.CompletionData.Add(it);
            w.Closed += (_, _) => _completion = null;
            _completion = w;
            w.Show();
        }

        // Completion colours by kind.
        private const string ColVar = "#C2410C";   // our defined variables/functions (orange)
        private const string ColUnit = "#16A34A";  // units (green)
        private const string ColFunc = "#1F7FAE";  // built-in functions (blue)
        private const string ColSnip = "#7C3AED";  // snippets (purple)
        private const string ColDir = "#B2379B";   // directives (magenta)

        // After inserting a variable via completion: if the line is a pure math expression (no
        // prose) and not already a calculation, wrap it in $…$ so it is evaluated. Prose lines
        // (multiple words) are left alone.
        private void ConvertLineToCalcIfExpression()
        {
            if (!CurrentMarkdownFull()) return;
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            var text = _editor.Document.GetText(line.Offset, line.Length).Trim();
            if (text.Length == 0 || text[0] == '#' || text.Contains('$')) return; // directive / already math
            // A pure expression: identifier/number operands joined only by math operators.
            if (!System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"^[\wµΩ°.]+(\s*[-+*/^().|]\s*[\wµΩ°.]+)*\s*$"))
                return; // looks like prose — leave it
            _editor.CaretOffset = line.Offset;
            _editor.SelectionLength = 0;
            ToggleCalc(); // wraps the current line in $…$
        }

        // Auto-calc: when the line is exactly "name =" (just typed), upgrade it to "name := "
        // so the user never types ':='. Reversible with one Ctrl+Z. Returns true if it fired.
        private bool TryAutoCalc()
        {
            if (!CurrentMarkdownFull()) return false;
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            var text = _editor.Document.GetText(line.Offset, line.Length);
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*[A-Za-z_]\w*\s*=\s*$")) return false;
            var upgraded = System.Text.RegularExpressions.Regex.Replace(text, @"=\s*$", ":= ");
            _editor.Document.Replace(line.Offset, line.Length, upgraded);
            _editor.CaretOffset = line.Offset + upgraded.Length;
            ScheduleRender();
            return true;
        }

        // Typing a math operator (= / * + -) on a markdown prose line proposes converting it to
        // a calculation — but only when there is an operand before the operator (so markdown
        // lists '- ' and bold '**' don't trigger it).
        private void MaybeProposeCalc()
        {
            if (!CurrentMarkdownFull()) return;
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            var full = _editor.Document.GetText(line.Offset, line.Length);
            var text = full.Trim();
            if (text.Length == 0 || text[0] == '#' || text.Contains('$')) return; // directive or already math

            // Require an operand (letter/digit/')') immediately before the just-typed operator.
            var col = _editor.CaretOffset - line.Offset - 2; // index of the char before the operator
            while (col >= 0 && full[col] == ' ') col--;
            if (col < 0) return;
            var prev = full[col];
            if (!(char.IsLetterOrDigit(prev) || prev == ')')) return;

            var w = new AvaloniaEdit.CodeCompletion.CompletionWindow(_editor.TextArea)
            {
                StartOffset = _editor.CaretOffset,
                EndOffset = _editor.CaretOffset
            };
            w.CompletionList.CompletionData.Add(new CompletionData(
                "convert", content: "↔ Convert this line to a calculation",
                description: "Wrap the line in $…$ so it is evaluated", colorHex: "#7C3AED",
                action: ToggleCalc));
            w.Closed += (_, _) => _completion = null;
            _completion = w;
            w.Show();
        }

        // Typed word → unit symbol it inserts (symbols that are awkward to type directly).
        private static readonly (string alias, string token)[] UnitAliases =
        {
            ("celsius", "°C"), ("fahrenheit", "°F"), ("kelvin", "K"), ("rankine", "°R"),
            ("degree", "°"), ("ohm", "Ω"), ("micro", "µ"), ("delta", "Δ"),
        };

        private System.Collections.Generic.List<CompletionData> BuildCompletionItems(bool directives)
        {
            var list = new System.Collections.Generic.List<CompletionData>();
            if (directives)
            {
                foreach (var d in new[] { "#md full", "#md on", "#md off", "#if", "#else if", "#else", "#end if",
                    "#def", "#end def", "#include", "#local", "#global", "#repeat", "#loop", "#for", "#while",
                    "#input", "#deg", "#rad", "#gra", "#round", "#format", "#const", "#hide", "#show" })
                    list.Add(new CompletionData(d, d + "\n", priority: 1, colorHex: ColDir));
                return list;
            }

            // Our worksheet variables/functions FIRST (highest priority, orange), with their doc.
            // Inserting one on a plain (non-calc) expression line converts the line to a calc.
            foreach (var name in CollectDefinedNames())
                list.Add(new CompletionData(name, priority: 100,
                    description: DescribeUserSymbol(name) ?? "defined in this worksheet",
                    content: name + "  —  variable", colorHex: ColVar,
                    postComplete: ConvertLineToCalcIfExpression));

            // Units (green). The filter key carries the description so a unit is also found by its
            // name (typing "celsius" surfaces °C); insertion still uses the bare unit token.
            foreach (var u in CalcpadGlossary.UnitNames)
            {
                var d = CalcpadGlossary.Describe(u) ?? "unit";
                list.Add(new CompletionData($"{u} {d}", insert: u, priority: 40,
                    description: d, content: u + "  —  unit", colorHex: ColUnit));
            }

            // Built-in functions (blue). Same — searchable by description, inserts "name(".
            foreach (var fn in CalcpadGlossary.FunctionNames)
            {
                var d = CalcpadGlossary.Describe(fn) ?? "function";
                list.Add(new CompletionData($"{fn} {d}", insert: fn + "(", priority: 30,
                    description: d, content: fn + "  —  function", colorHex: ColFunc));
            }

            // Named aliases for hard-to-type unit symbols: typing the word (prefix match) inserts the
            // symbol, e.g. "celsius" → °C, "ohm" → Ω. Reliable even without substring matching.
            foreach (var (alias, token) in UnitAliases)
                list.Add(new CompletionData(alias, insert: token, priority: 45,
                    description: $"inserts {token}", content: $"{alias}  —  inserts {token}", colorHex: ColUnit));

            // Mode-aware snippets (purple). Priority above units/functions so a snippet wins its
            // own exact trigger on Tab (e.g. 'var' snippet beats the 'VAR' volt-amp-reactive unit).
            foreach (var sn in CalcpadSnippets.ForMode(CurrentMarkdownFull()))
                // The filter key includes the title so the snippet is also found by words from its
                // description (e.g. typing "unit" surfaces the "convert" snippet). Insertion still
                // uses the body, and display still shows "key — Title".
                list.Add(new CompletionData($"{sn.Key} {sn.Title}", sn.Body, description: sn.Title + "\n\n" + sn.Body,
                    priority: 50, content: $"{sn.Key}  —  {sn.Title}", colorHex: ColSnip));

            return list;
        }

        private System.Collections.Generic.IEnumerable<string> CollectDefinedNames()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in (_editor.Text ?? string.Empty).Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(raw,
                    @"^\s*(?:#def\s+)?([A-Za-z_][A-Za-z0-9_]*\$?)\s*(?:\([^)]*\))?\s*(?::?=)");
                if (m.Success && seen.Add(m.Groups[1].Value))
                    yield return m.Groups[1].Value;
            }
        }

        // ---- Double-click a line number to toggle that line to/from a calculation ----

        private void InitLineNumberToggle()
        {
            Loaded += (_, _) =>
            {
                foreach (var m in _editor.TextArea.LeftMargins)
                {
                    if (m is AvaloniaEdit.Editing.LineNumberMargin lnm)
                    {
                        lnm.DoubleTapped += (_, e) =>
                        {
                            var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor.TextArea.TextView));
                            if (pos.HasValue)
                            {
                                _editor.CaretOffset = _editor.Document.GetOffset(pos.Value.Location);
                                _editor.SelectionLength = 0;
                                ToggleCalc();
                            }
                            e.Handled = true;
                        };
                    }
                }
            };
        }

        // ---- Go to definition of the variable/function under the caret ----

        private void GoToDefinition()
        {
            var word = WordAt(_editor.CaretOffset);
            if (string.IsNullOrEmpty(word)) { _status.Text = "No symbol under the caret"; return; }

            var rx = new System.Text.RegularExpressions.Regex(
                @"^\s*(?:#def\s+)?" + System.Text.RegularExpressions.Regex.Escape(word) + @"\s*(?:\([^)]*\))?\s*:?=");
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset).LineNumber;
            var lines = _editor.Document.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                var dl = lines[i];
                var text = _editor.Document.GetText(dl.Offset, dl.Length);
                if (rx.IsMatch(text) && dl.LineNumber != line) // jump to the definition, not the use
                {
                    _editor.CaretOffset = dl.Offset;
                    _editor.Select(dl.Offset, dl.Length);
                    _editor.TextArea.Caret.BringCaretToView();
                    _status.Text = $"Definition of '{word}' at line {dl.LineNumber}";
                    return;
                }
            }
            _status.Text = $"No definition found for '{word}'";
        }

        // ---- Editor right-click context menu ----

        private void BuildEditorContextMenu()
        {
            var menu = new ContextMenu();

            MenuItem Item(string header, Action action, string? gesture = null)
            {
                var mi = new MenuItem { Header = header };
                if (gesture is not null) mi.InputGesture = KeyGesture.Parse(gesture);
                mi.Click += (_, _) => action();
                menu.Items.Add(mi);
                return mi;
            }

            Item("Cut", () => _editor.Cut(), "Ctrl+X");
            Item("Copy", () => _editor.Copy(), "Ctrl+C");
            Item("Paste", () => _editor.Paste(), "Ctrl+V");
            menu.Items.Add(new Separator());
            Item("Toggle ↔ Calc", ToggleCalc);
            var tableItem = Item("Insert table…", OpenTableEditor);
            Item("Go to definition", GoToDefinition, "F12");
            Item("Insert function reference…", () => { if (_selectedTag is FunctionRef fr) _ = InsertFunction(fr); else _status.Text = "Select a function in the Files panel"; });
            menu.Items.Add(new Separator());
            Item("Undo", () => _editor.Undo(), "Ctrl+Z");
            Item("Redo", () => _editor.Redo(), "Ctrl+Y");
            Item("Select all", () => _editor.SelectAll(), "Ctrl+A");

            // "Edit table…" when the clicked line is a table, else "Insert table…".
            menu.Opening += (_, _) =>
                tableItem.Header = ClassifyLineBadge(CaretLineText()) == 't' ? "Edit table…" : "Insert table…";

            _editor.ContextMenu = menu;

            // Right-click moves the caret to the click position so the menu reflects that line.
            _editor.TextArea.AddHandler(global::Avalonia.Input.InputElement.PointerPressedEvent,
                (object? _, global::Avalonia.Input.PointerPressedEventArgs e) =>
                {
                    if (e.GetCurrentPoint(_editor.TextArea).Properties.IsRightButtonPressed)
                    {
                        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor.TextArea.TextView));
                        if (pos.HasValue) { _editor.CaretOffset = _editor.Document.GetOffset(pos.Value.Location); _editor.SelectionLength = 0; }
                    }
                },
                global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        // ---- Hover tooltips (units / functions / constants) ----

        private void InitHoverTooltips()
        {
            var tv = _editor.TextArea.TextView;
            tv.PointerHover += OnEditorPointerHover;
            tv.PointerHoverStopped += (_, _) => ToolTip.SetIsOpen(_editor, false);
        }

        private void OnEditorPointerHover(object? sender, PointerEventArgs e)
        {
            try
            {
                var tv = _editor.TextArea.TextView;
                var pos = tv.GetPosition(e.GetPosition(tv) + tv.ScrollOffset);
                if (pos is null) { ToolTip.SetIsOpen(_editor, false); return; }
                var offset = _editor.Document.GetOffset(pos.Value.Location);
                var word = WordAt(offset);
                // Prefer a user definition's own comment, then the built-in glossary.
                var desc = word is null ? null : (DescribeUserSymbol(word) ?? CalcpadGlossary.Describe(word));
                if (desc is null) { ToolTip.SetIsOpen(_editor, false); return; }
                ToolTip.SetTip(_editor, desc);
                ToolTip.SetIsOpen(_editor, true);
            }
            catch { /* hovering is best-effort */ }
        }

        // If `word` is defined in the current worksheet, return its signature plus the comment
        // line(s) immediately above the definition (so hovering a function shows its doc).
        private string? DescribeUserSymbol(string word)
        {
            var lines = (_editor.Text ?? string.Empty).Split('\n');
            var defRx = new System.Text.RegularExpressions.Regex(
                @"^\s*(?:#def\s+)?(" + System.Text.RegularExpressions.Regex.Escape(word) + @"(?:\([^)]*\))?)\s*(?::?=|$)");
            for (var i = 0; i < lines.Length; i++)
            {
                var m = defRx.Match(lines[i]);
                if (!m.Success) continue;

                // Gather contiguous comment lines just above the definition.
                var comments = new System.Collections.Generic.List<string>();
                for (var j = i - 1; j >= 0; j--)
                {
                    var t = lines[j].TrimStart();
                    if (t.StartsWith("'") || t.StartsWith("\""))
                        comments.Insert(0, t.TrimStart('\'', '"').Trim());
                    else break;
                }
                var sig = m.Groups[1].Value.Trim();
                return comments.Count > 0
                    ? $"{sig}\n\n{string.Join("\n", comments)}"
                    : $"{sig} — defined in this worksheet";
            }
            return null;
        }

        private string? WordAt(int offset)
        {
            var doc = _editor.Document;
            if (offset < 0 || offset > doc.TextLength) return null;
            int s = offset, en = offset;
            while (s > 0 && IsWordChar(doc.GetCharAt(s - 1))) s--;
            while (en < doc.TextLength && IsWordChar(doc.GetCharAt(en))) en++;
            return en > s ? doc.GetText(s, en - s) : null;
        }

        private static bool IsWordChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || "µΩ°²³".IndexOf(c) >= 0;

        // Clicking a gutter badge opens the table editor on a table line, else toggles calc/prose.
        private void ToggleLineByOffset(int offset)
        {
            _editor.CaretOffset = Math.Clamp(offset, 0, _editor.Document.TextLength);
            _editor.SelectionLength = 0;
            if (ClassifyLineBadge(CaretLineText()) == 't') { OpenTableEditor(); return; }
            ToggleCalc();
        }

        private string CaretLineText()
        {
            var l = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            return _editor.Document.GetText(l.Offset, l.Length);
        }

        private static readonly System.Text.RegularExpressions.Regex MatrixLineRegex =
            new(@"^[A-Za-z_]\w*\s*:?=\s*\[.*\]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool IsTableLine(string trimmed) =>
            trimmed.StartsWith("|") || MatrixLineRegex.IsMatch(trimmed);

        // Gutter badge: 't' = table, 'f' = calculation, '#' = heading, ' ' = prose/directive.
        private char ClassifyLineBadge(string lineText)
        {
            var t = lineText.TrimStart();
            if (t.Length == 0) return ' ';
            if (IsTableLine(t)) return 't';                      // matrix [..|..] or markdown | .. |
            if (CurrentMarkdownFull())
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^#{1,6}\s")) return '#';
                if (t[0] == '#') return ' ';                     // directive (#md, #if, …)
                if (t.Contains(":=") || t.Contains('$')) return 'f';
                return ' ';                                      // prose
            }
            if (t[0] == '\'' || t[0] == '"') return ' ';         // classic comment/title = text
            if (t[0] == '#') return ' ';
            return 'f';                                          // classic default = calculation
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

        // Common units offered in the keypad's Units column, grouped (incl. temperature/degrees).
        private static readonly (string group, string[] units)[] KeypadUnits =
        {
            ("Length",   new[] { "m", "cm", "mm", "km" }),
            ("Mass",     new[] { "kg", "g", "t" }),
            ("Force",    new[] { "N", "kN", "MN" }),
            ("Pressure", new[] { "Pa", "kPa", "MPa", "GPa" }),
            ("Temp",     new[] { "°C", "K", "°F", "°R", "Δ°C" }),
            ("Angle",    new[] { "°", "rad" }),
            ("Time",     new[] { "s", "min", "h" }),
            ("Elec",     new[] { "A", "V", "W", "Ω" }),
        };

        private void BuildKeypad()
        {
            var groups = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };

            groups.Children.Add(KeyGroup("Numbers",
                Kp("7", "7"), Kp("8", "8"), Kp("9", "9"), Kp("4", "4"), Kp("5", "5"), Kp("6", "6"),
                Kp("1", "1"), Kp("2", "2"), Kp("3", "3"), Kp("0", "0"), Kp(".", ".")));

            groups.Children.Add(KeyGroup("Operators",
                Kp("+", " + "), Kp("−", " - "), Kp("·", "*"), Kp("/", "/"), Kp("^", "^"),
                // '=' is handled specially (markdown-first assignment is ':=').
                ("=", (Action)(() => InsertAtCaret(CurrentMarkdownFull() ? " := " : " = "))),
                Kp("≠", " ≠ "), Kp("≤", " ≤ "), Kp("≥", " ≥ "), Kp("(", "("), Kp(")", ")"), Kp("|", " | ")));

            groups.Children.Add(KeyGroup("Functions",
                Kp("sin", "sin(‸)"), Kp("cos", "cos(‸)"), Kp("tan", "tan(‸)"),
                Kp("asin", "asin(‸)"), Kp("acos", "acos(‸)"), Kp("atan", "atan(‸)"),
                Kp("√", "sqrt(‸)"), Kp("ln", "ln(‸)"), Kp("log", "log(‸)"),
                Kp("exp", "exp(‸)"), Kp("abs", "abs(‸)"), Kp("round", "round(‸)")));

            groups.Children.Add(KeyGroup("Greek",
                Kp("π", "π"), Kp("α", "α"), Kp("β", "β"), Kp("γ", "γ"), Kp("δ", "δ"), Kp("ε", "ε"),
                Kp("θ", "θ"), Kp("λ", "λ"), Kp("μ", "μ"), Kp("ρ", "ρ"), Kp("σ", "σ"), Kp("φ", "φ"), Kp("ω", "ω")));

            // Right-hand column: this worksheet's variables + a units palette. The unit groups flow
            // into several columns (WrapPanel) so the keypad stays short instead of one tall stack.
            _keypadVars = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 520 };
            var unitsWrap = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 520 };
            foreach (var (g, units) in KeypadUnits)
            {
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var u in units)
                {
                    var btn = MakeButton(u, () => InsertAtCaret(u));
                    btn.Foreground = new SolidColorBrush(Color.Parse(ColUnit));
                    btn.MinWidth = 22;                                         // compact: units are short
                    btn.Padding = new global::Avalonia.Thickness(3, 0);
                    wrap.Children.Add(btn);
                }
                unitsWrap.Children.Add(SymbolRow(g, wrap)); // header-left block that flows and wraps
            }

            var column = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1 };
            column.Children.Add(new TextBlock { Text = "Variables", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#6B7280")) });
            column.Children.Add(_keypadVars);
            column.Children.Add(new TextBlock { Text = "Units", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), Margin = new global::Avalonia.Thickness(0, 2, 0, 0) });
            column.Children.Add(unitsWrap);

            var root = new DockPanel { LastChildFill = true };
            var colBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#E2E4E8")),
                BorderThickness = new global::Avalonia.Thickness(1, 0, 0, 0),
                Padding = new global::Avalonia.Thickness(8, 0, 0, 0),
                Margin = new global::Avalonia.Thickness(8, 0, 0, 0),
                Child = column
            };
            DockPanel.SetDock(colBorder, global::Avalonia.Controls.Dock.Right);
            root.Children.Add(colBorder);
            root.Children.Add(groups);

            _keypadHost.Child = root;
            RefreshKeypadVariables();
        }

        // A compact label + content block for the keypad's unit sub-groups. Blocks flow in a
        // WrapPanel, so each carries a right margin to separate columns.
        private static Control SymbolRow(string title, Control content)
        {
            var header = new TextBlock
            {
                Text = title, FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#9AA1AC")),
                Width = 46, VerticalAlignment = VerticalAlignment.Center
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 2,
                Margin = new global::Avalonia.Thickness(0, 0, 10, 0)
            };
            row.Children.Add(header);
            row.Children.Add(content);
            return row;
        }

        // Repopulates the keypad's Variables column from the names defined in the current worksheet.
        private void RefreshKeypadVariables()
        {
            if (_keypadVars is null) return;
            _keypadVars.Children.Clear();
            var any = false;
            foreach (var name in CollectDefinedNames())
            {
                any = true;
                var btn = MakeButton(name, () => InsertAtCaret(name));
                btn.Foreground = new SolidColorBrush(Color.Parse(ColVar));
                ToolTip.SetTip(btn, DescribeUserSymbol(name) ?? "defined in this worksheet");
                _keypadVars.Children.Add(btn);
            }
            if (!any)
                _keypadVars.Children.Add(new TextBlock
                {
                    Text = "— none yet —", FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#9AA1AC")),
                    Margin = new global::Avalonia.Thickness(2)
                });
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
                MinWidth = 28,
                Margin = new global::Avalonia.Thickness(1),
                Padding = new global::Avalonia.Thickness(4, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        // Toolbar toggle buttons drive panel visibility; the View menu items flip the buttons.
        // (Guarded: IsCheckedChanged can fire during XAML load, before the fields are assigned.)
        private void FilesToggle_Changed(object? sender, RoutedEventArgs e)
        { if (_filePanel is not null && sender is ToggleButton t) _filePanel.IsVisible = t.IsChecked == true; }
        private void KeypadToggle_Changed(object? sender, RoutedEventArgs e)
        { if (_keypadHost is not null && sender is ToggleButton t) _keypadHost.IsVisible = t.IsChecked == true; }
        private void AiToggle_Changed(object? sender, RoutedEventArgs e)
        { if (_aiPanel is not null && sender is ToggleButton t) _aiPanel.IsVisible = t.IsChecked == true; }

        private void ToggleKeypad_Click(object? sender, RoutedEventArgs e)
            => _keypadToggle.IsChecked = !(_keypadToggle.IsChecked == true);
        private void ToggleFiles_Click(object? sender, RoutedEventArgs e)
            => _filesToggle.IsChecked = !(_filesToggle.IsChecked == true);
        private void ToggleAi_Click(object? sender, RoutedEventArgs e)
            => _aiToggle.IsChecked = !(_aiToggle.IsChecked == true);

        private void ToggleCompletion_Click(object? sender, RoutedEventArgs e)
        {
            _completionEnabled = !_completionEnabled;
            _status.Text = _completionEnabled ? "Autocomplete on" : "Autocomplete off";
        }

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

            // Mode row (top): just the agent toggle. Attach lives with the composer at the bottom.
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new global::Avalonia.Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(modeRow, Dock.Top);
            var agent = new CheckBox { Content = "Agent mode", IsChecked = _agentMode, VerticalAlignment = VerticalAlignment.Center };
            agent.IsCheckedChanged += (_, _) => _agentMode = agent.IsChecked == true;
            ToolTip.SetTip(agent, "Let the assistant read, evaluate and insert into the worksheet via tools");
            modeRow.Children.Add(agent);
            root.Children.Add(modeRow);

            // Input + buttons at the bottom (composer). Attach sits here, next to Send.
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
            var attach = new Button { Content = "📎 Attach…" };
            attach.Click += (_, _) => AiAttach();
            _aiAttachInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), FontSize = 11 };
            buttons.Children.Add(send);
            buttons.Children.Add(insert);
            buttons.Children.Add(clear);
            buttons.Children.Add(attach);
            buttons.Children.Add(_aiAttachInfo);
            bottom.Children.Add(_aiInput);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

            // Conversation log fills the middle (last child): rendered as markdown.
            _aiLog = new global::Markdown.Avalonia.MarkdownScrollViewer
            {
                Markdown = string.Empty,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new global::Avalonia.Thickness(0, 6, 0, 6)
            };
            root.Children.Add(_aiLog);

            _aiPanel.Child = root;

            // Persistent memory: reload the previous conversation.
            var storePath = Path.Combine(Path.GetDirectoryName(StateFile)!, "ai-history.json");
            _aiStore = new JsonChatHistoryStore(storePath);
            var first = true;
            foreach (var m in _aiStore.Load())
            {
                _aiHistory.Add(m);
                if (!first) AppendAi("\n\n---\n\n");
                first = false;
                AppendAi($"**{(m.Role == AiRole.User ? "You" : "AI")}:** {m.Text}\n\n");
            }
        }

        private void AiClear()
        {
            _aiHistory.Clear();
            _agentHistory.Clear();
            _aiAttachments.Clear();
            UpdateAttachInfo();
            _aiStore?.Clear();
            _aiLogText.Clear();
            if (_aiLog is not null) _aiLog.Markdown = string.Empty;
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
            if (_aiLogText.Length > 0) AppendAi("\n\n---\n\n");
            AppendAi($"**You:** {prompt}\n\n");
            // Inject the CURRENT worksheet once via the system prompt (regenerated every send),
            // so the model always sees the latest state without stale copies piling up in history.
            var options = new AiOptions { ApiKey = key, SystemPrompt = CalcpadSystemPrompt.Default + "\n\n" + WorksheetContext() };

            try
            {
                if (_agentMode)
                    await RunAgentAsync(prompt, options);
                else
                    await RunChatAsync(prompt, options);
            }
            catch (Exception ex) { AppendAi($"\n\n_[error: {ex.Message}]_"); }

            _aiStore?.Save(_aiHistory);
            _aiBusy = false;
        }

        // The current worksheet (its name, source, and any current render errors) as a fresh
        // system-prompt context block, so the assistant always sees the latest state + problems.
        private string WorksheetContext()
        {
            var name = _currentFile is null ? "untitled" : Path.GetFileName(_currentFile);
            var sb = new StringBuilder();
            sb.Append($"The user is editing this worksheet ({name}). It is the single source of truth — ");
            sb.Append($"always reason about THIS current version:\n```calcpad\n{_editor.Text}\n```");

            var errors = ExtractRenderErrors(_lastHtml);
            if (errors.Count > 0)
            {
                sb.Append("\n\nThe current render has these errors — help fix them when relevant:\n");
                foreach (var err in errors) sb.Append("- ").Append(err).Append('\n');
            }
            return sb.ToString();
        }

        // Pulls the <p class="err"> messages out of the rendered HTML as plain text.
        private static System.Collections.Generic.List<string> ExtractRenderErrors(string html)
        {
            var list = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(html)) return list;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                html, "class=\"err\"[^>]*>(.*?)</p>", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var text = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "<[^>]+>", "");
                text = System.Net.WebUtility.HtmlDecode(text).Trim();
                if (text.Length > 0) list.Add(text);
            }
            return list;
        }

        // Plain streaming chat (optionally multimodal with attachments). The prompt is stored
        // clean; the worksheet lives in the (regenerated) system prompt, not in history.
        private async Task RunChatAsync(string prompt, AiOptions options)
        {
            _aiHistory.Add(_aiAttachments.Count > 0
                ? ChatMessage.FromUser(prompt, _aiAttachments.ToList())
                : ChatMessage.FromUser(prompt));
            _aiAttachments.Clear();
            UpdateAttachInfo();

            AppendAi("**AI:** ");
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
            // The agent runs on a background thread; every tool touches UI state, so marshal to the
            // UI thread (otherwise "Call from invalid thread" and the agent can't read/insert).
            var tools = CalcpadAgentTools.Create(
                getWorksheet: () => Dispatcher.UIThread.Invoke(() => _editor.Text ?? string.Empty),
                insertText: text => Dispatcher.UIThread.Invoke(() => { InsertAtCaret(text); return "Inserted at the caret."; }),
                evaluate: src => Dispatcher.UIThread.Invoke(() => EvaluateForAssistant(src)),
                getResult: () => Dispatcher.UIThread.Invoke(() => _lastHtml));

            var caller = new AnthropicModelCaller(options);
            var conv = new AgenticConversation(caller, tools);
            conv.ToolInvoking += call => AppendAi($"\n\n🔧 *{call.Name}*\n\n");
            _agentTextSeen = false;
            conv.AssistantText += t => { if (!string.IsNullOrEmpty(t)) _agentTextSeen = true; AppendAi(t); };

            _agentHistory.Add(AgentMessage.User(prompt));
            AppendAi("**AI:** ");
            var reply = await conv.RunAsync(_agentHistory);
            // If the run streamed no assistant text (only tool calls), show the final reply so the
            // user always sees an answer.
            if (!_agentTextSeen && !string.IsNullOrWhiteSpace(reply)) AppendAi(reply);
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
                SetWorkingDirToWorksheet();
                var macro = new MacroParser { Include = IncludeResolver };
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
            _aiLogText.Append(text);
            if (_aiRenderQueued) return;            // coalesce re-renders while streaming tokens
            _aiRenderQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _aiRenderQueued = false;
                try { _aiLog!.Markdown = _aiLogText.ToString(); } catch { }
            }, DispatcherPriority.Background);
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

        // The company Word-template share seeded as the template picker's starting folder on first use.
        private const string DefaultTemplateShare = @"\\192.168.0.50\Elexco\Marketing\Word";

        private AppState _state = new();

        // Loads persisted state once (and seeds in-memory options from it). Call before building
        // the options bar so toggles reflect the saved values.
        private void LoadState()
        {
            try
            {
                if (File.Exists(StateFile))
                    _state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile)) ?? new AppState();
            }
            catch { _state = new AppState(); }
            _useTemplate = _state.UseTemplate;
        }

        // Single load-mutate-save point so no setting clobbers another (the old SaveLastFolder wrote
        // a brand-new AppState and wiped every other field).
        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(_state));
            }
            catch { /* best effort */ }
        }

        private void SaveLastFolder(string dir)
        {
            _state.LastFolder = dir;
            SaveState();
        }

        private void RestoreLastFolder()
        {
            if (_state.LastFolder is string dir && Directory.Exists(dir))
                LoadFolder(dir);
        }

        private sealed class AppState
        {
            public string? LastFolder { get; set; }
            public string? TemplateFolder { get; set; }  // folder/share holding the Word templates
            public string? LastTemplate { get; set; }    // selected Word template used for export
            public bool UseTemplate { get; set; }         // master toggle for template-based export
        }

        // Builds a TreeViewItem for a directory: sub-folders first, then *.cpd files, each file
        // expandable into the functions it defines (insertable as a call + #include).
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
                {
                    var fileNode = new TreeViewItem { Header = file.Name, Tag = file.FullName };
                    foreach (var fn in ScanFunctions(file.FullName))
                        fileNode.Items.Add(new TreeViewItem
                        {
                            Header = "ƒ " + fn.signature,
                            Tag = new FunctionRef(file.FullName, fn.call)
                        });
                    node.Items.Add(fileNode);
                }
            }
            catch { return null; } // unreadable folders are skipped
            return node;
        }

        private sealed record FunctionRef(string FilePath, string Call);

        private static readonly System.Text.RegularExpressions.Regex MacroDefRegex =
            new(@"^\s*#def\s+([A-Za-z_][A-Za-z0-9_]*\$?)\s*(?:\(([^)]*)\))?", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex MathFuncRegex =
            new(@"^\s*([A-Za-z_][A-Za-z_0-9]*)\s*\(([^)]*)\)\s*=", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Scans a .cpd for #def macros and math function definitions (ported from the WPF LibraryScanner).
        private static IEnumerable<(string signature, string call)> ScanFunctions(string path)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { yield break; }
            foreach (var line in lines)
            {
                var m = MacroDefRegex.Match(line);
                if (!m.Success) m = MathFuncRegex.Match(line);
                if (!m.Success) continue;
                var name = m.Groups[1].Value;
                var pars = m.Groups.Count > 2 ? m.Groups[2].Value.Trim() : string.Empty;
                var sig = string.IsNullOrEmpty(pars) ? name : $"{name}({pars})";
                yield return (sig, sig);
            }
        }

        // Selecting a node only tracks it; double-click opens a file (or inserts a function),
        // the Insert button includes the file or inserts the selected function's call.
        private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _selectedTag = (_fileTree.SelectedItem as TreeViewItem)?.Tag;
        }

        private async void OpenSelectedFile()
        {
            if (_selectedTag is FunctionRef fr) { await InsertFunction(fr); return; }
            if (_selectedTag is not string path) return;
            try
            {
                OpenOrActivate(path, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        // Insert button: a function's call (+ #include of its file) or an #include for a file.
        private async void InsertFile_Click(object? sender, RoutedEventArgs e)
        {
            switch (_selectedTag)
            {
                case FunctionRef fr: await InsertFunction(fr); break;
                case string path: InsertAtCaret($"#include {BuildLinkedPath(await MaybeCopyIntoFolder(path))}\n"); break;
                default: _status.Text = "Select a file or function first"; break;
            }
        }

        // Ensures the function's file is #included at the top, then inserts a call to it.
        private async System.Threading.Tasks.Task InsertFunction(FunctionRef fr)
        {
            var filePath = await MaybeCopyIntoFolder(fr.FilePath);
            var text = _editor.Text ?? string.Empty;
            var include = $"#include {BuildLinkedPath(filePath)}";
            if (text.IndexOf(include, StringComparison.OrdinalIgnoreCase) < 0)
                _editor.Document.Insert(0, include + "\n");
            InsertAtCaret(fr.Call + "\n");
        }

        // ---- Docked table editor: follows the caret onto a table, two-way bound, Excel-like nav ----

        private bool _tableActive, _tableInsertMode, _tableSyncing, _tableMarkdown, _tableBuilding;
        private int _tableStartLine = -1, _tableEndLine = -1;
        private string _tableName = "A";
        private Border _tableGridHost = null!;
        private readonly System.Collections.Generic.List<System.Collections.Generic.List<string>> _tableData = new();
        private readonly System.Collections.Generic.List<System.Collections.Generic.List<TextBox>> _tableCells = new();

        private void InitTableEditor()
        {
            _editor.TextArea.Caret.PositionChanged += (_, _) => { try { OnCaretTableFollow(); } catch { } };
            // TextEditor-level event (survives document swaps on open/tab switch, unlike Document.Changed).
            _editor.TextChanged += (_, _) => { try { OnTableDocumentChanged(); } catch { } };
        }

        // The ⊞ button / "Insert table…" / "Edit table…" / gutter-badge entry point.
        private void OpenTableEditor()
        {
            var info = DetectTableAtCaret();
            if (info is { } t) OpenBoundTable(t); else OpenInsertTable();
        }

        private void ToggleTableEditor()
        {
            if (_tableOverlay.IsVisible) { CloseTableEditor(); return; }
            OpenTableEditor();
        }

        private bool _tableFollowQueued;

        // Caret moved: bind the editor to the table under the caret, or close it when off tables.
        // Deferred to the dispatcher so the UI isn't rebuilt re-entrantly during caret/input events
        // (which crashes the layout pass when typing in the code). A real click still opens it next frame.
        private void OnCaretTableFollow()
        {
            if (_tableSyncing || _tableInsertMode || _switching || _tableFollowQueued) return;
            _tableFollowQueued = true;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _tableFollowQueued = false;
                try
                {
                    if (_tableSyncing || _tableInsertMode || _switching) return;
                    var info = DetectTableAtCaret();
                    if (info is not { } t) { if (_tableActive) CloseTableEditor(); return; }
                    if (_tableActive && t.start == _tableStartLine && t.end == _tableEndLine) return; // same table
                    OpenBoundTable(t);
                }
                catch { }
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        }

        // Raw-text edit to the bound table (focus NOT in the grid) → refresh the grid from the text.
        // Deferred to the dispatcher so we don't rebuild the grid's visual tree re-entrantly while the
        // editor is still processing the text change (which can crash the layout pass).
        private bool _tableSuppressRefresh;

        private void OnTableDocumentChanged()
        {
            if (_tableSuppressRefresh) { _tableSuppressRefresh = false; return; } // our own grid commit
            if (_tableSyncing || !_tableActive || _switching || _tableRefreshQueued) return;
            _tableRefreshQueued = true;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _tableRefreshQueued = false;
                try { RefreshBoundTableFromText(); } catch { }
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        }

        private bool _tableRefreshQueued;

        private void RefreshBoundTableFromText()
        {
            if (!_tableActive || _tableSyncing) return;
            var focused = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as global::Avalonia.Visual;
            if (focused is not null && global::Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(_tableOverlay, focused)) return; // grid is the source
            var info = DetectTableAtLine(_tableStartLine);     // re-read the bound table from its own line
            if (info is not { } t) { CloseTableEditor(); return; }
            _tableStartLine = t.start; _tableEndLine = t.end; _tableMarkdown = t.md; _tableName = t.name;
            _tableData.Clear();
            foreach (var r in t.data) _tableData.Add(new System.Collections.Generic.List<string>(r));
            RenderTableGrid();                                 // refresh cells in place (keep the panel)
        }

        private (int start, int end, bool md, string name, System.Collections.Generic.List<System.Collections.Generic.List<string>> data)? DetectTableAtLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > _editor.Document.LineCount) return null;
            var data = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
            var name = SeedTableFrom(_editor.Document.GetLineByNumber(lineNumber), data);
            if (_tableSeedStart < 1) return null;
            return (_tableSeedStart, _tableSeedEnd, _tableSeedMarkdown, name ?? "A", data);
        }

        private (int start, int end, bool md, string name, System.Collections.Generic.List<System.Collections.Generic.List<string>> data)? DetectTableAtCaret()
        {
            var data = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
            var name = TrySeedTableFromLine(data); // sets _tableSeedStart/_tableSeedEnd/_tableSeedMarkdown
            if (_tableSeedStart < 1) return null;
            return (_tableSeedStart, _tableSeedEnd, _tableSeedMarkdown, name ?? "A", data);
        }

        private void OpenBoundTable((int start, int end, bool md, string name, System.Collections.Generic.List<System.Collections.Generic.List<string>> data) t)
        {
            _tableActive = true; _tableInsertMode = false;
            _tableStartLine = t.start; _tableEndLine = t.end; _tableMarkdown = t.md; _tableName = t.name;
            _tableData.Clear();
            foreach (var r in t.data) _tableData.Add(new System.Collections.Generic.List<string>(r));
            BuildTablePanel(bound: true);
            _tableOverlay.IsVisible = true;
        }

        private void OpenInsertTable()
        {
            _tableActive = false; _tableInsertMode = true;
            _tableStartLine = _tableEndLine = -1; _tableMarkdown = false; _tableName = "A";
            _tableData.Clear();
            for (int r = 0; r < 3; r++) { var row = new System.Collections.Generic.List<string>(); for (int c = 0; c < 3; c++) row.Add(""); _tableData.Add(row); }
            BuildTablePanel(bound: false);
            _tableOverlay.IsVisible = true;
        }

        private void CloseTableEditor()
        {
            _tableActive = false; _tableInsertMode = false;
            _tableStartLine = _tableEndLine = -1;
            _tableOverlay.IsVisible = false;
        }

        private void BuildTablePanel(bool bound)
        {
            _tableBuilding = true; // suppress commits from initial control events during construction
            var nameBox = new TextBox { Text = _tableName, Width = 90 };
            nameBox.TextChanged += (_, _) => { if (_tableBuilding) return; _tableName = string.IsNullOrWhiteSpace(nameBox.Text) ? "A" : nameBox.Text.Trim(); if (_tableActive) CommitTableToDocument(); };

            var fmt = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
            fmt.Items.Add(new ComboBoxItem { Content = "Calcpad matrix  [a;b|c;d]" });
            fmt.Items.Add(new ComboBoxItem { Content = "Markdown table" });
            fmt.SelectedIndex = _tableMarkdown ? 1 : 0;
            fmt.SelectionChanged += (_, _) => { if (_tableBuilding) return; _tableMarkdown = fmt.SelectedIndex == 1; if (_tableActive) CommitTableToDocument(); };

            _tableGridHost = new Border();
            RenderTableGrid();

            Button Tool(string label, Action act)
            { var b = new Button { Content = label, Margin = new global::Avalonia.Thickness(1), Padding = new global::Avalonia.Thickness(5, 1) }; b.Click += (_, _) => act(); return b; }

            void Structural(Action change) { SyncCells(); change(); RenderTableGrid(); if (_tableActive) CommitTableToDocument(); }

            var row1 = new WrapPanel { Orientation = Orientation.Horizontal };
            row1.Children.Add(new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center, Margin = new global::Avalonia.Thickness(0, 0, 4, 0) });
            row1.Children.Add(nameBox);
            row1.Children.Add(new TextBlock { Text = "as", VerticalAlignment = VerticalAlignment.Center, Margin = new global::Avalonia.Thickness(8, 0, 4, 0) });
            row1.Children.Add(fmt);
            row1.Children.Add(new Border { Width = 10 });
            row1.Children.Add(Tool("+ Row", () => Structural(() => { var c = _tableData.Count > 0 ? _tableData[0].Count : 1; var row = new System.Collections.Generic.List<string>(); for (int i = 0; i < c; i++) row.Add(""); _tableData.Add(row); })));
            row1.Children.Add(Tool("− Row", () => Structural(() => { if (_tableData.Count > 1) _tableData.RemoveAt(_tableData.Count - 1); })));
            row1.Children.Add(Tool("+ Col", () => Structural(() => { foreach (var row in _tableData) row.Add(""); })));
            row1.Children.Add(Tool("− Col", () => Structural(() => { if (_tableData.Count > 0 && _tableData[0].Count > 1) foreach (var row in _tableData) row.RemoveAt(row.Count - 1); })));
            row1.Children.Add(Tool("📋 Paste (Excel)", async () => { SyncCells(); await PasteGridAsync(_tableData); RenderTableGrid(); if (_tableActive) CommitTableToDocument(); }));
            row1.Children.Add(Tool("Clear", () => Structural(() => { foreach (var row in _tableData) for (int c = 0; c < row.Count; c++) row[c] = ""; })));

            var row2 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new global::Avalonia.Thickness(0, 2, 0, 0) };
            if (!bound)
                row2.Children.Add(Tool("Insert at caret", () =>
                {
                    SyncCells();
                    var outText = _tableMarkdown ? BuildMarkdownTable(_tableData) : BuildMatrix(_tableName, _tableData, CurrentMarkdownFull());
                    InsertAtCaret(outText);
                    CloseTableEditor();
                }));
            row2.Children.Add(Tool("Close", CloseTableEditor));
            row2.Children.Add(new TextBlock
            {
                Text = bound ? "Bound to the table — edits apply live (undoable). Tab / Enter / arrows to move." : "Tab / Enter to move. Paste from Excel, then Insert.",
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new global::Avalonia.Thickness(8, 0, 0, 0), Foreground = new SolidColorBrush(Color.Parse("#6B7280"))
            });

            var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
            panel.Children.Add(row1);
            panel.Children.Add(_tableGridHost);
            panel.Children.Add(row2);
            _tableOverlay.Child = panel;
            _tableBuilding = false;
        }

        private void RenderTableGrid()
        {
            if (_tableGridHost is null) return;
            var wasBuilding = _tableBuilding;
            _tableBuilding = true; // programmatic cell Text changes below must not commit
            _tableCells.Clear();
            var g = new Grid();
            var cols = _tableData.Count == 0 ? 0 : _tableData.Max(r => r.Count);
            for (int c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            for (int r = 0; r < _tableData.Count; r++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int r = 0; r < _tableData.Count; r++)
            {
                var rowList = new System.Collections.Generic.List<TextBox>();
                for (int c = 0; c < _tableData[r].Count; c++)
                {
                    int rr = r, cc = c;
                    var tb = new TextBox { Text = _tableData[r][c], MinWidth = 54, Height = 22, MinHeight = 0, Margin = new global::Avalonia.Thickness(1), Padding = new global::Avalonia.Thickness(3, 1) };
                    // Live two-way: every keystroke writes the table back to the document.
                    tb.TextChanged += (_, _) =>
                    {
                        if (_tableBuilding || _tableSyncing) return;
                        if (rr < _tableData.Count && cc < _tableData[rr].Count) _tableData[rr][cc] = tb.Text ?? "";
                        if (_tableActive) CommitTableToDocument();
                    };
                    tb.KeyDown += (_, e) => OnCellKey(e, rr, cc);
                    Grid.SetRow(tb, r); Grid.SetColumn(tb, c);
                    g.Children.Add(tb); rowList.Add(tb);
                }
                _tableCells.Add(rowList);
            }
            _tableGridHost.Child = new ScrollViewer
            {
                Content = g, MaxHeight = 180,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _tableBuilding = wasBuilding; // restore (nested inside BuildTablePanel keeps it true)
        }

        private void SyncCells()
        {
            for (int r = 0; r < _tableCells.Count && r < _tableData.Count; r++)
                for (int c = 0; c < _tableCells[r].Count && c < _tableData[r].Count; c++)
                    _tableData[r][c] = _tableCells[r][c].Text ?? "";
        }

        // Excel-like navigation: Enter → down, Tab → right (Shift+Tab left), arrows → up/down.
        private void OnCellKey(global::Avalonia.Input.KeyEventArgs e, int r, int c)
        {
            if (r < _tableCells.Count && c < _tableCells[r].Count) _tableData[r][c] = _tableCells[r][c].Text ?? "";
            var cols = _tableData.Count > 0 ? _tableData[0].Count : 0;
            if (e.Key == Key.Enter) { e.Handled = true; FocusCell(r + 1, c, addRow: true); }
            else if (e.Key == Key.Tab)
            {
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Shift))
                { if (c > 0) FocusCell(r, c - 1, false); else if (r > 0) FocusCell(r - 1, cols - 1, false); }
                else { if (c + 1 < cols) FocusCell(r, c + 1, false); else FocusCell(r + 1, 0, addRow: true); }
            }
            else if (e.Key == Key.Up && r > 0) { e.Handled = true; FocusCell(r - 1, c, false); }
            else if (e.Key == Key.Down) { e.Handled = true; FocusCell(r + 1, c, addRow: false); }
        }

        private void FocusCell(int r, int c, bool addRow)
        {
            if (r >= _tableData.Count)
            {
                if (!addRow) return;
                var cols = _tableData.Count > 0 ? _tableData[0].Count : 1;
                var row = new System.Collections.Generic.List<string>(); for (int i = 0; i < cols; i++) row.Add("");
                _tableData.Add(row);
                RenderTableGrid();
                if (_tableActive) CommitTableToDocument();
            }
            if (r < 0 || r >= _tableCells.Count || c < 0 || c >= _tableCells[r].Count) return;
            var tb = _tableCells[r][c];
            tb.Focus();
            tb.SelectAll();
        }

        // Writes the grid back to the document table range (undoable). Guarded so it doesn't retrigger
        // the caret/document handlers (which would rebuild the grid and steal focus mid-edit).
        private void CommitTableToDocument()
        {
            if (!_tableActive || _tableSyncing || _tableBuilding || _tableStartLine < 1) return;
            SyncCells();
            var doc = _editor.Document;
            if (_tableStartLine > doc.LineCount || _tableEndLine > doc.LineCount) return;
            var newText = (_tableMarkdown ? BuildMarkdownTable(_tableData) : BuildMatrix(_tableName, _tableData, CurrentMarkdownFull())).TrimEnd('\n');
            var ls = doc.GetLineByNumber(_tableStartLine);
            var le = doc.GetLineByNumber(_tableEndLine);
            var current = doc.GetText(ls.Offset, le.EndOffset - ls.Offset);
            if (newText == current) return;
            _tableSyncing = true;
            _tableSuppressRefresh = true; // the resulting TextChanged must not rebuild the grid
            try { doc.Replace(ls.Offset, le.EndOffset - ls.Offset, newText); }
            catch { /* offsets shifted — the follow handler will re-bind */ }
            finally { _tableSyncing = false; }
            _tableEndLine = _tableStartLine + newText.Split('\n').Length - 1; // row count may have changed
        }

        // Fills the grid model from the clipboard: TSV (Excel) or CSV, one row per line.
        private async System.Threading.Tasks.Task PasteGridAsync(System.Collections.Generic.List<System.Collections.Generic.List<string>> data)
        {
            var top = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
            var text = top?.Clipboard is null ? null : await top.Clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = "Clipboard is empty"; return; }
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n').Split('\n');
            var rows = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
            foreach (var ln in lines)
            {
                var parts = ln.Contains('\t') ? ln.Split('\t') : ln.Split(',');
                rows.Add(parts.Select(s => s.Trim()).ToList());
            }
            var maxc = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            foreach (var r in rows) while (r.Count < maxc) r.Add("");
            if (rows.Count > 0) { data.Clear(); data.AddRange(rows); }
        }

        // The line range the table editor was seeded from (1-based, inclusive), for "Replace table".
        private int _tableSeedStart = -1, _tableSeedEnd = -1;
        private bool _tableSeedMarkdown;

        // Seeds the grid from an EXISTING table at the caret: a Calcpad matrix line "name (:=|=) [ … ]",
        // or a markdown table block (contiguous lines starting with '|'). Records the line range so it
        // can be replaced. Returns the matrix name (null for a markdown table or no table).
        private string? TrySeedTableFromLine(System.Collections.Generic.List<System.Collections.Generic.List<string>> data)
            => SeedTableFrom(_editor.Document.GetLineByOffset(_editor.CaretOffset), data);

        private string? SeedTableFrom(AvaloniaEdit.Document.DocumentLine line, System.Collections.Generic.List<System.Collections.Generic.List<string>> data)
        {
            _tableSeedStart = _tableSeedEnd = -1;
            _tableSeedMarkdown = false;
            var doc = _editor.Document;
            var text = doc.GetText(line.Offset, line.Length);

            var m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*([A-Za-z_]\w*)\s*:?=\s*\[(.+)\]\s*$");
            if (m.Success)
            {
                foreach (var rowStr in m.Groups[2].Value.Split('|'))
                    data.Add(rowStr.Split(';').Select(s => s.Trim()).ToList());
                NormalizeGrid(data);
                _tableSeedStart = _tableSeedEnd = line.LineNumber;
                return m.Groups[1].Value;
            }

            string LineText(int n) { var l = doc.GetLineByNumber(n); return doc.GetText(l.Offset, l.Length); }
            if (text.TrimStart().StartsWith("|"))
            {
                int start = line.LineNumber, end = line.LineNumber;
                while (start > 1 && LineText(start - 1).TrimStart().StartsWith("|")) start--;
                while (end < doc.LineCount && LineText(end + 1).TrimStart().StartsWith("|")) end++;
                for (int n = start; n <= end; n++)
                {
                    var lt = LineText(n);
                    // skip a markdown separator row like | --- | --- |
                    if (lt.Contains('-') && System.Text.RegularExpressions.Regex.IsMatch(lt, @"^\s*\|?[\s:\-|]+\|?\s*$"))
                        continue;
                    data.Add(lt.Trim().Trim('|').Split('|').Select(s => s.Trim()).ToList());
                }
                NormalizeGrid(data);
                _tableSeedStart = start; _tableSeedEnd = end; _tableSeedMarkdown = true;
                return null;
            }
            return null;
        }

        private static void NormalizeGrid(System.Collections.Generic.List<System.Collections.Generic.List<string>> data)
        {
            var maxc = data.Count == 0 ? 0 : data.Max(r => r.Count);
            foreach (var r in data) while (r.Count < maxc) r.Add("");
        }

        private static string BuildMatrix(string name, System.Collections.Generic.List<System.Collections.Generic.List<string>> data, bool markdown)
        {
            // Keep every row the grid has (an added row is intentional); empty cells become 0.
            var body = string.Join(" | ", data.Select(r =>
                string.Join("; ", r.Select(c => string.IsNullOrWhiteSpace(c) ? "0" : c.Trim()))));
            var op = markdown ? ":=" : "=";
            return $"{name} {op} [{body}]\n";
        }

        private static string BuildMarkdownTable(System.Collections.Generic.List<System.Collections.Generic.List<string>> data)
        {
            if (data.Count == 0) return string.Empty;
            string Cell(string c) => string.IsNullOrWhiteSpace(c) ? " " : c.Trim();
            var sb = new StringBuilder();
            sb.Append("| ").Append(string.Join(" | ", data[0].Select(Cell))).Append(" |\n");
            sb.Append("| ").Append(string.Join(" | ", data[0].Select(_ => "---"))).Append(" |\n");
            for (int r = 1; r < data.Count; r++)
                sb.Append("| ").Append(string.Join(" | ", data[r].Select(Cell))).Append(" |\n");
            return sb.ToString();
        }

        // If the file lives outside the worksheet's folder, offer to copy it in (and return the
        // local copy's path); otherwise return the path unchanged.
        private async System.Threading.Tasks.Task<string> MaybeCopyIntoFolder(string filePath)
        {
            if (string.IsNullOrEmpty(_currentFile)) return filePath; // unsaved → nowhere local to copy
            var docDir = Path.GetDirectoryName(_currentFile);
            var srcDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(docDir) || string.Equals(docDir, srcDir, StringComparison.OrdinalIgnoreCase))
                return filePath; // already local
            try
            {
                var ok = await ConfirmAsync("Copy file locally?",
                    $"'{Path.GetFileName(filePath)}' is outside this worksheet's folder.\n\nCopy it here so the worksheet is self-contained?");
                if (!ok) return filePath;
                var dest = Path.Combine(docDir, Path.GetFileName(filePath));
                File.Copy(filePath, dest, overwrite: true);
                _status.Text = "Copied " + Path.GetFileName(filePath) + " locally";
                return dest;
            }
            catch { return filePath; }
        }

        // Returns the path to embed in the document: relative to the current worksheet's folder
        // when "Relative paths" is on and the worksheet is saved, otherwise the absolute path.
        private string BuildLinkedPath(string filePath)
        {
            if (_relativePaths && !string.IsNullOrEmpty(_currentFile))
            {
                try
                {
                    var docDir = Path.GetDirectoryName(_currentFile);
                    if (!string.IsNullOrEmpty(docDir))
                    {
                        var rel = Path.GetRelativePath(docDir, filePath).Replace('\\', '/');
                        if (!Path.IsPathRooted(rel)) // cross-volume → GetRelativePath returns absolute
                        {
                            if (!rel.StartsWith("./", StringComparison.Ordinal) && !rel.StartsWith("../", StringComparison.Ordinal))
                                rel = "./" + rel;
                            return rel;
                        }
                    }
                }
                catch { /* fall through to absolute */ }
            }
            else if (_relativePaths && string.IsNullOrEmpty(_currentFile))
                _status.Text = "Save the worksheet first to use relative paths (absolute used)";
            return filePath.Replace('\\', '/');
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
            var rel = Check("Relative paths", _relativePaths, v => _relativePaths = v);
            ToolTip.SetTip(rel, "Insert #include and image paths relative to the current worksheet (not absolute)");
            bar.Children.Add(rel);
            var auto = Check("Auto-calc", _autoCalc, v => _autoCalc = v);
            ToolTip.SetTip(auto, "Typing 'name =' on a line auto-upgrades it to a calculation (name := …)");
            bar.Children.Add(auto);

            // Word-template export (doesn't affect the HTML preview, so no re-render on toggle).
            var tmpl = new CheckBox
            {
                Content = "Word template",
                IsChecked = _useTemplate,
                Margin = new global::Avalonia.Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            tmpl.IsCheckedChanged += (_, _) =>
            {
                _useTemplate = tmpl.IsChecked == true;
                _state.UseTemplate = _useTemplate;
                SaveState();
                UpdateTemplateTip();
            };
            _templateCheck = tmpl;
            bar.Children.Add(tmpl);
            var pick = MakeButton("Setup…", async () => await ChooseTemplateAsync());
            ToolTip.SetTip(pick, "Configure the Word export template (folder, file, on/off)");
            pick.MinWidth = 26;
            bar.Children.Add(pick);
            UpdateTemplateTip();

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

            bar.Children.Add(new Border { Width = 10 });
            var calc = MakeButton("↔ Calc", ToggleCalc);
            ToolTip.SetTip(calc, "Ctrl+=  ·  Toggle line/selection between prose and a calculation.\nPicks the lightest delimiter: assignment → :=, bare expression → $…$.");
            bar.Children.Add(calc);

            bar.Children.Add(new Border { Width = 10 });
            var table = MakeButton("⊞ Table", ToggleTableEditor);
            ToolTip.SetTip(table, "Inline grid: paste from Excel or type data, insert/replace as a Calcpad matrix or a markdown table");
            bar.Children.Add(table);

            _formatBar.Child = bar;
        }

        // Toggles the current line (or selection) between prose/text and a calculation.
        // Markdown mode wraps/unwraps in $…$; classic mode adds/removes a leading ' comment.
        private void ToggleCalc()
        {
            int start, length;
            string text;
            if (_editor.SelectionLength > 0)
            {
                start = _editor.SelectionStart;
                length = _editor.SelectionLength;
                text = _editor.SelectedText ?? string.Empty;
            }
            else
            {
                var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
                start = line.Offset;
                length = line.Length;
                text = _editor.Document.GetText(line.Offset, line.Length);
            }

            // Preserve leading indentation; operate on the trimmed content.
            var leading = text.Length - text.TrimStart().Length;
            var indent = text.Substring(0, leading);
            var body = text.Substring(leading).TrimEnd();

            string replacement;
            if (CurrentMarkdownFull())
            {
                var Rx = System.Text.RegularExpressions.Regex.IsMatch(body, @"^[A-Za-z_]\w*\s*(?:\([^)]*\))?\s*=(?!=)");
                // Shape-aware: pick the lightest delimiter so the user never types $$ by hand.
                if (body.StartsWith("$$") && body.EndsWith("$$") && body.Length >= 4)
                    replacement = body.Substring(2, body.Length - 4).Trim();                       // calc → prose
                else if (body.StartsWith("$") && body.EndsWith("$") && body.Length >= 2)
                    replacement = body.Substring(1, body.Length - 2).Trim();                        // calc → prose
                else if (body.Contains(":="))
                    replacement = System.Text.RegularExpressions.Regex.Replace(body, @"\s*:=\s*", " = "); // calc assignment → prose
                else if (Rx)
                    replacement = System.Text.RegularExpressions.Regex.Replace(body,
                        @"^(\s*[A-Za-z_]\w*(?:\([^)]*\))?\s*)=(?!=)\s*", "$1:= ");                  // prose assignment → calc (:=)
                else
                    replacement = "$" + body + "$";                                                 // bare expression → inline math
            }
            else
            {
                replacement = body.StartsWith("'") || body.StartsWith("\"")
                    ? body.Substring(1)
                    : "'" + body;
            }

            _editor.Document.Replace(start, length, indent + replacement);
            _editor.Focus();
            ScheduleRender();
        }

        // ---- Snippets (mode-aware) ----

        private void PopulateSnippetsMenu()
        {
            _snippetsMenuMarkdown = CurrentMarkdownFull();
            _snippetsMenu.Items.Clear();
            foreach (var snippet in CalcpadSnippets.ForMode(_snippetsMenuMarkdown))
            {
                var body = snippet.Body;
                // Show the autocomplete trigger key next to the title; preview the body on hover.
                var item = new MenuItem { Header = $"{snippet.Title}   ({snippet.Key})" };
                ToolTip.SetTip(item, body);
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

        // Loads the quick-reference (Calcpad language) help page into the preview pane.
        private void ShowHelp_Click(object? sender, RoutedEventArgs e) => ShowHelpPage("help.html", "Quick reference");

        // Loads the Avalonia app feature/shortcut guide into the preview pane.
        private void ShowAppHelp_Click(object? sender, RoutedEventArgs e) => ShowHelpPage("app-help.html", "Features & shortcuts");

        private void ShowHelpPage(string file, string label)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
                if (File.Exists(path))
                {
                    _webView.LoadHtml(File.ReadAllText(path));
                    _status.Text = $"{label} — press Run (F5) to return to the preview";
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
                RefreshKeypadVariables(); // keep the keypad's Variables column in sync
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
            }
        }

        private string RenderHtml(string source)
        {
            // Resolve relative #include / image paths against the worksheet's folder, like the CLI:
            // MacroParser.ParseInclude uses Path.GetFullPath against the current directory.
            SetWorkingDirToWorksheet();

            var macro = new MacroParser { Include = IncludeResolver };
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

        private void SetWorkingDirToWorksheet()
        {
            try
            {
                var dir = string.IsNullOrEmpty(_currentFile) ? null : Path.GetDirectoryName(_currentFile);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.SetCurrentDirectory(dir);
            }
            catch { /* best effort */ }
        }

        // Resolves #include directives: reads the referenced file (relative to the current
        // worksheet's folder), expands nested #includes, and honours #local/#global sections.
        private string IncludeResolver(string fileName, Queue<string> fields)
        {
            var baseDir = string.IsNullOrEmpty(_currentFile)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(_currentFile) ?? Environment.CurrentDirectory;
            // In markdown-first mode, a classic macro body (e.g. `n = n_rows(x)`) would expand at
            // the call site as prose and never assign. Upgrade variable assignments inside
            // #def…#end def blocks to ':=' so they still compute. The file on disk is untouched,
            // and top-level (classic) library lines are left alone.
            return ExpandInclude(fileName, baseDir, 0, markdownMacros: CurrentMarkdownFull());
        }

        private static readonly System.Text.RegularExpressions.Regex AssignRegex =
            new(@"^(\s*[A-Za-z_]\w*\s*)=(?!=)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string ExpandInclude(string fileName, string baseDir, int depth, bool markdownMacros = false)
        {
            if (depth > 20) return "'[#include nested too deeply]";
            string path;
            try { path = Path.IsPathRooted(fileName) ? fileName : Path.GetFullPath(Path.Combine(baseDir, fileName)); }
            catch { return $"'[invalid include path: {fileName}]"; }
            if (!File.Exists(path)) return $"'[#include not found: {fileName}]";

            var dir = Path.GetDirectoryName(path) ?? baseDir;
            var sb = new StringBuilder();
            var isLocal = false;
            var inDef = false; // inside a #def…#end def macro body
            // A '\v' separates the worksheet code from its saved input-form field data — keep only
            // the code part (matches the CLI), otherwise the form data is parsed as broken code.
            var content = File.ReadAllText(path);
            var vt = content.IndexOf('\v');
            if (vt > 0) content = content.Substring(0, vt);
            foreach (var raw in content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                var t = raw.TrimStart();
                if (t.StartsWith("#local", StringComparison.OrdinalIgnoreCase)) { isLocal = true; continue; }
                if (t.StartsWith("#global", StringComparison.OrdinalIgnoreCase)) { isLocal = false; continue; }
                if (isLocal) continue;
                if (t.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(ExpandInclude(t.Substring("#include".Length).Trim(), dir, depth + 1, markdownMacros)).Append('\n');
                    continue;
                }
                if (t.StartsWith("#end def", StringComparison.OrdinalIgnoreCase)) inDef = false;
                // Migrate classic macro-body lines to markdown so they still compute/print when the
                // body is expanded at a markdown call site:
                //  • a bare assignment  (n = n_rows(x))      → n := n_rows(x)
                //  • a classic text line ("Label : "value)   → **Label :** $value$
                string outLine = raw;
                if (markdownMacros && inDef && t.Length > 0 && t[0] != '#')
                {
                    if (t[0] == '"' || t[0] == '\'')
                        outLine = ConvertClassicTextLine(raw);
                    else if (AssignRegex.IsMatch(raw))
                        outLine = AssignRegex.Replace(raw, "$1:= ");
                }
                sb.Append(outLine).Append('\n');
                if (t.StartsWith("#def", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("#end def", StringComparison.OrdinalIgnoreCase))
                    inDef = true;
            }
            // In a markdown worksheet, parse an included CLASSIC library in classic mode so its
            // top-level definitions (f(x) = …) compute instead of being read as prose. A markdown
            // library re-enables markdown itself via its own #md directive. (#def macro bodies are
            // already converted above, since they expand at the markdown call site.)
            if (markdownMacros && depth == 0)
                return "#md off\n" + sb.ToString() + "#md full\n";
            return sb.ToString();
        }

        // Converts a classic Calcpad text line (a sequence of "heading"/'comment' quoted segments
        // interleaved with bare code expressions) to its markdown-first equivalent: quoted text
        // becomes prose (double-quote → bold), and each code segment becomes inline math $…$ so its
        // value is printed instead of the variable name. Macro params ($-suffixed) survive untouched
        // for the MacroParser to substitute afterwards.
        private static string ConvertClassicTextLine(string raw)
        {
            int lead = 0;
            while (lead < raw.Length && (raw[lead] == ' ' || raw[lead] == '\t')) lead++;
            var indent = raw.Substring(0, lead);
            var s = raw.Substring(lead);
            if (s.Length == 0 || (s[0] != '"' && s[0] != '\'')) return raw;

            var sb = new StringBuilder(indent);
            int i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"' || c == '\'')
                {
                    int j = i + 1;
                    while (j < s.Length && s[j] != c) j++;
                    var text = s.Substring(i + 1, (j < s.Length ? j : s.Length) - (i + 1));
                    if (c == '"')
                    {
                        var trimmed = text.Trim();
                        if (trimmed.Length > 0) sb.Append("**").Append(trimmed).Append("** ");
                        else sb.Append(text);
                    }
                    else sb.Append(text);
                    i = j < s.Length ? j + 1 : s.Length;
                }
                else
                {
                    int j = i;
                    while (j < s.Length && s[j] != '"' && s[j] != '\'') j++;
                    var code = s.Substring(i, j - i).Trim();
                    // Wrap in parentheses: inside a macro body an identifier directly before '$'
                    // (e.g. `calibre$`) is parsed as a macro invocation, so a bare `$calibre$` would
                    // fail with "macro not defined". The closing ')' before '$' avoids that while
                    // staying a valid inline-math expression that prints the value.
                    if (code.Length > 0) sb.Append("$(").Append(code).Append(")$");
                    i = j;
                }
            }
            return sb.ToString();
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
            _packagePath = null; _packageWorkDir = null; // a new doc is not a package
            AddTab(null, NewDocument);
            _editor.CaretOffset = _editor.Document.TextLength;
            PopulateSnippetsMenu();
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
                var path = files[0].Path.LocalPath;
                _packagePath = null; _packageWorkDir = null; // opening a plain worksheet leaves package mode
                OpenOrActivate(path, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        private async void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (_packagePath is not null) { await UpdatePackage(); return; }
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
                var oldDir = _currentFile is null ? null : Path.GetDirectoryName(_currentFile);
                var newPath = file.Path.LocalPath;
                await SaveTo(newPath);
                await MaybeCopyReferencesLocally(oldDir, Path.GetDirectoryName(newPath));
            }
            catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
        }

        // ---- Self-contained .cpdz package (worksheet + images + libraries + config, updatable) ----

        private async void OpenPackage_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Calcpad package",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Calcpad package") { Patterns = new[] { "*.cpdz" } } }
                });
                if (files.Count == 0) return;
                OpenPackage(files[0].Path.LocalPath);
            }
            catch (Exception ex) { _status.Text = "Open package failed: " + ex.Message; }
        }

        private void OpenPackage(string zipPath)
        {
            var (wsPath, cfg, workDir) = CpdPackage.Open(zipPath);
            var text = File.ReadAllText(wsPath).Replace("\r\n", "\n").Replace("\r", "\n");
            OpenOrActivate(wsPath, text);        // loads into a tab; _currentFile = extracted worksheet.cpd
            _packagePath = zipPath;
            _packageWorkDir = workDir;
            if (cfg is not null) ApplyConfigJson(cfg);
            UpdateTitle();
            _status.Text = "Opened package " + Path.GetFileName(zipPath);
        }

        private async void SaveAsPackage_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save as Calcpad package",
                    DefaultExtension = "cpdz",
                    SuggestedFileName = (_currentFile is null ? "worksheet" : Path.GetFileNameWithoutExtension(_currentFile)) + ".cpdz",
                    FileTypeChoices = new[] { new FilePickerFileType("Calcpad package") { Patterns = new[] { "*.cpdz" } } }
                });
                if (file is null) return;
                var dest = file.Path.LocalPath;
                var mainDir = string.IsNullOrEmpty(_currentFile) ? Environment.CurrentDirectory : (Path.GetDirectoryName(_currentFile) ?? Environment.CurrentDirectory);
                SetWorkingDirToWorksheet();
                var (tmplLocal, tmplName) = await ResolvePackageTemplateAsync();
                CpdPackage.Save(_editor.Text ?? string.Empty, mainDir, BuildConfigJson(tmplName), dest,
                    templateLocalPath: tmplLocal, templateEntryName: tmplName);
                _status.Text = "Saved package " + Path.GetFileName(dest) + (tmplName is null ? "" : " (+ template)");
                OpenPackage(dest); // switch into package mode so later saves update it
            }
            catch (Exception ex) { _status.Text = "Save package failed: " + ex.Message; }
        }

        // Re-zips the open package from the current editor text, re-collecting any newly referenced
        // files and mirroring them into the working folder. This is the package "update" on Save.
        private async System.Threading.Tasks.Task UpdatePackage()
        {
            try
            {
                var text = _editor.Text ?? string.Empty;
                var workDir = _packageWorkDir ?? Path.GetDirectoryName(_currentFile!)!;
                if (!string.IsNullOrEmpty(_currentFile)) await File.WriteAllTextAsync(_currentFile, text);
                var (tmplLocal, tmplName) = await ResolvePackageTemplateAsync();
                var rewritten = CpdPackage.Save(text, workDir, BuildConfigJson(tmplName), _packagePath!,
                    syncDir: workDir, templateLocalPath: tmplLocal, templateEntryName: tmplName);
                var norm = rewritten.Replace("\r\n", "\n").Replace("\r", "\n");
                if (norm != text)
                {
                    _editor.Document.Text = norm;                 // new external refs were internalized
                    if (!string.IsNullOrEmpty(_currentFile)) await File.WriteAllTextAsync(_currentFile, norm);
                }
                _dirty = false;
                if (_active is not null) _active.Dirty = false;
                RebuildTabs();
                _status.Text = "Updated package " + Path.GetFileName(_packagePath!);
                Render();
            }
            catch (Exception ex) { _status.Text = "Update package failed: " + ex.Message; }
        }

        // Resolves the configured Word template (cached/local copy + its name) to bundle in the
        // package, or (null, null) when none is set / it is unreachable.
        private async System.Threading.Tasks.Task<(string? local, string? name)> ResolvePackageTemplateAsync()
        {
            if (!_useTemplate || string.IsNullOrEmpty(_state.LastTemplate)) return (null, null);
            var local = await ResolveTemplateAsync(_state.LastTemplate!);
            return local is null ? (null, null) : (local, Path.GetFileName(_state.LastTemplate!));
        }

        // The editor options stored in / restored from a package's config.json.
        private sealed class PackageConfig
        {
            public int Decimals { get; set; } = 6;
            public bool Substitute { get; set; } = true;
            public bool FormatEquations { get; set; } = true;
            public bool Complex { get; set; }
            public bool AdaptivePlot { get; set; } = true;
            public bool EmbedImages { get; set; } = true;
            public bool RelativePaths { get; set; } = true;
            public bool AutoCalc { get; set; }
            public int Degrees { get; set; }
            public string Units { get; set; } = "m";
            public string Palette { get; set; } = "Rainbow";
            public string? TemplateFile { get; set; } // Word template bundled in the package (template/…)
        }

        private string BuildConfigJson(string? templateName = null) => JsonSerializer.Serialize(new PackageConfig
        {
            Decimals = _decimals, Substitute = _substitute, FormatEquations = _formatEquations, Complex = _complex,
            AdaptivePlot = _adaptivePlot, EmbedImages = _embedImages, RelativePaths = _relativePaths, AutoCalc = _autoCalc,
            Degrees = _degrees, Units = _units, Palette = _palette.ToString(), TemplateFile = templateName
        });

        private void ApplyConfigJson(string json)
        {
            try
            {
                var c = JsonSerializer.Deserialize<PackageConfig>(json);
                if (c is null) return;
                _decimals = c.Decimals; _substitute = c.Substitute; _formatEquations = c.FormatEquations; _complex = c.Complex;
                _adaptivePlot = c.AdaptivePlot; _embedImages = c.EmbedImages; _relativePaths = c.RelativePaths; _autoCalc = c.AutoCalc;
                _degrees = c.Degrees; _units = c.Units;
                if (Enum.TryParse<PlotSettings.ColorScales>(c.Palette, out var pal)) _palette = pal;
                // A Word template bundled in the package (template/…) → use it for Word export here.
                if (!string.IsNullOrEmpty(c.TemplateFile) && _packageWorkDir is not null)
                {
                    var bundled = Path.Combine(_packageWorkDir, "template", c.TemplateFile);
                    if (File.Exists(bundled)) { _state.LastTemplate = bundled; _useTemplate = true; }
                }
                var dec = this.FindControl<NumericUpDown>("Decimals");
                if (dec is not null) dec.Value = _decimals;
                BuildOptionsBar(); // rebuild so the toggles reflect the restored values
                UpdateTemplateTip();
                if (_ready) Render();
            }
            catch { /* ignore a bad config */ }
        }

        // ---- Paste: an image becomes a saved <img>; otherwise plain text ----

        private async System.Threading.Tasks.Task HandlePasteAsync()
        {
            try
            {
                var cb = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb is null) return;
                var formats = await cb.GetFormatsAsync();

                string? imgFmt = null;
                foreach (var f in formats)
                {
                    var lf = f.ToLowerInvariant();
                    if (lf.Contains("png")) { imgFmt = f; break; }               // PNG preferred
                    if (lf is "deviceindependentbitmap" or "dib") imgFmt ??= f;   // else DIB (screenshots)
                }
                if (imgFmt is not null)
                {
                    var data = await cb.GetDataAsync(imgFmt);
                    if (data is byte[] bytes && bytes.Length > 0 && await TryInsertPastedImageAsync(bytes, imgFmt))
                        return;
                }

                var text = await cb.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    var start = _editor.SelectionLength > 0 ? _editor.SelectionStart : _editor.CaretOffset;
                    _editor.Document.Replace(start, _editor.SelectionLength, text);
                    _editor.CaretOffset = start + text.Length;
                }
            }
            catch (Exception ex) { _status.Text = "Paste failed: " + ex.Message; }
        }

        // Saves the pasted image next to the worksheet (or in the open package) and inserts a
        // relative <img> so it is self-contained — and gets picked up on the next package update.
        private async System.Threading.Tasks.Task<bool> TryInsertPastedImageAsync(byte[] bytes, string fmt)
        {
            try
            {
                byte[] png;
                if (fmt.ToLowerInvariant().Contains("png"))
                    png = bytes;
                else
                {
                    using var bmpMs = new MemoryStream(DibToBmp(bytes));
                    using var img = new global::Avalonia.Media.Imaging.Bitmap(bmpMs);
                    using var outMs = new MemoryStream();
                    img.Save(outMs);                 // Avalonia encodes PNG
                    png = outMs.ToArray();
                }

                var name = "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
                string imagesDir =
                    _packageWorkDir is not null ? Path.Combine(_packageWorkDir, "Images")
                    : !string.IsNullOrEmpty(_currentFile) ? Path.Combine(Path.GetDirectoryName(_currentFile)!, "Images")
                    : Path.Combine(Path.GetTempPath(), "CalcpadAvalonia", "pasted");
                Directory.CreateDirectory(imagesDir);
                var full = Path.Combine(imagesDir, name);
                await File.WriteAllBytesAsync(full, png);

                // Relative to the worksheet (portable / self-contained) when saved; absolute otherwise.
                var reference = string.IsNullOrEmpty(_currentFile)
                    ? full.Replace('\\', '/')
                    : Path.GetRelativePath(Path.GetDirectoryName(_currentFile)!, full).Replace('\\', '/');
                InsertAtCaret($"<img src=\"{reference}\"/>");
                _status.Text = _packageWorkDir is not null
                    ? "Inserted pasted image (saved in package on next Save)"
                    : "Inserted pasted image → " + reference;
                return true;
            }
            catch { return false; }
        }

        // Wraps a clipboard DIB (BITMAPINFOHEADER + pixels) into a .bmp byte stream.
        private static byte[] DibToBmp(byte[] dib)
        {
            int headerSize = BitConverter.ToInt32(dib, 0);
            short bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int palette = bitCount <= 8 ? (clrUsed == 0 ? (1 << bitCount) : clrUsed) * 4 : 0;
            if (compression == 3) palette += 12; // BI_BITFIELDS colour masks
            int offset = 14 + headerSize + palette;
            int fileSize = 14 + dib.Length;
            var bmp = new byte[fileSize];
            bmp[0] = 0x42; bmp[1] = 0x4D; // "BM"
            BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
            BitConverter.GetBytes(offset).CopyTo(bmp, 10);
            Array.Copy(dib, 0, bmp, 14, dib.Length);
            return bmp;
        }

        // ---- Make a worksheet self-contained: copy its images/included libraries into its folder ----

        // After Save As, offers to copy any image/#include referenced outside the new folder into it
        // and rewrite the paths to local (./…), so the folder is self-contained.
        private async System.Threading.Tasks.Task MaybeCopyReferencesLocally(string? oldDir, string? newDir)
        {
            if (string.IsNullOrEmpty(newDir)) return;
            var refs = CollectExternalReferences(_editor.Text ?? string.Empty, oldDir, newDir);
            if (refs.Count == 0) return;

            var ok = await ConfirmAsync("Copy referenced files?",
                $"This worksheet references {refs.Count} file(s) (images / included libraries) outside this folder.\n\n" +
                "Copy them here and update the paths to local ones so the folder is self-contained?");
            if (!ok) return;

            var text = _editor.Text ?? string.Empty;
            var copied = 0;
            foreach (var r in refs)
            {
                try
                {
                    var sub = r.IsImage ? "Images" : string.Empty;
                    var destDir = string.IsNullOrEmpty(sub) ? newDir : Path.Combine(newDir, sub);
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, Path.GetFileName(r.FullPath));
                    if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(r.FullPath), StringComparison.OrdinalIgnoreCase))
                        File.Copy(r.FullPath, dest, overwrite: true);
                    var localRef = (string.IsNullOrEmpty(sub) ? "./" : "./" + sub + "/") + Path.GetFileName(r.FullPath);
                    text = text.Replace(r.Original, localRef);
                    copied++;
                }
                catch { /* skip a file that can't be copied */ }
            }
            if (copied > 0)
            {
                _editor.Document.Text = text;
                await File.WriteAllTextAsync(_currentFile!, text);
                _dirty = false;
                if (_active is not null) _active.Dirty = false;
                RebuildTabs();
                _status.Text = $"Copied {copied} file(s) locally and updated paths";
                Render();
            }
        }

        private readonly record struct ExtRef(string Original, string FullPath, bool IsImage);

        private static System.Collections.Generic.List<ExtRef> CollectExternalReferences(string text, string? baseDir, string targetDir)
        {
            var list = new System.Collections.Generic.List<ExtRef>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetFull = Path.GetFullPath(targetDir);

            void Consider(string raw, bool isImage)
            {
                var refPath = raw.Trim().Trim('"');
                if (refPath.Length == 0 || refPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || refPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                string full;
                try { full = Path.IsPathRooted(refPath) ? Path.GetFullPath(refPath) : Path.GetFullPath(Path.Combine(baseDir ?? targetDir, refPath)); }
                catch { return; }
                if (!File.Exists(full)) return;
                var dir = Path.GetDirectoryName(full) ?? string.Empty;
                // already inside the target folder (or its Images/) → nothing to do
                if (dir.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase)) return;
                if (seen.Add(full)) list.Add(new ExtRef(raw.Trim(), full, isImage));
            }

            foreach (var line in text.Split('\n'))
            {
                var t = line.TrimStart();
                if (t.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
                    Consider(t.Substring("#include".Length).Trim(), isImage: false);
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                text, "src\\s*=\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                Consider(m.Groups[1].Value, isImage: true);

            return list;
        }

        // Minimal modal Yes/No dialog.
        private async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message)
        {
            var yes = new Button { Content = "Yes", MinWidth = 72 };
            var no = new Button { Content = "No", MinWidth = 72 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(yes); buttons.Children.Add(no);
            var panel = new StackPanel { Margin = new global::Avalonia.Thickness(16), Spacing = 14 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 });
            panel.Children.Add(buttons);
            var dlg = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            yes.Click += (_, _) => dlg.Close(true);
            no.Click += (_, _) => dlg.Close(false);
            return await dlg.ShowDialog<bool>(this);
        }

        private async System.Threading.Tasks.Task SaveTo(string path)
        {
            await File.WriteAllTextAsync(path, _editor.Text ?? string.Empty);
            _currentFile = path;
            _dirty = false;
            if (_active is not null) { _active.Path = path; _active.Dirty = false; }
            RebuildTabs();
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

        private async void TemplateSetup_Click(object? sender, RoutedEventArgs e)
            => await ChooseTemplateAsync();

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

                // Resolve the branding template (if enabled). An unreachable share degrades to a
                // blank export rather than failing, after confirming with the user.
                string? templatePath = null;
                if (_useTemplate && !string.IsNullOrWhiteSpace(_state.LastTemplate))
                {
                    templatePath = await ResolveTemplateAsync(_state.LastTemplate!);
                    if (templatePath is null)
                    {
                        var cont = await ConfirmAsync("Template unavailable",
                            $"The Word template\n{_state.LastTemplate}\nis not reachable right now. " +
                            "Export without it (standard styling)?");
                        if (!cont) { _status.Text = "Export cancelled"; return; }
                    }
                }

                // DOCX needs the OpenXml expression list, produced when parsing with getXml = true.
                SetWorkingDirToWorksheet();
                var macro = new MacroParser { Include = IncludeResolver };
                macro.Parse(_editor.Text ?? string.Empty, out var unwrapped, null, 0, false);
                var parser = new ExpressionParser { Settings = BuildSettings() };
                parser.Parse(unwrapped ?? string.Empty, true, true);

                var expressions = parser.OpenXmlExpressions;
                var html = parser.HtmlResult;
                var dest = file.Path.LocalPath;
                // Run the writer off the UI thread: with a template it reads the (possibly network)
                // master and clones it, which shouldn't freeze the window.
                await System.Threading.Tasks.Task.Run(() =>
                    new OpenXmlWriter(expressions).Convert(html, dest, templatePath ?? string.Empty));

                _status.Text = templatePath is null
                    ? "Exported " + Path.GetFileName(dest)
                    : $"Exported {Path.GetFileName(dest)}  ·  template {Path.GetFileName(templatePath)}";
            }
            catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
        }

        // Configuration wizard for template-based Word export: choose the templates folder/share,
        // pick the template file (scanned from the folder with a timeout, or browsed), and toggle
        // whether exports use it. Everything is persisted to state.json.
        private async System.Threading.Tasks.Task ChooseTemplateAsync()
        {
          try
          {
            _status.Text = "Opening Word template setup…";
            var folderBox = new TextBox
            {
                Text = _state.TemplateFolder
                       ?? (!string.IsNullOrEmpty(_state.LastTemplate) ? Path.GetDirectoryName(_state.LastTemplate) : null)
                       ?? (Directory.Exists(DefaultTemplateShare) ? DefaultTemplateShare : string.Empty),
                Watermark = @"\\server\share\Word  or a local folder",
                MinWidth = 330
            };
            var browseFolder = new Button { Content = "Browse…" };
            var fileCombo = new ComboBox { MinWidth = 300 };
            var rescan = new Button { Content = "Scan" };
            var browseFile = new Button { Content = "Pick file…" };
            var useCheck = new CheckBox { Content = "Use this template when exporting to Word", IsChecked = _useTemplate };
            var scanStatus = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
            };
            string? chosenFile = _state.LastTemplate;

            async System.Threading.Tasks.Task Scan()
            {
                var folder = folderBox.Text?.Trim() ?? string.Empty;
                fileCombo.Items.Clear();
                if (folder.Length == 0) { scanStatus.Text = "Enter or browse to a templates folder."; return; }
                scanStatus.Text = "Scanning…";
                try
                {
                    var names = await System.Threading.Tasks.Task.Run(() =>
                        new DirectoryInfo(folder).EnumerateFiles()
                            .Where(f => (f.Extension.Equals(".dotx", StringComparison.OrdinalIgnoreCase)
                                      || f.Extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                                      && !f.Name.StartsWith("~$"))
                            .Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList())
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    foreach (var n in names) fileCombo.Items.Add(new ComboBoxItem { Content = n });
                    scanStatus.Text = names.Count == 0 ? "No .dotx/.docx found in this folder." : $"{names.Count} template(s) found.";
                    var cur = chosenFile is null ? null : Path.GetFileName(chosenFile);
                    if (cur is not null)
                        for (int k = 0; k < fileCombo.Items.Count; k++)
                            if (fileCombo.Items[k] is ComboBoxItem ci && (string)ci.Content! == cur) { fileCombo.SelectedIndex = k; break; }
                }
                catch { scanStatus.Text = "Folder not reachable right now (offline or wrong path). You can still 'Pick file…'."; }
            }

            fileCombo.SelectionChanged += (_, _) =>
            {
                if (fileCombo.SelectedItem is ComboBoxItem ci)
                    chosenFile = Path.Combine(folderBox.Text?.Trim() ?? string.Empty, (string)ci.Content!);
            };
            browseFolder.Click += async (_, _) =>
            {
                var picked = await StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = "Templates folder", AllowMultiple = false });
                if (picked.Count > 0) { folderBox.Text = picked[0].Path.LocalPath; await Scan(); }
            };
            rescan.Click += async (_, _) => await Scan();
            browseFile.Click += async (_, _) =>
            {
                global::Avalonia.Platform.Storage.IStorageFolder? start = null;
                var seed = folderBox.Text?.Trim();
                if (!string.IsNullOrEmpty(seed)) { try { start = await StorageProvider.TryGetFolderFromPathAsync(seed); } catch { } }
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose a Word template",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                    FileTypeFilter = new[] { new FilePickerFileType("Word template") { Patterns = new[] { "*.dotx", "*.docx" } } }
                });
                if (files.Count > 0)
                {
                    chosenFile = files[0].Path.LocalPath;
                    folderBox.Text = Path.GetDirectoryName(chosenFile) ?? folderBox.Text;
                    useCheck.IsChecked = true;
                    await Scan();
                }
            };

            var body = new StackPanel { Margin = new global::Avalonia.Thickness(16), Spacing = 10, MinWidth = 450 };
            body.Children.Add(new TextBlock
            {
                Text = "Word export can clone a company template so the .docx inherits its header/footer, margins, fonts and heading styles.",
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock { Text = "1 · Templates folder", FontWeight = FontWeight.SemiBold });
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row1.Children.Add(folderBox); row1.Children.Add(browseFolder);
            body.Children.Add(row1);
            body.Children.Add(new TextBlock { Text = "2 · Template file", FontWeight = FontWeight.SemiBold });
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row2.Children.Add(fileCombo); row2.Children.Add(rescan); row2.Children.Add(browseFile);
            body.Children.Add(row2);
            body.Children.Add(scanStatus);
            body.Children.Add(useCheck);

            var save = new Button { Content = "Save", MinWidth = 72 };
            var cancel = new Button { Content = "Cancel", MinWidth = 72 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(save); buttons.Children.Add(cancel);
            body.Children.Add(buttons);

            var dlg = new Window
            {
                Title = "Word template setup",
                SizeToContent = SizeToContent.Height,
                Width = 520,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = body
            };
            save.Click += (_, _) => dlg.Close(true);
            cancel.Click += (_, _) => dlg.Close(false);

            _ = Scan(); // populate while the dialog opens
            var ok = await dlg.ShowDialog<bool>(this);
            if (!ok) return;

            _state.TemplateFolder = string.IsNullOrWhiteSpace(folderBox.Text) ? null : folderBox.Text.Trim();
            _state.LastTemplate = string.IsNullOrWhiteSpace(chosenFile) ? null : chosenFile;
            _useTemplate = useCheck.IsChecked == true && _state.LastTemplate is not null;
            _state.UseTemplate = _useTemplate;
            if (_templateCheck is not null) _templateCheck.IsChecked = _useTemplate;
            SaveState();
            UpdateTemplateTip();
            _status.Text = _state.LastTemplate is null
                ? "No Word template set"
                : (_useTemplate ? "Word template: " : "Word template (off): ") + Path.GetFileName(_state.LastTemplate);
          }
          catch (Exception ex) { _status.Text = "Template setup failed: " + ex.Message; }
        }

        private static string TemplateCacheDir =>
            Path.Combine(Path.GetDirectoryName(StateFile)!, "template-cache");

        // Resolves the template to a LOCAL, usable path and keeps an offline cache:
        //  • source reachable (within a 4 s timeout) → refresh the cache if stale, use the cached copy;
        //  • source offline/slow/denied → fall back to the cached copy if one exists;
        //  • nothing available → null (caller offers a blank export).
        // Reading a local cached copy also keeps the writer off the (possibly slow) network.
        private async System.Threading.Tasks.Task<string?> ResolveTemplateAsync(string source)
        {
            var cacheFile = Path.Combine(TemplateCacheDir, TemplateCacheKey(source) + Path.GetExtension(source));

            (DateTime mtime, long len)? info = null;
            try
            {
                info = await System.Threading.Tasks.Task.Run(() =>
                {
                    var fi = new FileInfo(source);
                    if (!fi.Exists) throw new FileNotFoundException();
                    return (fi.LastWriteTimeUtc, fi.Length);
                }).WaitAsync(TimeSpan.FromSeconds(4));
            }
            catch { info = null; } // timeout / offline / not found / access denied

            if (info is { } meta)
            {
                try
                {
                    Directory.CreateDirectory(TemplateCacheDir);
                    // File.Copy preserves the source's last-write time, so compare it + length.
                    var fresh = File.Exists(cacheFile)
                        && new FileInfo(cacheFile).Length == meta.len
                        && File.GetLastWriteTimeUtc(cacheFile) == meta.mtime;
                    if (!fresh)
                        await System.Threading.Tasks.Task.Run(() => File.Copy(source, cacheFile, true))
                            .WaitAsync(TimeSpan.FromSeconds(20));
                    return cacheFile;
                }
                catch { return File.Exists(cacheFile) ? cacheFile : source; } // source is live even if caching failed
            }

            if (File.Exists(cacheFile))
            {
                _status.Text = "Template offline — using cached copy";
                return cacheFile;
            }
            return null;
        }

        private static string TemplateCacheKey(string source)
        {
            var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(source));
            return System.Convert.ToHexString(bytes)[..16];
        }

        private void UpdateTemplateTip()
        {
            if (_templateCheck is null) return;
            var tip = string.IsNullOrWhiteSpace(_state.LastTemplate)
                ? "Export Word from a company template. Click … to choose one."
                : (_useTemplate ? "Export clones: " : "Template (off): ") + _state.LastTemplate;
            ToolTip.SetTip(_templateCheck, tip);
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
