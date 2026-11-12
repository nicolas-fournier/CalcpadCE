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
        private TextBlock _editorHint = null!;      // greyed empty-state guidance shown on a blank worksheet
        private Button _runButton = null!;          // dynamic label/colour reflects form vs computed mode
        private Button _recalcInputsButton = null!; // "↻ Inputs" — only shown on an input-form worksheet
        // Important confirmations (input/image actions) hold the status line this long so the
        // auto-render's "rendered in N ms" doesn't wipe them before the user can read them.
        private DateTime _statusHoldUntil;
        // Signature of the input-field structure captured when we switched to the computed view,
        // so an unrelated edit (a result line, prose) keeps the entered values instead of discarding
        // them; only a change to the #input structure drops back to the editable form.
        private string _inputSig = string.Empty;
        private readonly MenuItem _snippetsMenu;
        private readonly MenuItem _recentMenu;
        private MenuItem _unitSwitchItem;
        private MenuItem _unitSearchHost;
        private TextBox _unitSearchBox;
        private string? _unitCompatCategory;
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
        private Grid _mainGrid = null!;
        private GridSplitter _fileSplitter = null!, _aiSplitter = null!;
        private double _fileColWidth = 240, _aiColWidth = 340; // remembered panel widths
        private TextBox? _aiKey;
        private ScrollViewer? _aiScroll;
        private StackPanel? _aiMessages;
        private global::Markdown.Avalonia.MarkdownScrollViewer? _aiCurrentBody;
        private StringBuilder? _aiCurrentMd;
        private Border? _aiProposalBar;
        private TextBlock? _aiProposalPreview;
        private string? _pendingProposal;
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
        private readonly List<string> _userFolders = new();   // folders the user added to the browser (kept together)
        private readonly List<System.IO.FileSystemWatcher> _folderWatchers = new();
        private global::Avalonia.Threading.DispatcherTimer? _browserRefreshTimer;
        private string? _currentTreeRoot; // signature of what the browser currently shows (avoids needless reloads)
        private TextBox _fileFilter;
        private TextBlock _fileEmptyHint;
        private Button _insertFileBtn;
        private string _fileFilterText = string.Empty;
        private readonly string[] _previewFiles;
        private int _previewSlot;
        private bool _ready;
        private bool _dirty;
        private bool _snippetsMenuMarkdown;
        private int _decimals = 6;
        private object? _selectedTag; // selected file path (string) or FunctionRef
        // Values entered into #input fields in the preview, applied on the next render.
        private readonly List<(int line, string value)> _inputValues = new();
        // Form (false) vs computed (true) view for #input worksheets: when true the render
        // neutralizes #input/#pause so results compute with the entered values; when false the
        // form is shown with editable boxes. Any text edit returns to the form view.
        private bool _computeInputs;

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
            _editorHint = this.FindControl<TextBlock>("EditorHint")!;
            _runButton = this.FindControl<Button>("RunButton")!;
            _recalcInputsButton = this.FindControl<Button>("RecalcInputsButton")!;
            _snippetsMenu = this.FindControl<MenuItem>("SnippetsMenu")!;
            _recentMenu = this.FindControl<MenuItem>("RecentMenu")!;
            _keypadHost = this.FindControl<Border>("KeypadHost")!;
            _formatBar = this.FindControl<Border>("FormatBar")!;
            _tableOverlay = this.FindControl<Border>("TableOverlay")!;
            _filePanel = this.FindControl<Border>("FilePanel")!;
            _mainGrid = this.FindControl<Grid>("MainGrid")!;
            _fileSplitter = this.FindControl<GridSplitter>("FileSplitter")!;
            _aiSplitter = this.FindControl<GridSplitter>("AiSplitter")!;
            _fileTree = this.FindControl<TreeView>("FileTree")!;
            _fileFilter = this.FindControl<TextBox>("FileFilter")!;
            _fileEmptyHint = this.FindControl<TextBlock>("FileEmptyHint")!;
            _insertFileBtn = this.FindControl<Button>("InsertFileBtn")!;
            _fileTree.KeyDown += FileTree_KeyDown;
            _optionsBar = this.FindControl<Border>("OptionsBar")!;
            _filesToggle = this.FindControl<ToggleButton>("FilesToggle")!;
            _keypadToggle = this.FindControl<ToggleButton>("KeypadToggle")!;
            _aiToggle = this.FindControl<ToggleButton>("AiToggle")!;
            _aiPanel = this.FindControl<Border>("AiPanel")!;
            _fileTree.DoubleTapped += (_, _) => OpenSelectedFile();
            LoadState();      // before BuildAiPanel so the saved API key is available
            BuildRecentMenu();
            BuildAiPanel();

            _template = LoadTemplate();
            var previewDir = Path.Combine(Path.GetTempPath(), "CalcpadAvalonia");
            Directory.CreateDirectory(previewDir);
            _previewFiles = new[] { Path.Combine(previewDir, "preview-a.html"), Path.Combine(previewDir, "preview-b.html") };
            LoadHighlighting();
            InitHoverTooltips();
            InitCompletion();
            InitTableEditor();
            try { _editor.TextArea.LeftMargins.Add(new CalcGutterMargin(ClassifyLineBadge, ToggleLineByOffset)); } catch { }
            BuildEditorContextMenu();
            InitEditorDrop();
            BuildKeypad();
            _keypadHost.IsVisible = _keypadToggle.IsChecked == true; // honour the default-closed toggle (handler is guarded during XAML load)
            BuildFormatBar();
            BuildOptionsBar();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };

            _editor.TextChanged += (_, _) =>
            {
                if (!_ready || _switching) return;
                _dirty = true;
                if (_active is not null) _active.Dirty = true;
                // An edit that leaves the #input structure intact (e.g. tweaking a result line or
                // prose) keeps the computed view and the entered values; any change to the input
                // fields themselves drops back to the editable form so the boxes aren't stale.
                if (!(_computeInputs && InputSignature() == _inputSig))
                {
                    _inputValues.Clear();
                    _computeInputs = false;
                }
                UpdateTitle();
                UpdateTabLabels();
                UpdateRunButton();
                UpdateEditorHint();
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
                    {
                        OpenOrActivate(args[1], File.ReadAllText(args[1]).Replace("\r\n", "\n").Replace("\r", "\n"));
                        AddRecent(args[1]);
                    }
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
            // When the tab represents a .cpdz package, show the package name, not the extracted worksheet.cpd.
            public string? DisplayName;
            public string? PackagePath;     // the .cpdz on disk, when this tab is a package
            public string? PackageWorkDir;  // its extracted folder, shown in the browser while this tab is active
            public bool ReadOnly;           // true for a bundled library opened from the package temp dir
            public string Title => DisplayName ?? (Path is null ? "untitled" : System.IO.Path.GetFileName(Path));
            // The real file on disk this tab maps to (the package, or the plain worksheet).
            public string? DiskPath => PackagePath ?? Path;
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
            // Restore the per-tab package context so Save routes correctly after a tab switch.
            _packagePath = doc.PackagePath;
            _packageWorkDir = doc.PackageWorkDir;
            _inputValues.Clear();
            _switching = false;

            ApplyReadOnly();
            RebuildTabs();
            UpdateTitle();
            RefreshSnippetsMenuIfModeChanged();
            UpdateFileBrowserForActiveDoc();
            if (_ready) Render();
        }

        // ---- File-browser palette & row helpers ----

        private const string ColFolder = "#6B7280", ColCpd = "#1F7FAE", ColFn = "#C2410C",
                             ColAsset = "#9CA3AF", ColPkg = "#7C3AED", ColLabel = "#1F2937";

        // A row = fixed-width colored glyph gutter + label (functions get a dimmed mono "(params)").
        private static Control Row(string glyph, string glyphColor, string label, bool bold = false, string? dimSuffix = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sp.Children.Add(new TextBlock
            {
                Text = glyph,
                Width = 14,
                TextAlignment = global::Avalonia.Media.TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(glyphColor)),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.Parse(ColLabel)),
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (dimSuffix is not null)
                sp.Children.Add(new TextBlock
                {
                    Text = dimSuffix,
                    Foreground = new SolidColorBrush(Color.Parse(ColAsset)),
                    FontFamily = new global::Avalonia.Media.FontFamily("Cascadia Code,Consolas,monospace"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            return sp;
        }

        private static TreeViewItem FunctionItem(string file, (string signature, string call) fn)
        {
            var paren = fn.signature.IndexOf('(');
            var name = paren < 0 ? fn.signature : fn.signature[..paren];
            var pars = paren < 0 ? null : fn.signature[paren..];
            return new TreeViewItem { Header = Row("ƒ", ColFn, name, dimSuffix: pars), Tag = new FunctionRef(file, fn.call) };
        }

        private bool PassesFilter(string name, IEnumerable<(string signature, string call)>? fns = null)
        {
            if (_fileFilterText.Length == 0) return true;
            if (name.Contains(_fileFilterText, StringComparison.OrdinalIgnoreCase)) return true;
            return fns is not null && fns.Any(f => f.signature.Contains(_fileFilterText, StringComparison.OrdinalIgnoreCase));
        }

        // The browser always keeps the user's opened folder, and — while a .cpdz tab is active —
        // ALSO shows a top node with just that package's own libraries.
        private void UpdateFileBrowserForActiveDoc()
        {
            var pkgWd = _active?.PackageWorkDir is string wd && Directory.Exists(wd) ? wd : null;
            var sig = (pkgWd ?? string.Empty) + "|" + string.Join(";", _userFolders) + "|" + _fileFilterText;
            if (sig == _currentTreeRoot) return;          // same composition already shown
            _currentTreeRoot = sig;

            var expanded = CaptureExpanded();
            var selected = _selectedTag;
            _fileTree.ItemsSource = null;
            _fileTree.Items.Clear();

            if (pkgWd is not null)
            {
                var libs = BuildPackageLibsNode(pkgWd, _active!.DisplayName ?? "package");
                if (libs is not null) { libs.IsExpanded = true; _fileTree.Items.Add(libs); }
            }
            foreach (var f in _userFolders)
            {
                if (!Directory.Exists(f)) continue;
                var root = BuildFileNode(new DirectoryInfo(f), 0, isRoot: true);
                if (root is not null) { root.IsExpanded = true; _fileTree.Items.Add(root); }
                else if (_fileFilterText.Length == 0)
                    _fileTree.Items.Add(new TreeViewItem { Header = Row("○", ColAsset, Path.GetFileName(f) + " (unreadable)"), Tag = new DirRef(f) });
            }

            ApplyExpanded(expanded, restoreSelection: selected);
            _fileEmptyHint.IsVisible = _fileTree.Items.Count == 0 && _fileFilterText.Length == 0;
        }

        private void FileFilter_Changed(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
        {
            _fileFilterText = (_fileFilter.Text ?? string.Empty).Trim();
            _currentTreeRoot = null;           // force a rebuild for the new filter
            UpdateFileBrowserForActiveDoc();
        }

        // Records the keys (D:dir / F:file) of expanded nodes so a rebuild can restore them.
        private HashSet<string> CaptureExpanded()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(global::Avalonia.Controls.ItemsControl node)
            {
                foreach (var obj in node.Items)
                    if (obj is TreeViewItem tvi)
                    {
                        if (tvi.IsExpanded && NodeKey(tvi.Tag) is string k) set.Add(k);
                        Walk(tvi);
                    }
            }
            Walk(_fileTree);
            return set;
        }

        private void ApplyExpanded(HashSet<string> expanded, object? restoreSelection)
        {
            void Walk(global::Avalonia.Controls.ItemsControl node)
            {
                foreach (var obj in node.Items)
                    if (obj is TreeViewItem tvi)
                    {
                        if (NodeKey(tvi.Tag) is string k && expanded.Contains(k)) tvi.IsExpanded = true;
                        if (_fileFilterText.Length > 0) tvi.IsExpanded = true; // a filtered tree reads better expanded
                        if (restoreSelection is not null && Equals(tvi.Tag, restoreSelection)) tvi.IsSelected = true;
                        Walk(tvi);
                    }
            }
            Walk(_fileTree);
        }

        private static string? NodeKey(object? tag) => tag switch
        {
            DirRef d => "D:" + d.Path,
            string s => "F:" + s,
            _ => null
        };

        // A "Libraries · <package>" node listing only the .cpd libraries bundled in the .cpdz
        // (everything except the package's own worksheet.cpd), each expandable into its functions.
        private TreeViewItem? BuildPackageLibsNode(string workDir, string pkgName)
        {
            var node = new TreeViewItem { Header = Row("●", ColPkg, "Libraries · " + pkgName, bold: true), Tag = new DirRef(workDir) };
            AttachFolderMenu(node, workDir);
            var any = false;
            try
            {
                foreach (var file in Directory.EnumerateFiles(workDir, "*.cpd", SearchOption.AllDirectories)
                                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(Path.GetFileName(file), "worksheet.cpd", StringComparison.OrdinalIgnoreCase)) continue;
                    var fns = ScanFunctions(file).ToList();
                    if (!PassesFilter(Path.GetFileName(file), fns)) continue;
                    var fileNode = new TreeViewItem { Header = Row("●", ColCpd, Path.GetFileName(file)), Tag = file };
                    AttachFileMenu(fileNode, file, isPackageLib: true);
                    EnableDrag(fileNode, file);
                    foreach (var fn in fns)
                        if (PassesFilter(fn.signature)) { var fi = FunctionItem(file, fn); AttachFunctionMenu(fi, file, fn.call); EnableDrag(fi, new FunctionRef(file, fn.call)); fileNode.Items.Add(fi); }
                    node.Items.Add(fileNode);
                    any = true;
                }
            }
            catch { /* ignore unreadable package */ }
            return any ? node : null;
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
                // Left-click activates; right-click must fall through to the context menu
                // (activating would RebuildTabs and destroy this Border mid-gesture, killing the menu).
                tab.PointerPressed += (_, ev) =>
                {
                    if (ev.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
                        ActivateDoc(captured);
                };
                tab.ContextMenu = BuildTabContextMenu(captured);
                _tabStrip.Children.Add(tab);
            }
        }

        private void UpdateTabLabels()
        {
            // Cheap: only the active tab's dirty marker changes on a keystroke.
            RebuildTabs();
        }

        // ---- Tab right-click menu: copy path, rename, reveal in Explorer ----

        private ContextMenu BuildTabContextMenu(OpenDoc doc)
        {
            var menu = new ContextMenu();
            var items = new List<object>();

            var copy = new MenuItem { Header = "Copy full path" };
            copy.Click += async (_, _) => await CopyTabPath(doc);

            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) => await RenameTabFile(doc);

            var reveal = new MenuItem { Header = "Open in Explorer" };
            reveal.Click += (_, _) => RevealInExplorer(doc);

            var hasFile = !string.IsNullOrEmpty(doc.DiskPath);
            copy.IsEnabled = rename.IsEnabled = reveal.IsEnabled = hasFile;

            items.Add(copy);
            items.Add(rename);
            items.Add(reveal);
            menu.ItemsSource = items;
            return menu;
        }

        private async System.Threading.Tasks.Task CopyTabPath(OpenDoc doc)
        {
            var path = doc.DiskPath;
            if (string.IsNullOrEmpty(path)) { _status.Text = "This tab has no file yet — save it first"; return; }
            var cb = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb is not null) { await cb.SetTextAsync(path); _status.Text = "Copied path: " + path; }
        }

        private void RevealInExplorer(OpenDoc doc)
        {
            var path = doc.DiskPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { _status.Text = "File not found on disk"; return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { _status.Text = "Open in Explorer failed: " + ex.Message; }
        }

        private async System.Threading.Tasks.Task RenameTabFile(OpenDoc doc)
        {
            var path = doc.DiskPath;
            if (string.IsNullOrEmpty(path)) { _status.Text = "Save the file before renaming it"; return; }
            if (!File.Exists(path)) { _status.Text = "File not found on disk"; return; }

            var newName = await PromptText("Rename file", "New file name:", Path.GetFileName(path));
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (string.Equals(newName, Path.GetFileName(path), StringComparison.Ordinal)) return;
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { _status.Text = "Invalid file name"; return; }

            var dir = Path.GetDirectoryName(path)!;
            // Keep the original extension if the user didn't type one.
            if (string.IsNullOrEmpty(Path.GetExtension(newName)))
                newName += Path.GetExtension(path);
            var dest = Path.Combine(dir, newName);
            if (File.Exists(dest)) { _status.Text = "A file with that name already exists"; return; }

            try
            {
                File.Move(path, dest);
                // Re-point this tab at the renamed file.
                if (string.Equals(doc.PackagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    doc.PackagePath = dest;
                    doc.DisplayName = Path.GetFileName(dest);
                    if (ReferenceEquals(doc, _active)) _packagePath = dest;
                }
                else
                {
                    doc.Path = dest;
                    if (ReferenceEquals(doc, _active)) _currentFile = dest;
                }
                AddRecent(dest);
                RebuildTabs();
                UpdateTitle();
                _status.Text = "Renamed to " + Path.GetFileName(dest);
            }
            catch (Exception ex) { _status.Text = "Rename failed: " + ex.Message; }
        }

        // Small modal text-input dialog (Avalonia has no built-in prompt).
        private async System.Threading.Tasks.Task<string?> PromptText(string title, string label, string initial)
        {
            var box = new TextBox { Text = initial, MinWidth = 320 };
            var body = new StackPanel { Margin = new global::Avalonia.Thickness(16), Spacing = 10, MinWidth = 340 };
            body.Children.Add(new TextBlock { Text = label });
            body.Children.Add(box);
            var ok = new Button { Content = "OK", MinWidth = 72, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 72, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            body.Children.Add(buttons);
            var dlg = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.Height,
                Width = 380,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = body
            };
            ok.Click += (_, _) => dlg.Close(true);
            cancel.Click += (_, _) => dlg.Close(false);
            box.AttachedToVisualTree += (_, _) => { box.SelectAll(); box.Focus(); };
            var accepted = await dlg.ShowDialog<bool>(this);
            return accepted ? box.Text : null;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // F5 goes through Run_Click so an #input worksheet toggles form <-> computed
            // (a plain Render() here would freeze the current view and never return to the form).
            if (e.Key == Key.F5) { Run_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
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

        private static readonly string AppVersion = GetAppVersion();
        private static string GetAppVersion()
        {
            var asm = typeof(MainWindow).Assembly;
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
            return (info ?? asm.GetName().Version?.ToString() ?? "").Split('+')[0]; // drop build metadata
        }

        private void UpdateTitle()
        {
            var name = _packagePath is not null ? Path.GetFileName(_packagePath)
                     : _currentFile is null ? "untitled" : Path.GetFileName(_currentFile);
            Title = $"CalcpadCE {AppVersion} — {name}{(_dirty ? " *" : string.Empty)}";
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

            // Don't offer this inside ordinary prose ("cost-benefit", "and/or", "trade-off"): only
            // when the line looks like a calculation — it has a number or an assignment '='.
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"\d") && !text.Contains('='))
                return;

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
            Item("Force to text", ForceToText);
            var inputItem = Item("Input field…", InputFieldSmart);
            var tableItem = Item("Insert table…", OpenTableEditor);
            Item("Insert image…", () => _ = InsertImageAsync());
            BuildUnitSwitchItem();
            menu.Items.Add(_unitSwitchItem);
            Item("Go to definition", GoToDefinition, "F12");
            Item("Insert function reference…", () => { if (_selectedTag is FunctionRef fr) _ = InsertFunction(fr); else _status.Text = "Select a function in the Files panel"; });
            menu.Items.Add(new Separator());
            Item("Undo", () => _editor.Undo(), "Ctrl+Z");
            Item("Redo", () => _editor.Redo(), "Ctrl+Y");
            Item("Select all", () => _editor.SelectAll(), "Ctrl+A");

            // "Edit table…" when the clicked line is a table, else "Insert table…".
            menu.Opening += (_, _) =>
            {
                var lineText = CaretLineText();
                tableItem.Header = ClassifyLineBadge(lineText) == 't' ? "Edit table…" : "Insert table…";
                var sel = (_editor.SelectedText ?? string.Empty).Trim();
                var onInput = System.Text.RegularExpressions.Regex.IsMatch(lineText, @"=\s*\?\{");
                var quick = System.Text.RegularExpressions.Regex.IsMatch(sel, @"^-?\d+(?:[.,]\d+)?$")
                    || System.Text.RegularExpressions.Regex.IsMatch(lineText, @"^\s*[A-Za-z_][\w\.]*\s*(?::=|=)\s*-?\d");
                // One context-aware item: edit an existing field, quick-wrap a selected number /
                // assignment inline, or open the panel to build a fresh field.
                inputItem.Header = onInput ? "Edit input field…" : quick ? "Wrap number as input field" : "Insert input field…";
                // Offer unit switching on calculation lines (results) and on input-field lines.
                _unitSwitchItem.IsEnabled = ClassifyLineBadge(lineText) is 'f' or 'r' or 'i' ||
                    (lineText.Contains('=') && !lineText.TrimStart().StartsWith("#"));
                _unitSwitchItem.Header = lineText.Contains("?{") ? "Switch input unit" : "Switch result to unit";
            };

            _editor.ContextMenu = menu;

            // A right-click must NOT move the caret or clear the selection: the context menu
            // acts on wherever the caret already is. AvaloniaEdit moves the caret on a
            // right-button press by default, so swallow that press in the tunnel phase (the
            // ContextRequested event that opens the menu is separate and still fires).
            _editor.TextArea.AddHandler(global::Avalonia.Input.InputElement.PointerPressedEvent,
                (object? _, global::Avalonia.Input.PointerPressedEventArgs e) =>
                {
                    if (e.GetCurrentPoint(_editor.TextArea).Properties.IsRightButtonPressed)
                        e.Handled = true;
                },
                global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        // ---- "Switch result to unit" submenu (compatible units + live search) ----

        private void BuildUnitSwitchItem()
        {
            _unitSearchBox = new TextBox
            {
                Watermark = "Search units…",
                MinWidth = 180,
                Margin = new global::Avalonia.Thickness(4, 2)
            };
            _unitSearchBox.TextChanged += (_, _) => RefreshUnitSwitchList();
            _unitSearchHost = new MenuItem { StaysOpenOnClick = true, Header = _unitSearchBox };

            _unitSwitchItem = new MenuItem { Header = "Switch result to unit" };
            // Seed one child so the submenu arrow shows before it is first opened.
            _unitSwitchItem.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
            _unitSwitchItem.SubmenuOpened += (_, _) =>
            {
                _unitCompatCategory = DetectCompatibleCategory(CaretLineText());
                _unitSearchBox.Text = string.Empty;
                RefreshUnitSwitchList();
                Dispatcher.UIThread.Post(() => _unitSearchBox.Focus(), DispatcherPriority.Input);
            };
        }

        private void RefreshUnitSwitchList()
        {
            if (_unitSwitchItem is null) return;
            _unitSwitchItem.Items.Clear();
            _unitSwitchItem.Items.Add(_unitSearchHost);
            _unitSwitchItem.Items.Add(new Separator());

            var filter = (_unitSearchBox.Text ?? string.Empty).Trim();
            var hasFilter = filter.Length > 0;
            var any = false;

            // Escape hatch for any composite unit: apply exactly what was typed (e.g. "kN/m",
            // "N/mm^2", "kg/m^3"), even when it isn't one of the catalog presets.
            if (hasFilter)
            {
                var typed = filter;
                var applyTyped = new MenuItem { Header = $"Apply  “{typed}”", FontWeight = FontWeight.SemiBold };
                applyTyped.Click += (_, _) => ApplyUnitToCaretLine(typed);
                _unitSwitchItem.Items.Add(applyTyped);
                _unitSwitchItem.Items.Add(new Separator());
            }

            foreach (var (cat, units) in UnitSwitchCatalog)
            {
                IEnumerable<string> matches;
                if (hasFilter)
                    matches = units.Where(u => u.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                            || cat.Contains(filter, StringComparison.OrdinalIgnoreCase));
                else if (_unitCompatCategory is not null)
                    matches = cat == _unitCompatCategory ? units : Array.Empty<string>();
                else
                    matches = units; // no hint and no search → show everything

                var list = matches.ToList();
                if (list.Count == 0) continue;

                _unitSwitchItem.Items.Add(new MenuItem
                {
                    Header = cat + (cat == _unitCompatCategory && !hasFilter ? "  (compatible)" : string.Empty),
                    IsEnabled = false,
                    FontWeight = FontWeight.SemiBold
                });
                foreach (var u in list)
                {
                    var unit = u;
                    var mi = new MenuItem { Header = "    " + unit };
                    mi.Click += (_, _) => ApplyUnitToCaretLine(unit);
                    _unitSwitchItem.Items.Add(mi);
                    any = true;
                }
            }
            if (!any)
                _unitSwitchItem.Items.Add(new MenuItem { Header = "(no match)", IsEnabled = false });
        }

        // Finds the catalog category whose unit appears on the line, so the menu can default to
        // dimensionally-compatible units. Composite units (kN/m, m^2, kN*m) are matched FIRST so
        // a line load "kN/m" is recognised as such instead of its bare "m" matching "Length".
        private static string? DetectCompatibleCategory(string line)
        {
            // Match against the unit part only: after the last '=' and before any comment.
            var eq = line.LastIndexOf('=');
            var span = eq >= 0 ? line[(eq + 1)..] : line;
            var q = span.IndexOfAny(new[] { '\'', '"' });
            if (q >= 0) span = span[..q];

            var all = UnitSwitchCatalog
                .SelectMany(c => c.units.Select(u => (c.cat, u)))
                .OrderByDescending(x => x.u.IndexOfAny(new[] { '^', '*', '/' }) >= 0 ? 1 : 0) // composites first
                .ThenByDescending(x => x.u.Length);
            foreach (var (cat, u) in all)
                if (ContainsUnitToken(span, u))
                    return cat;
            return null;
        }

        // True if `unit` appears in `text` as a whole unit token (not a fragment of a longer
        // identifier). For a composite like "kN/m" the whole expression must appear verbatim.
        private static bool ContainsUnitToken(string text, string unit)
        {
            var pattern = $"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(unit)}(?![A-Za-z0-9])";
            return System.Text.RegularExpressions.Regex.IsMatch(text, pattern);
        }

        // Applies a unit to the caret line. An input-field line (…?{…}…) has its own unit token
        // rewritten; any other calculation line gets a "| unit" display target. The chosen unit
        // may be a composite (kN/m, N/mm^2) — it is applied verbatim and parsed by the engine.
        private void ApplyUnitToCaretLine(string unit)
        {
            unit = unit.Trim();
            if (unit.Length == 0) return;
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            var text = _editor.Document.GetText(line.Offset, line.Length);

            string newText;
            string label;
            var brace = text.LastIndexOf('}');
            if (text.Contains("?{") && brace >= 0)
            {
                // Input field: replace the unit token that follows the closing brace, keeping any
                // trailing comment. "span = ?{6}m" → "span = ?{6}mm".
                var after = text[(brace + 1)..];
                var cut = after.IndexOfAny(new[] { '\'', '"' });
                var comment = cut >= 0 ? " " + after[cut..].TrimStart() : string.Empty;
                newText = text[..(brace + 1)] + unit + comment;
                label = "Input unit → " + unit;
            }
            else
            {
                // Result line: set/replace the "| unit" display target.
                var bar = text.LastIndexOf('|');
                var baseText = (bar >= 0 && !text[bar..].Contains(']')) ? text[..bar].TrimEnd() : text.TrimEnd();
                newText = baseText + " | " + unit;
                label = "Result unit → " + unit;
            }

            _editor.Document.Replace(line.Offset, line.Length, newText);
            _editor.CaretOffset = line.Offset + newText.Length;
            _status.Text = label;
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

        // Clicking a gutter badge: table → table editor, input → the input panel (bound to the
        // field), image → just place the caret, else toggle calc/prose.
        private void ToggleLineByOffset(int offset)
        {
            _editor.CaretOffset = Math.Clamp(offset, 0, _editor.Document.TextLength);
            _editor.SelectionLength = 0;
            switch (ClassifyLineBadge(CaretLineText()))
            {
                case 't': OpenTableEditor(); return;
                case 'i': OpenInputPanel(); return;
                case 'g': // image: select the line and point to the Image button to replace it
                    var gl = _editor.Document.GetLineByOffset(_editor.CaretOffset);
                    _editor.Select(gl.Offset, gl.Length);
                    _status.Text = "Image line — edit the ![…](…) text, or use ▣ Image to pick another";
                    return;
                case '#': return; // heading: not a calculation, nothing to toggle
                default: ToggleCalc(); return;
            }
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

        // Gutter badge: 't' table · 'i' input field · 'g' image · 'r' result (=) · 'f' calculation
        // (assignment) · '#' heading · ' ' prose/directive.
        private char ClassifyLineBadge(string lineText)
        {
            var t = lineText.TrimStart();
            if (t.Length == 0) return ' ';
            if (IsTableLine(t)) return 't';                      // matrix [..|..] or markdown | .. |
            if (t.StartsWith("![") || t.StartsWith("<img", StringComparison.OrdinalIgnoreCase)) return 'g';
            if (CurrentMarkdownFull())
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^#{1,6}\s")) return '#';
                if (t[0] == '#') return ' ';                     // directive (#md, #if, …)
                var hasMath = t.Contains(":=") || t.Contains('$');
                if (!hasMath) return IsEvalRequest(t) ? 'r' : ' ';
                if (t.Contains("?{")) return 'i';                // editable input field
                return t.Contains(":=") ? 'f' : 'r';             // assignment vs pure $…$ result
            }
            if (t[0] == '\'' || t[0] == '"') return ' ';         // classic comment/title = text
            if (t[0] == '#') return ' ';
            if (t.Contains("?{")) return 'i';
            // name = value → assignment (ƒ); a bare expression or "expr=" → result (=).
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z_][\w\.]*\s*=\s*\S") && !t.TrimEnd().EndsWith("="))
                return 'f';
            return 'r';
        }

        // A line ending in a lone '=' is a calculator-style "evaluate this" request (see
        // EvaluateTrailingEquals), e.g. "x*2=". A bare assignment target ("M =", "f(x) =") is an
        // unfinished assignment, not an evaluation — EvaluateTrailingEquals skips it, so the badge
        // must too, otherwise the '=' result badge would lie about a line that renders as prose.
        private static bool IsEvalRequest(string t)
        {
            var e = t.TrimEnd();
            if (e.Length < 2 || e[^1] != '=' ||
                e.EndsWith(":=") || e.EndsWith("==") ||
                e.EndsWith(">=") || e.EndsWith("<=") || e.EndsWith("!=")) return false;
            var expr = e[..^1].TrimEnd();
            return !System.Text.RegularExpressions.Regex.IsMatch(expr, @"^\s*[A-Za-z_]\w*(?:\s*\([^)]*\))?\s*$");
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

        // ---- Image insertion (markdown-aware) ----

        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

        // Lets the user pick an image and inserts a reference at the caret. When the worksheet
        // is saved, the picture is copied into an "Images/" folder beside it and referenced with
        // a relative path (so it survives in HTML/DOCX export and in a .cpdz package); for an
        // unsaved worksheet it falls back to an absolute file:// URI so the preview still shows it.
        private async System.Threading.Tasks.Task InsertImageAsync()
        {
            try
            {
                var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Insert image",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Images")
                            { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg" } },
                        FilePickerFileTypes.All
                    }
                });
                var file = picked?.FirstOrDefault();
                if (file is null) return;

                var srcPath = file.Path.LocalPath;
                var alt = Path.GetFileNameWithoutExtension(srcPath);
                string url;

                var dir = _currentFile is null ? null : Path.GetDirectoryName(_currentFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    // Copy into <worksheet>/Images with a non-clobbering name, reference relatively.
                    var imagesDir = Path.Combine(dir, "Images");
                    Directory.CreateDirectory(imagesDir);
                    var name = Path.GetFileName(srcPath);
                    var dest = Path.Combine(imagesDir, name);
                    if (!PathsEqual(srcPath, dest))
                    {
                        if (File.Exists(dest) && !FilesIdentical(srcPath, dest))
                        {
                            var stem = Path.GetFileNameWithoutExtension(name);
                            var ext = Path.GetExtension(name);
                            for (var i = 2; File.Exists(dest); ++i)
                                dest = Path.Combine(imagesDir, $"{stem}-{i}{ext}");
                        }
                        if (!File.Exists(dest)) File.Copy(srcPath, dest);
                    }
                    url = "Images/" + Path.GetFileName(dest).Replace(" ", "%20");
                }
                else
                {
                    // Unsaved worksheet: no base href, so use an absolute file:// URI.
                    url = new Uri(srcPath).AbsoluteUri;
                    StatusHold("Image linked by absolute path — save the worksheet to copy it into Images/");
                }

                InsertAtCaret(CurrentMarkdownFull()
                    ? $"![{alt}]({url})\n"
                    : $"<img src=\"{url}\" alt=\"{alt}\" />\n");
            }
            catch (Exception ex) { _status.Text = "Insert image failed: " + ex.Message; }
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static bool FilesIdentical(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (fa.Length != fb.Length) return false;
                return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
            }
            catch { return false; }
        }

        // ---- Keypad ----

        // Richer, grouped catalog used by the editor's "Switch result to unit" menu.
        // Composite forms (m^2, kN*m, km/h) are parsed by Calcpad's UnitsParser as target units.
        private static readonly (string cat, string[] units)[] UnitSwitchCatalog =
        {
            ("Length",      new[] { "m", "cm", "mm", "km", "μm", "in", "ft", "yd", "mi" }),
            ("Area",        new[] { "m^2", "cm^2", "mm^2", "km^2", "in^2", "ft^2" }),
            ("Volume",      new[] { "m^3", "cm^3", "mm^3", "L", "mL", "in^3", "ft^3" }),
            ("Mass",        new[] { "kg", "g", "mg", "t", "lb", "oz" }),
            ("Force",       new[] { "N", "kN", "MN", "lbf", "kip" }),
            ("Line load",   new[] { "N/m", "kN/m", "N/mm", "kN/cm", "kip/ft", "lbf/in" }),
            ("Moment",      new[] { "N*m", "kN*m", "N*mm", "lbf*ft" }),
            ("Pressure",    new[] { "Pa", "kPa", "MPa", "GPa", "bar", "psi", "ksi" }),
            ("Temperature", new[] { "°C", "K", "°F", "°R", "Δ°C" }),
            ("Angle",       new[] { "°", "rad" }),
            ("Time",        new[] { "s", "ms", "min", "h" }),
            ("Frequency",   new[] { "Hz", "kHz", "MHz" }),
            ("Velocity",    new[] { "m/s", "km/h", "ft/s" }),
            ("Energy",      new[] { "J", "kJ", "MJ", "Wh", "kWh" }),
            ("Power",       new[] { "W", "kW", "MW", "hp" }),
            ("Electric",    new[] { "A", "mA", "V", "kV", "Ω" }),
        };

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
        { if (_mainGrid is not null && sender is ToggleButton t) SetSidePanel(0, _filePanel, _fileSplitter, ref _fileColWidth, 240, t.IsChecked == true); }
        private void KeypadToggle_Changed(object? sender, RoutedEventArgs e)
        { if (_keypadHost is not null && sender is ToggleButton t) _keypadHost.IsVisible = t.IsChecked == true; }
        private void AiToggle_Changed(object? sender, RoutedEventArgs e)
        { if (_mainGrid is not null && sender is ToggleButton t) SetSidePanel(4, _aiPanel, _aiSplitter, ref _aiColWidth, 340, t.IsChecked == true); }

        // Shows/hides a resizable side panel: toggles the panel + its splitter and collapses the
        // column to 0 (remembering the dragged width to restore it next time).
        private void SetSidePanel(int column, Border panel, GridSplitter splitter, ref double remembered, double fallback, bool visible)
        {
            var col = _mainGrid.ColumnDefinitions[column];
            if (visible)
            {
                col.Width = new global::Avalonia.Controls.GridLength(remembered > 40 ? remembered : fallback);
                panel.IsVisible = true;
                splitter.IsVisible = true;
            }
            else
            {
                if (col.ActualWidth > 40) remembered = col.ActualWidth; // keep the current width
                col.Width = new global::Avalonia.Controls.GridLength(0);
                panel.IsVisible = false;
                splitter.IsVisible = false;
            }
        }

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
            _aiPanel.Padding = new global::Avalonia.Thickness(6); // tighter
            var root = new DockPanel();

            Button Mini(string label, Action onClick)
            {
                var b = new Button { Content = label, FontSize = 11, MinHeight = 0, Padding = new global::Avalonia.Thickness(6, 2) };
                b.Click += (_, _) => onClick();
                return b;
            }

            // Compact top row: title + agent toggle together.
            var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new global::Avalonia.Thickness(0, 0, 0, 3) };
            DockPanel.SetDock(topRow, Dock.Top);
            topRow.Children.Add(new TextBlock { Text = "AI Assistant", FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var agent = new CheckBox { Content = "Agent mode", IsChecked = _agentMode, FontSize = 11, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            agent.IsCheckedChanged += (_, _) => _agentMode = agent.IsChecked == true;
            ToolTip.SetTip(agent, "Let the assistant read, evaluate and propose changes to the worksheet via tools");
            topRow.Children.Add(agent);
            root.Children.Add(topRow);

            _aiKey = new TextBox
            {
                Watermark = "ANTHROPIC_API_KEY",
                PasswordChar = '•',
                Text = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? DecryptKey(_state.ApiKey) ?? string.Empty,
                FontSize = 11, MinHeight = 0, Padding = new global::Avalonia.Thickness(4, 2),
                Margin = new global::Avalonia.Thickness(0, 0, 0, 3)
            };
            // Persist the key locally so it survives restarts (stored in state.json on this machine).
            _aiKey.LostFocus += (_, _) => SaveApiKey();
            DockPanel.SetDock(_aiKey, Dock.Top);
            root.Children.Add(_aiKey);

            // Composer at the very bottom.
            var bottom = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _aiInput = new TextBox { Watermark = "Ask about your worksheet…", AcceptsReturn = true, MinHeight = 46, FontSize = 11, Padding = new global::Avalonia.Thickness(4, 2), TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            _aiAttachInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#6B7280")), FontSize = 11 };
            buttons.Children.Add(Mini("Send", AiSend));
            buttons.Children.Add(Mini("Insert reply", () => { if (!string.IsNullOrEmpty(_lastAiReply)) InsertAtCaret(_lastAiReply); }));
            buttons.Children.Add(Mini("Clear", AiClear));
            buttons.Children.Add(Mini("📎", AiAttach));
            buttons.Children.Add(_aiAttachInfo);
            bottom.Children.Add(_aiInput);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

            // Proposed-change bar (above the composer): the assistant proposes, the user applies.
            _aiProposalPreview = new TextBlock { FontSize = 11, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#334155")), FontFamily = new global::Avalonia.Media.FontFamily("Cascadia Code,Consolas,monospace") };
            var propButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new global::Avalonia.Thickness(0, 3, 0, 0) };
            propButtons.Children.Add(Mini("✓ Apply", ApplyProposal));
            propButtons.Children.Add(Mini("✗ Dismiss", DismissProposal));
            var propInner = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
            propInner.Children.Add(new TextBlock { Text = "Proposed change — before/after (red = removed, green = added)", FontWeight = FontWeight.SemiBold, FontSize = 11, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#92400E")) });
            propInner.Children.Add(new ScrollViewer { Content = _aiProposalPreview, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            propInner.Children.Add(propButtons);
            _aiProposalBar = new Border
            {
                IsVisible = false,
                Background = new SolidColorBrush(Color.Parse("#FFFBEB")),
                BorderBrush = new SolidColorBrush(Color.Parse("#F59E0B")),
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(3),
                Padding = new global::Avalonia.Thickness(5),
                Margin = new global::Avalonia.Thickness(0, 3, 0, 3),
                Child = propInner
            };
            DockPanel.SetDock(_aiProposalBar, Dock.Bottom);
            root.Children.Add(_aiProposalBar);

            // Conversation = a stack of message cards (clear prompt/response separation), markdown-rendered.
            _aiMessages = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
            _aiScroll = new ScrollViewer
            {
                Content = _aiMessages,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new global::Avalonia.Thickness(0, 3, 0, 3)
            };
            root.Children.Add(_aiScroll);

            _aiPanel.Child = root;

            // Persistent memory: reload the previous conversation.
            var storePath = Path.Combine(Path.GetDirectoryName(StateFile)!, "ai-history.json");
            _aiStore = new JsonChatHistoryStore(storePath);
            foreach (var m in _aiStore.Load())
            {
                _aiHistory.Add(m);
                if (m.Role == AiRole.User) AddUserCard(m.Text);
                else { _aiCurrentMd = null; AppendAi(m.Text); }
            }
        }

        private void AiClear()
        {
            _aiHistory.Clear();
            _agentHistory.Clear();
            _aiAttachments.Clear();
            UpdateAttachInfo();
            _aiStore?.Clear();
            _aiCurrentMd = null; _aiCurrentBody = null;
            _aiMessages?.Children.Clear();
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
            if (_aiBusy || _aiInput is null || _aiMessages is null || _aiKey is null) return;
            var prompt = _aiInput.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) return;
            var key = _aiKey.Text?.Trim();
            if (string.IsNullOrEmpty(key)) { AppendAi("⚠ Set your Anthropic API key (or the ANTHROPIC_API_KEY env var).\n"); return; }
            SaveApiKey(); // remember it for next launch

            _aiBusy = true;
            _aiInput.Text = string.Empty;
            AddUserCard(prompt);
            // Inject the CURRENT worksheet once via the system prompt (regenerated every send),
            // so the model always sees the latest state without stale copies piling up in history.
            var options = new AiOptions { ApiKey = key, SystemPrompt = CalcpadSystemPrompt.Default + "\n\n" + SvgGuidance + "\n\n" + WorksheetContext() };

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

        // Pulls the error messages out of the rendered HTML as plain text. Errors appear both as
        // block <p class="err">…</p> (parse/line errors) and inline <span class="err">…</span>
        // (per-value errors: undefined variable, unit mismatch, ∞) — match both.
        private static System.Collections.Generic.List<string> ExtractRenderErrors(string html)
        {
            var list = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(html)) return list;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                html, "class=\"err\"[^>]*>(.*?)</(?:p|span)>", System.Text.RegularExpressions.RegexOptions.Singleline))
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

            EnsureAssistantCard();
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
                insertText: text => Dispatcher.UIThread.Invoke(() => ShowProposal(text)), // propose, user applies
                evaluate: src => Dispatcher.UIThread.Invoke(() => EvaluateForAssistant(src)),
                getResult: () => Dispatcher.UIThread.Invoke(() => _lastHtml));

            var caller = new AnthropicModelCaller(options);
            var conv = new AgenticConversation(caller, tools);
            conv.ToolInvoking += call => AppendAi($"\n\n🔧 *{call.Name}*\n\n");
            _agentTextSeen = false;
            conv.AssistantText += t => { if (!string.IsNullOrEmpty(t)) _agentTextSeen = true; AppendAi(t); };

            _agentHistory.Add(AgentMessage.User(prompt));
            EnsureAssistantCard();
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

        // Tells the assistant it can embed diagrams: Calcpad renders inline SVG in the worksheet.
        private const string SvgGuidance =
            "DIAGRAMS: Calcpad renders inline SVG directly in the worksheet and exports it to HTML/Word/PDF. " +
            "When a sketch would clarify the principle (free-body diagram, geometry, beam/section, circuit, " +
            "vector/force layout), include a small inline `<svg …>…</svg>` (with viewBox and width/height) right " +
            "where it helps, with short text labels (<text>) for the quantities. Keep it clean and to scale; " +
            "prefer lines/polylines/circles/text over heavy styling. Put the SVG on its own line in the worksheet.";

        private void SaveApiKey()
        {
            var key = string.IsNullOrWhiteSpace(_aiKey?.Text) ? null : _aiKey!.Text.Trim();
            if (key == DecryptKey(_state.ApiKey)) return; // unchanged
            _state.ApiKey = EncryptKey(key);
            SaveState();
        }

        // DPAPI (Windows, per-user) encrypts the stored key; a "dpapi:" prefix marks encrypted values.
        private static string? EncryptKey(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (!OperatingSystem.IsWindows()) return key; // DPAPI is Windows-only
            try
            {
                var enc = System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(key), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(enc);
            }
            catch { return key; }
        }

        private static string? DecryptKey(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // plaintext (old) / non-Windows
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var bytes = Convert.FromBase64String(stored.Substring(6));
                return Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }

        // The assistant proposes a COMPLETE new worksheet; the bar shows a before/after diff, and the
        // user applies it (replacing the document) or dismisses it.
        private string ShowProposal(string text)
        {
            _pendingProposal = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (_aiProposalPreview is not null) RenderDiff(_editor.Text ?? string.Empty, _pendingProposal);
            if (_aiProposalBar is not null) _aiProposalBar.IsVisible = true;
            return "Proposed to the user as a before/after diff. They will review and click Apply to update the worksheet.";
        }

        private void RenderDiff(string before, string after)
        {
            if (_aiProposalPreview is null) return;
            _aiProposalPreview.Inlines?.Clear();
            var inlines = _aiProposalPreview.Inlines!;
            var changes = 0;
            foreach (var (tag, line) in DiffLines(before, after))
            {
                var (prefix, color) = tag switch
                {
                    '+' => ("+ ", "#166534"),  // added  (green)
                    '-' => ("- ", "#B91C1C"),  // removed (red)
                    _ => ("  ", "#94A3B8")      // context (grey)
                };
                if (tag != ' ') changes++;
                inlines.Add(new global::Avalonia.Controls.Documents.Run(prefix + line) { Foreground = new SolidColorBrush(Color.Parse(color)) });
                inlines.Add(new global::Avalonia.Controls.Documents.LineBreak());
            }
            if (changes == 0)
                inlines.Add(new global::Avalonia.Controls.Documents.Run("(no changes)") { Foreground = new SolidColorBrush(Color.Parse("#94A3B8")) });
        }

        // Unified line diff (LCS). Returns each line tagged '+' added, '-' removed, ' ' unchanged.
        private static System.Collections.Generic.List<(char tag, string line)> DiffLines(string before, string after)
        {
            var a = before.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var b = after.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int n = a.Length, m = b.Length;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            var res = new System.Collections.Generic.List<(char, string)>();
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[x] == b[y]) { res.Add((' ', a[x])); x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) { res.Add(('-', a[x])); x++; }
                else { res.Add(('+', b[y])); y++; }
            }
            while (x < n) { res.Add(('-', a[x])); x++; }
            while (y < m) { res.Add(('+', b[y])); y++; }
            return res;
        }

        private void ApplyProposal()
        {
            var text = _pendingProposal;
            _pendingProposal = null;
            if (_aiProposalBar is not null) _aiProposalBar.IsVisible = false; // always clear the bar
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                _editor.Document.Replace(0, _editor.Document.TextLength, text); // full replace, undoable
                _editor.CaretOffset = 0;
                AppendAi("\n\n✓ *Applied — the worksheet was updated (Ctrl+Z to revert).*\n\n");
                ScheduleRender();
            }
            catch (Exception ex) { _status.Text = "Apply failed: " + ex.Message; }
        }

        private void DismissProposal()
        {
            _pendingProposal = null;
            if (_aiProposalBar is not null) _aiProposalBar.IsVisible = false;
            AppendAi("\n\n✗ *Dismissed the proposed change.*\n\n");
        }

        // Appends streamed text to the current assistant card (creating one if needed).
        private void AppendAi(string text)
        {
            if (_aiMessages is null) return;
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => AppendAi(text)); return; }
            EnsureAssistantCard();
            _aiCurrentMd!.Append(text);
            if (_aiRenderQueued) return;            // coalesce re-renders while streaming tokens
            _aiRenderQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _aiRenderQueued = false;
                try { if (_aiCurrentBody is not null) _aiCurrentBody.Markdown = _aiCurrentMd!.ToString(); } catch { }
                ScrollChatToEnd();
            }, DispatcherPriority.Background);
        }

        // A message card: distinct background for You vs Assistant + a Copy button (clear separation).
        private Control MakeCard(bool user, Control body, Func<string> copyText)
        {
            var label = new TextBlock { Text = user ? "You" : "Assistant", FontWeight = FontWeight.SemiBold, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(user ? "#1D4ED8" : "#0F766E")) };
            var copy = new Button { Content = "📋", FontSize = 10, MinHeight = 0, Padding = new global::Avalonia.Thickness(4, 0), Background = global::Avalonia.Media.Brushes.Transparent, BorderThickness = new global::Avalonia.Thickness(0) };
            ToolTip.SetTip(copy, "Copy this message");
            copy.Click += async (_, _) =>
            {
                var top = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (top?.Clipboard is not null) { await top.Clipboard.SetTextAsync(copyText()); _status.Text = "Copied message"; }
            };
            var head = new DockPanel { Margin = new global::Avalonia.Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(copy, Dock.Right);
            head.Children.Add(copy);
            head.Children.Add(label);
            var inner = new StackPanel { Orientation = Orientation.Vertical };
            inner.Children.Add(head);
            inner.Children.Add(body);
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse(user ? "#EFF6FF" : "#FFFFFF")),
                BorderBrush = new SolidColorBrush(Color.Parse("#E2E4E8")),
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(6, 4),
                Child = inner
            };
        }

        private void AddUserCard(string prompt)
        {
            _aiCurrentMd = null; _aiCurrentBody = null; // the reply starts a fresh assistant card
            var body = new SelectableTextBlock { Text = prompt, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
            _aiMessages!.Children.Add(MakeCard(true, body, () => prompt));
            ScrollChatToEnd();
        }

        private void EnsureAssistantCard()
        {
            if (_aiCurrentMd is not null) return;
            _aiCurrentMd = new StringBuilder();
            _aiCurrentBody = new global::Markdown.Avalonia.MarkdownScrollViewer { Markdown = string.Empty };
            var md = _aiCurrentMd;
            _aiMessages!.Children.Add(MakeCard(false, _aiCurrentBody, () => md.ToString()));
            ScrollChatToEnd();
        }

        private void ScrollChatToEnd()
        {
            if (_aiScroll is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                try { _aiScroll.Offset = new global::Avalonia.Vector(0, Math.Max(0, _aiScroll.Extent.Height - _aiScroll.Viewport.Height)); } catch { }
            }, DispatcherPriority.Background);
        }

        // ---- File browser ----

        private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Add a folder of Calcpad worksheets",
                    AllowMultiple = true
                });
                if (folders.Count == 0) return;
                foreach (var folder in folders)
                {
                    var dir = folder.Path.LocalPath;
                    if (Directory.Exists(dir) && !_userFolders.Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase)))
                        _userFolders.Add(dir);     // ADD (keep existing folders), don't replace
                }
                RefreshBrowser();
                SaveFolders();
            }
            catch (Exception ex) { _status.Text = "Folder failed: " + ex.Message; }
        }

        private void RemoveUserFolder(string dir)
        {
            _userFolders.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
            RefreshBrowser();
            SaveFolders();
            _status.Text = "Removed from browser: " + Path.GetFileName(dir);
        }

        // Forces a full browser rebuild (also re-arms the folder watchers).
        private void RefreshBrowser()
        {
            _currentTreeRoot = null;
            UpdateFileBrowserForActiveDoc();
            RearmFolderWatchers();
        }

        // Watch the user's folders so external file add/rename/delete shows up without re-opening.
        private void RearmFolderWatchers()
        {
            foreach (var w in _folderWatchers) { try { w.Dispose(); } catch { } }
            _folderWatchers.Clear();
            foreach (var dir in _userFolders)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var w = new System.IO.FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName | System.IO.NotifyFilters.LastWrite,
                        EnableRaisingEvents = true
                    };
                    void OnChange(object? _, System.IO.FileSystemEventArgs __) => Dispatcher.UIThread.Post(DebouncedBrowserRefresh);
                    w.Created += OnChange; w.Deleted += OnChange; w.Renamed += (_, __) => Dispatcher.UIThread.Post(DebouncedBrowserRefresh);
                    _folderWatchers.Add(w);
                }
                catch { /* watching is best-effort */ }
            }
        }

        // Coalesce bursts of filesystem events into a single rebuild.
        private void DebouncedBrowserRefresh()
        {
            _browserRefreshTimer ??= new global::Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _browserRefreshTimer.Tick -= BrowserRefreshTick;
            _browserRefreshTimer.Tick += BrowserRefreshTick;
            _browserRefreshTimer.Stop();
            _browserRefreshTimer.Start();
        }

        private void BrowserRefreshTick(object? sender, EventArgs e)
        {
            _browserRefreshTimer?.Stop();
            _currentTreeRoot = null;
            UpdateFileBrowserForActiveDoc();
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
            _state.RecentFiles ??= new(); // old state files won't have the field
            _state.Folders ??= new();
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

        private void SaveFolders()
        {
            _state.Folders = new List<string>(_userFolders);
            _state.LastFolder = _userFolders.LastOrDefault(); // back-compat
            SaveState();
        }

        // ---- Recent files ----

        private const int MaxRecent = 12;

        private void AddRecent(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var full = Path.GetFullPath(path);
            _state.RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            _state.RecentFiles.Insert(0, full);
            if (_state.RecentFiles.Count > MaxRecent)
                _state.RecentFiles.RemoveRange(MaxRecent, _state.RecentFiles.Count - MaxRecent);
            SaveState();
            BuildRecentMenu();
        }

        private void BuildRecentMenu()
        {
            var items = new List<object>();
            var recents = _state.RecentFiles ?? new List<string>();
            if (recents.Count == 0)
            {
                items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            }
            else
            {
                for (int i = 0; i < recents.Count; i++)
                {
                    var path = recents[i];
                    var mi = new MenuItem
                    {
                        Header = $"_{(i + 1) % 10}  {Path.GetFileName(path)}",
                        // Avalonia uses '_' as a mnemonic; the full path tooltip stays readable.
                    };
                    ToolTip.SetTip(mi, path);
                    mi.Click += async (_, _) => await OpenRecent(path);
                    items.Add(mi);
                }
                items.Add(new Separator());
                var clear = new MenuItem { Header = "_Clear recent" };
                clear.Click += (_, _) => { _state.RecentFiles!.Clear(); SaveState(); BuildRecentMenu(); };
                items.Add(clear);
            }
            _recentMenu.ItemsSource = items;
        }

        private async System.Threading.Tasks.Task OpenRecent(string path)
        {
            if (!File.Exists(path))
            {
                _status.Text = "File not found: " + path;
                _state.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                SaveState();
                BuildRecentMenu();
                return;
            }
            try
            {
                if (string.Equals(Path.GetExtension(path), ".cpdz", StringComparison.OrdinalIgnoreCase))
                {
                    OpenPackage(path);
                }
                else
                {
                    _packagePath = null; _packageWorkDir = null;
                    OpenOrActivate(path, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace("\r", "\n"));
                }
                AddRecent(path);
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        private void RestoreLastFolder()
        {
            _userFolders.Clear();
            var saved = (_state.Folders is { Count: > 0 } ? _state.Folders
                        : _state.LastFolder is string lf ? new List<string> { lf } : new List<string>());
            foreach (var dir in saved)
                if (Directory.Exists(dir) && !_userFolders.Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase)))
                    _userFolders.Add(dir);
            RefreshBrowser();
        }

        private sealed class AppState
        {
            public string? LastFolder { get; set; }       // legacy single folder (migrated into Folders)
            public List<string> Folders { get; set; } = new(); // all folders kept in the browser
            public string? TemplateFolder { get; set; }  // folder/share holding the Word templates
            public string? LastTemplate { get; set; }    // selected Word template used for export
            public bool UseTemplate { get; set; }         // master toggle for template-based export
            public string? ApiKey { get; set; }           // saved Anthropic API key
            public List<string> RecentFiles { get; set; } = new(); // most-recently opened .cpd / .cpdz, newest first
        }

        private const int MaxTreeDepth = 16;

        // Builds a TreeViewItem for a directory: sub-folders first, then *.cpd files (each expandable
        // into its functions), honouring the live filter. Guards against symlink loops / deep nesting.
        private TreeViewItem? BuildFileNode(DirectoryInfo dir, int depth, bool isRoot = false)
        {
            if (depth > MaxTreeDepth) return null;
            TreeViewItem node;
            try
            {
                node = new TreeViewItem { Header = Row("○", ColFolder, dir.Name, bold: isRoot), Tag = new DirRef(dir.FullName) };
                AttachFolderMenu(node, dir.FullName, isRoot);
                foreach (var sub in dir.GetDirectories().OrderBy(d => d.Name))
                {
                    if ((sub.Attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) continue; // skip hidden + junctions/symlinks
                    var child = BuildFileNode(sub, depth + 1);
                    if (child is not null) node.Items.Add(child);
                }
                foreach (var file in dir.GetFiles("*.cpd").OrderBy(f => f.Name))
                {
                    var fns = ScanFunctions(file.FullName).ToList();
                    if (!PassesFilter(file.Name, fns)) continue;
                    var fileNode = new TreeViewItem { Header = Row("●", ColCpd, file.Name), Tag = file.FullName };
                    AttachFileMenu(fileNode, file.FullName, isPackageLib: false);
                    EnableDrag(fileNode, file.FullName);
                    foreach (var fn in fns)
                        if (PassesFilter(fn.signature)) { var fi = FunctionItem(file.FullName, fn); AttachFunctionMenu(fi, file.FullName, fn.call); EnableDrag(fi, new FunctionRef(file.FullName, fn.call)); fileNode.Items.Add(fi); }
                    node.Items.Add(fileNode);
                }
            }
            catch { return null; } // unreadable folders are skipped
            // A root stays visible even when empty (so the user can remove it); nested empties are hidden while filtering.
            return (!isRoot && _fileFilterText.Length > 0 && node.Items.Count == 0) ? null : node;
        }

        // Right-click a folder node → open / copy / (roots: refresh, remove from browser).
        private void AttachFolderMenu(TreeViewItem node, string path, bool isRoot = false)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItemFor("Open in Explorer", () => RevealPath(path, select: false)));
            menu.Items.Add(MenuItemFor("Copy path", () => _ = CopyTextToClipboard(path)));
            if (isRoot)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuItemFor("Refresh", RefreshBrowser));
                menu.Items.Add(MenuItemFor("Remove from browser", () => RemoveUserFolder(path)));
            }
            node.ContextMenu = menu;
        }

        // Right-click a .cpd file → open / insert / copy / reveal.
        private void AttachFileMenu(TreeViewItem node, string path, bool isPackageLib)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItemFor(isPackageLib ? "Open (read-only)" : "Open", () => _ = OpenFromBrowser(path, isPackageLib)));
            menu.Items.Add(MenuItemFor("Insert #include", () => _ = InsertIncludeFor(path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Copy path", () => _ = CopyTextToClipboard(path)));
            menu.Items.Add(MenuItemFor("Reveal in Explorer", () => RevealPath(path, select: true)));
            node.ContextMenu = menu;
        }

        // Right-click a function → insert its call (and its #include) or just the #include.
        private void AttachFunctionMenu(TreeViewItem node, string file, string call)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItemFor("Insert call", () => _ = InsertFunction(new FunctionRef(file, call))));
            menu.Items.Add(MenuItemFor("Insert #include only", () => _ = InsertIncludeFor(file)));
            menu.Items.Add(MenuItemFor("Copy name", () => _ = CopyTextToClipboard(call)));
            node.ContextMenu = menu;
        }

        private static MenuItem MenuItemFor(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            return mi;
        }

        private static void RevealPath(string path, bool select)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }

        private async System.Threading.Tasks.Task CopyTextToClipboard(string text)
        {
            var cb = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb is not null) { await cb.SetTextAsync(text); _status.Text = "Copied: " + text; }
        }

        private async System.Threading.Tasks.Task InsertIncludeFor(string path)
            => InsertAtCaret($"#include {BuildLinkedPath(await MaybeCopyIntoFolder(path))}\n");

        // ---- Drag a file/function from the browser onto the editor to insert it ----

        private const string DragFormat = "calcpad/insert";

        private void EnableDrag(Control node, object payload)
        {
            var start = new global::Avalonia.Point();
            var armed = false;
            node.PointerPressed += (_, e) =>
            {
                armed = e.GetCurrentPoint(node).Properties.IsLeftButtonPressed;
                start = e.GetPosition(node);
            };
            node.PointerReleased += (_, _) => armed = false;
            node.PointerMoved += async (_, e) =>
            {
                if (!armed) return;
                var p = e.GetPosition(node);
                if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
                armed = false;
                var data = new global::Avalonia.Input.DataObject();
                data.Set(DragFormat, payload);
                try { await global::Avalonia.Input.DragDrop.DoDragDrop(e, data, global::Avalonia.Input.DragDropEffects.Copy); } catch { }
            };
        }

        private void InitEditorDrop()
        {
            global::Avalonia.Input.DragDrop.SetAllowDrop(_editor, true);
            _editor.AddHandler(global::Avalonia.Input.DragDrop.DragOverEvent, (object? _, global::Avalonia.Input.DragEventArgs e) =>
            {
                e.DragEffects = e.Data.Contains(DragFormat)
                    ? global::Avalonia.Input.DragDropEffects.Copy
                    : global::Avalonia.Input.DragDropEffects.None;
            });
            _editor.AddHandler(global::Avalonia.Input.DragDrop.DropEvent, async (object? _, global::Avalonia.Input.DragEventArgs e) =>
            {
                var payload = e.Data.Get(DragFormat);
                if (payload is null) return;
                var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor.TextArea.TextView));
                if (pos.HasValue) { _editor.CaretOffset = _editor.Document.GetOffset(pos.Value.Location); _editor.SelectionLength = 0; }
                if (payload is FunctionRef fr) await InsertFunction(fr);
                else if (payload is string path) await InsertIncludeFor(path);
            });
        }

        private sealed record FunctionRef(string FilePath, string Call);
        private sealed record DirRef(string Path);

        private static readonly System.Text.RegularExpressions.Regex MacroDefRegex =
            new(@"^\s*#def\s+([A-Za-z_][A-Za-z0-9_]*\$?)\s*(?:\(([^)]*)\))?", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex MathFuncRegex =
            new(@"^\s*([A-Za-z_][A-Za-z_0-9]*)\s*\(([^)]*)\)\s*=", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Scans a .cpd for #def macros and math function definitions (ported from the WPF LibraryScanner).
        private static IEnumerable<(string signature, string call)> ScanFunctions(string path)
        {
            string[] lines;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 2_000_000) yield break; // don't stall the UI scanning multi-MB files
                lines = File.ReadAllLines(path);
            }
            catch { yield break; }
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
            // Live-relabel the Insert button to what it will do (file → #include, function → call).
            if (_insertFileBtn is not null)
                (_insertFileBtn.Content, _insertFileBtn.IsEnabled) = _selectedTag switch
                {
                    FunctionRef => ("Insert call", true),
                    string => ("Insert #include", true),
                    _ => ("Insert", false)
                };
        }

        private void FileTree_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) InsertFile_Click(this, new RoutedEventArgs());
                else OpenSelectedFile();
                e.Handled = true;
            }
        }

        private async void OpenSelectedFile()
        {
            if (_selectedTag is FunctionRef fr) { await InsertFunction(fr); return; }
            if (_selectedTag is DirRef) return;                 // folders expand via the chevron
            if (_selectedTag is not string path) return;
            var isLib = _active?.PackageWorkDir is string wd && path.StartsWith(wd, StringComparison.OrdinalIgnoreCase);
            await OpenFromBrowser(path, isLib);
        }

        // Opens a file picked in the browser. .cpdz → package; non-text asset → reveal; a bundled
        // library → read-only view (editing a temp-extracted lib would be silently lost).
        private async System.Threading.Tasks.Task OpenFromBrowser(string path, bool isPackageLib)
        {
            try
            {
                var ext = Path.GetExtension(path);
                if (string.Equals(ext, ".cpdz", StringComparison.OrdinalIgnoreCase)) { OpenPackage(path); return; }
                if (!string.Equals(ext, ".cpd", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
                {
                    RevealPath(path, select: true);              // images, template, manifest…
                    return;
                }
                var text = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace("\r", "\n");
                if (isPackageLib)
                {
                    // Keep package mode intact; open the lib as a read-only tab so edits can't vanish.
                    AddTab(path, text);
                    if (_active is not null) _active.ReadOnly = true;
                    ApplyReadOnly();
                    _status.Text = "Read-only (bundled in package): " + Path.GetFileName(path);
                    return;
                }
                _packagePath = null; _packageWorkDir = null;
                OpenOrActivate(path, text);
                AddRecent(path);
            }
            catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
        }

        private void ApplyReadOnly() => _editor.IsReadOnly = _active?.ReadOnly == true;

        // Insert button: a function's call (+ #include of its file) or an #include for a file.
        private async void InsertFile_Click(object? sender, RoutedEventArgs e)
        {
            switch (_selectedTag)
            {
                case FunctionRef fr: await InsertFunction(fr); break;
                case string path: await InsertIncludeFor(path); break;
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
            if (_tableSyncing || _tableInsertMode || _switching || _tableFollowQueued || _inputPanelActive) return;
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
            _tableActive = false; _tableInsertMode = false; _inputPanelActive = false;
            _tableStartLine = _tableEndLine = -1;
            _tableOverlay.IsVisible = false;
        }

        // ---- Inline input-field panel (quick name / value / unit entry, like the table editor) ----

        private bool _inputPanelActive;

        // Context-aware input action for the right-click menu: edit an existing field in the panel,
        // quick-wrap a selected number / assignment inline, or open the panel for a fresh field.
        private void InputFieldSmart()
        {
            var lineText = CaretLineText();
            if (System.Text.RegularExpressions.Regex.IsMatch(lineText, @"=\s*\?\{")) { OpenInputPanel(); return; }
            var sel = (_editor.SelectedText ?? string.Empty).Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(sel, @"^-?\d+(?:[.,]\d+)?$") ||
                System.Text.RegularExpressions.Regex.IsMatch(lineText, @"^\s*[A-Za-z_][\w\.]*\s*(?::=|=)\s*-?\d"))
            { InsertInputField(); return; }   // inline quick wrap/toggle
            OpenInputPanel();                 // build a new field in the panel
        }

        // Opens a small docked panel below the code to enter an input field fast: name, default
        // value and (compatible) unit — composites like kN/m accepted — then Insert at the caret.
        private void OpenInputPanel()
        {
            if (_inputPanelActive) { CloseTableEditor(); return; } // toggle off
            // Make sure the table editor isn't bound underneath us.
            _tableActive = false; _tableInsertMode = false;
            _inputPanelActive = true;

            var md = CurrentMarkdownFull();

            // If the caret sits on an existing input field, bind to it: pre-fill the fields and
            // replace that line on insert (edit in place), like the table editor binds to a table.
            var caretLine = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            var caretText = _editor.Document.GetText(caretLine.Offset, caretLine.Length);
            var bm = System.Text.RegularExpressions.Regex.Match(caretText,
                @"^(\s*)([A-Za-z_][\w\.]*)\s*(?::=|=)\s*\?\{([^}]*)\}\s*([^'""\n]*?)\s*(?:['""](.*))?$");
            var bound = bm.Success;
            var boundLine = bound ? caretLine : null;

            var nameBox = new TextBox { Text = bound ? bm.Groups[2].Value : "x", Width = 90, Watermark = "name" };
            var valueBox = new TextBox { Text = bound ? bm.Groups[3].Value.Trim() : "0", Width = 70, Watermark = "value" };
            var unitBox = new AutoCompleteBox
            {
                Width = 150,
                Watermark = "unit (e.g. kN/m)",
                FilterMode = AutoCompleteFilterMode.Contains,
                ItemsSource = AllCatalogUnits(),
                MinimumPrefixLength = 0,
                Text = bound ? bm.Groups[4].Value.Trim() : string.Empty
            };
            var commentBox = new TextBox { Text = bound ? bm.Groups[5].Value.Trim() : string.Empty, Width = 150, Watermark = "comment (optional)" };

            void DoInsert()
            {
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? "x" : nameBox.Text.Trim();
                var val = (valueBox.Text ?? "0").Trim().Replace(',', '.');
                if (val.Length == 0) val = "0";
                // The ?{default} brace must hold a plain number — an expression/unit there is a
                // malformed field. Flag it instead of silently emitting broken code.
                if (!System.Text.RegularExpressions.Regex.IsMatch(val, @"^-?\d+(?:\.\d+)?$"))
                {
                    valueBox.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
                    _status.Text = "Default must be a plain number (e.g. 6 or 10.5) — put units in the unit box";
                    _status.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
                    valueBox.Focus();
                    return;
                }
                var unit = (unitBox.Text ?? string.Empty).Trim();
                var comment = (commentBox.Text ?? string.Empty).Trim();
                var assign = md ? " := " : " = ";
                var field = $"{name}{assign}?{{{val}}}{unit}";
                if (comment.Length > 0) field += "  '" + comment;

                if (bound && boundLine is not null)
                {
                    EnsureInputDirectiveAbove(boundLine);           // idempotent (already present)
                    _editor.Document.Replace(boundLine.Offset, boundLine.Length, field);
                }
                else
                {
                    var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
                    EnsureInputDirectiveAbove(line);
                    line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
                    var lineText = _editor.Document.GetText(line.Offset, line.Length);
                    var off = _editor.CaretOffset;
                    var prefix = lineText.Trim().Length > 0 ? "\n" : "";
                    _editor.Document.Insert(off, prefix + field);
                }
                CloseTableEditor();
                _computeInputs = false;
                _editor.Focus();
                StatusHold(bound ? "Input field updated — press Compute (F5) to calculate"
                                 : "Input field inserted — press Compute (F5) to calculate");
                UpdateRunButton();
                ScheduleRender();
            }

            Button Tool(string label, Action act)
            { var b = new Button { Content = label, Margin = new global::Avalonia.Thickness(1), Padding = new global::Avalonia.Thickness(6, 1) }; b.Click += (_, _) => act(); return b; }

            void OnKey(object? _, global::Avalonia.Input.KeyEventArgs e)
            {
                if (e.Key == global::Avalonia.Input.Key.Enter) { DoInsert(); e.Handled = true; }
                else if (e.Key == global::Avalonia.Input.Key.Escape) { CloseTableEditor(); _editor.Focus(); e.Handled = true; }
            }
            nameBox.KeyDown += OnKey; valueBox.KeyDown += OnKey; commentBox.KeyDown += OnKey;

            TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new global::Avalonia.Thickness(8, 0, 4, 0) };

            var row1 = new WrapPanel { Orientation = Orientation.Horizontal };
            row1.Children.Add(new TextBlock { Text = bound ? "Edit input" : "Input", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new global::Avalonia.Thickness(0, 0, 4, 0) });
            row1.Children.Add(nameBox);
            row1.Children.Add(Lbl(md ? ":=" : "="));
            row1.Children.Add(valueBox);
            row1.Children.Add(Lbl("unit"));
            row1.Children.Add(unitBox);
            row1.Children.Add(Lbl("note"));
            row1.Children.Add(commentBox);

            var row2 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new global::Avalonia.Thickness(0, 2, 0, 0) };
            row2.Children.Add(Tool(bound ? "Update field" : "Insert at caret", DoInsert));
            row2.Children.Add(Tool("Close", () => { CloseTableEditor(); _editor.Focus(); }));
            row2.Children.Add(new TextBlock
            {
                Text = "Value = a plain number (6, 10.5); units go in the unit box (composites like kN/m ok). Adds #input so the box is editable. Enter = insert · Esc = close.",
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new global::Avalonia.Thickness(8, 0, 0, 0), Foreground = new SolidColorBrush(Color.Parse("#6B7280"))
            });

            var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
            panel.Children.Add(row1);
            panel.Children.Add(row2);
            _tableOverlay.Child = panel;
            _tableOverlay.IsVisible = true;
            Dispatcher.UIThread.Post(() => nameBox.Focus(), DispatcherPriority.Input);
        }

        // Flat list of catalog units (simple + composite) for the input panel's unit suggestions.
        private static string[] AllCatalogUnits() =>
            UnitSwitchCatalog.SelectMany(c => c.units).Distinct().ToArray();

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

            Control Sep() => new Border
            {
                Width = 1, Background = new SolidColorBrush(Color.Parse("#D5D8DD")),
                Margin = new global::Avalonia.Thickness(2, 1, 14, 1)
            };

            var subst = Check("Substitute", _substitute, v => _substitute = v);
            ToolTip.SetTip(subst, "Show computed values substituted into each formula (a = 3 instead of just a)");
            bar.Children.Add(subst);
            var fmtEq = Check("Format equations", _formatEquations, v => _formatEquations = v);
            ToolTip.SetTip(fmtEq, "Render math as typeset equations (fractions, powers) rather than plain text");
            bar.Children.Add(fmtEq);
            var cplx = Check("Complex", _complex, v => _complex = v);
            ToolTip.SetTip(cplx, "Enable complex-number arithmetic (results may have an imaginary part)");
            bar.Children.Add(cplx);
            bar.Children.Add(Sep());
            var adapt = Check("Adaptive plots", _adaptivePlot, v => _adaptivePlot = v);
            ToolTip.SetTip(adapt, "Add extra sample points where a plotted curve bends sharply, for a smoother chart");
            bar.Children.Add(adapt);
            var embed = Check("Embed images", _embedImages, v => _embedImages = v);
            ToolTip.SetTip(embed, "Embed plots/images in the HTML as base64; save to files if unchecked");
            bar.Children.Add(embed);
            bar.Children.Add(Sep());
            var rel = Check("Relative paths", _relativePaths, v => _relativePaths = v);
            ToolTip.SetTip(rel, "Insert #include and image paths relative to the current worksheet (not absolute)");
            bar.Children.Add(rel);
            var auto = Check("Auto-calc", _autoCalc, v => _autoCalc = v);
            ToolTip.SetTip(auto, "Typing 'name =' on a line auto-upgrades it to a calculation (name := …)");
            bar.Children.Add(auto);
            bar.Children.Add(Sep());

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

            bar.Children.Add(Sep());
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

            var input = MakeButton("▭ Field", OpenInputPanel);
            ToolTip.SetTip(input, "Insert an editable input field: name / value / unit (composites like kN/m ok). Adds #input so the box is editable; press Compute (F5) to calculate. (Right-click in the code for the quick wrap/toggle.)");
            bar.Children.Add(input);

            var image = MakeButton("▣ Image", () => _ = InsertImageAsync());
            ToolTip.SetTip(image, "Pick an image and insert it. Saved worksheets copy it into an Images/ folder and link it relatively; markdown uses ![…](…), classic uses <img>.");
            bar.Children.Add(image);

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
                // A heading, directive, image or table isn't math — $-wrapping it produces broken
                // output (e.g. "# Title" → "$# Title$"). Leave those lines untouched.
                if (body.Length == 0 ||
                    body.StartsWith("#") ||                                   // heading or #directive
                    body.StartsWith("![") || body.StartsWith("<img", StringComparison.OrdinalIgnoreCase) ||
                    body.StartsWith("|"))                                     // markdown table row
                {
                    _status.Text = "That line isn't a calculation (heading / directive / image / table)";
                    _editor.Focus();
                    return;
                }
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

        // Forces the current line (or selection) to render as literal TEXT, one-way (unlike the
        // ↔ Calc toggle). Markdown: strips $…$ / $$…$$, turns ':=' into '=' and drops a trailing
        // lone '=' so nothing is evaluated. Classic: prefixes a '  comment marker.
        private void ForceToText()
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

            var leading = text.Length - text.TrimStart().Length;
            var indent = text.Substring(0, leading);
            var body = text.Substring(leading).TrimEnd();
            if (body.Length == 0) return;

            string replacement;
            if (CurrentMarkdownFull())
            {
                if (body.StartsWith("$$") && body.EndsWith("$$") && body.Length >= 4)
                    body = body.Substring(2, body.Length - 4).Trim();
                else if (body.StartsWith("$") && body.EndsWith("$") && body.Length >= 2)
                    body = body.Substring(1, body.Length - 2).Trim();
                body = System.Text.RegularExpressions.Regex.Replace(body, @"\s*:=\s*", " = ");
                if (body.EndsWith("=") && !body.EndsWith("==") && !body.EndsWith(">=") &&
                    !body.EndsWith("<=") && !body.EndsWith("!="))
                    body = body[..^1].TrimEnd();
                replacement = body;
            }
            else
            {
                replacement = body.StartsWith("'") || body.StartsWith("\"") ? body : "'" + body;
            }

            _editor.Document.Replace(start, length, indent + replacement);
            _editor.Focus();
            StatusHold("Line converted to text (one-way) — Ctrl+Z to undo");
            ScheduleRender();
        }

        // ---- Quick input field (?) ----

        // Inserts/toggles a Calcpad input field (?{default}unit) and guarantees an #input
        // directive governs it, so the preview shows an editable box. Mode-aware: markdown
        // assigns with ':=', classic with '='. Behaviour, in order:
        //   • a number is selected        → wrap it:  6 → ?{6}
        //   • caret on "name =/:= value"  → toggle the right-hand number to/from ?{…}
        //   • otherwise                    → insert a fresh "name = ?{0}" field, name selected
        private void InsertInputField()
        {
            try
            {
                var md = CurrentMarkdownFull();
                var assign = md ? " := " : " = ";
                var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
                var lineText = _editor.Document.GetText(line.Offset, line.Length);
                var sel = _editor.SelectedText ?? string.Empty;

                // 1) Wrap a selected number.
                if (sel.Trim().Length > 0 &&
                    System.Text.RegularExpressions.Regex.IsMatch(sel.Trim(), @"^-?\d+(?:[.,]\d+)?$"))
                {
                    var def = sel.Trim().Replace(',', '.');
                    _editor.Document.Replace(_editor.SelectionStart, _editor.SelectionLength, $"?{{{def}}}");
                    EnsureInputDirectiveAbove(line);
                    AfterInputInsert("Input field added — press Compute (F5) to calculate");
                    return;
                }

                // 2) Toggle the right-hand number of an assignment line.
                var m = System.Text.RegularExpressions.Regex.Match(lineText,
                    @"^(\s*[A-Za-z_][\w\.]*\s*(?::=|=)\s*)(?:\?\{\s*(-?\d+(?:\.\d+)?)\s*\}|(-?\d+(?:\.\d+)?))(.*)$");
                if (m.Success)
                {
                    var revert = m.Groups[2].Success;                   // currently ?{num}
                    var num = revert ? m.Groups[2].Value : m.Groups[3].Value;
                    var newRhs = revert ? num : $"?{{{num}}}";
                    _editor.Document.Replace(line.Offset, line.Length,
                        m.Groups[1].Value + newRhs + m.Groups[4].Value);
                    if (!revert) EnsureInputDirectiveAbove(line);
                    else RemoveOrphanInputDirective();
                    AfterInputInsert(revert
                        ? "Input field removed"
                        : "Input field added — press Compute (F5) to calculate");
                    return;
                }

                // 3) Insert a fresh field; select the placeholder name for immediate typing.
                EnsureInputDirectiveAbove(line);
                line = _editor.Document.GetLineByOffset(_editor.CaretOffset); // offsets may have shifted
                lineText = _editor.Document.GetText(line.Offset, line.Length);
                var off = _editor.CaretOffset;
                var prefix = lineText.Trim().Length > 0 ? "\n" : "";
                var text = prefix + $"x{assign}?{{0}}";
                _editor.Document.Insert(off, text);
                var nameOff = off + prefix.Length;
                _editor.CaretOffset = nameOff + 1;
                _editor.SelectionStart = nameOff;
                _editor.SelectionLength = 1;                            // "x" selected → type the name
                AfterInputInsert("Name the input, then press Compute (F5) to calculate");
            }
            catch (Exception ex) { _status.Text = "Insert input failed: " + ex.Message; }
        }

        // Ensures a bare #input directive appears above the given line (inserting one at the
        // line's start if absent), so the field renders as an editable box in the preview.
        private void EnsureInputDirectiveAbove(AvaloniaEdit.Document.DocumentLine line)
        {
            var upto = _editor.Document.GetText(0, line.Offset);
            foreach (var raw in upto.Split('\n'))
            {
                var t = raw.TrimStart();
                if (t.StartsWith("#input", StringComparison.OrdinalIgnoreCase) &&
                    (t.Length == 6 || t[6] is ' ' or '\t' or '\'' or '"'))
                    return;
            }
            _editor.Document.Insert(line.Offset, "#input\n");
        }

        // After a field is removed, drop any now-orphaned bare #input / #pause directive lines
        // (when no ?{…} field remains), so the directive doesn't linger and freeze the sheet.
        private void RemoveOrphanInputDirective()
        {
            if (_editor.Text?.Contains("?{") == true) return; // fields remain → keep the directive
            for (var n = _editor.Document.LineCount; n >= 1; n--)
            {
                var dl = _editor.Document.GetLineByNumber(n);
                var t = _editor.Document.GetText(dl.Offset, dl.Length).TrimStart();
                if ((t.StartsWith("#input", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("#pause", StringComparison.OrdinalIgnoreCase)) &&
                    (t.Length == 6 || t[6] is ' ' or '\t' or '\'' or '"'))
                    _editor.Document.Remove(dl.Offset, dl.TotalLength); // whole line incl. newline
            }
        }

        private void AfterInputInsert(string status)
        {
            _computeInputs = false;   // show the editable form for the new field
            _editor.Focus();
            StatusHold(status);
            UpdateRunButton();
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

        private void Run_Click(object? sender, RoutedEventArgs e)
        {
            // For an #input worksheet, F5 toggles between the editable form and the computed
            // result: form → read the boxes and calculate; computed → back to the form.
            if (DocHasInputForm())
            {
                if (_computeInputs)
                {
                    _computeInputs = false;
                    _inputValues.Clear();
                    Render();
                    StatusHold("Editing inputs — change the boxes, then press Compute (F5)");
                }
                else
                {
                    RecalcInputs_Click(sender, e); // read the entered values and compute
                }
                return;
            }
            Render();
        }

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
                    _status.Text = $"{label} — press F5 to return to the preview";
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
                _webView.Opacity = 1; // clear any "stale" dimming from a previous failed render

                // Count real errors (the engine's "Paused!" form banner isn't one) so the status
                // tells the truth instead of always reporting success.
                var errors = ExtractRenderErrors(_lastHtml)
                    .Where(e => e.IndexOf("Paused", StringComparison.OrdinalIgnoreCase) < 0).Count();
                var name = _currentFile is null ? "untitled" : Path.GetFileName(_currentFile);
                if (errors > 0)
                {
                    _status.Text = $"{name} · {errors} error{(errors == 1 ? "" : "s")} — see red text in the preview";
                    _status.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
                }
                else if (DateTime.UtcNow < _statusHoldUntil)
                {
                    // keep the held action confirmation; don't clobber it with the timing message
                }
                else
                {
                    _status.Text = $"{name} · rendered in {sw.ElapsedMilliseconds} ms";
                    _status.Foreground = new SolidColorBrush(Color.Parse("#4B5563"));
                }
                UpdateRunButton();
                UpdateEditorHint();
                RefreshKeypadVariables(); // keep the keypad's Variables column in sync
            }
            catch (Exception ex)
            {
                // Leave the previous (now out-of-date) preview on screen but visibly dim it, so the
                // user can't mistake stale output for the current worksheet.
                _webView.Opacity = 0.45;
                _status.Text = "Preview out of date — " + ex.Message;
                _status.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
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
            // Calculator habit: a line like "x*2=" means "evaluate x*2". Calcpad appends the
            // "= result" itself (bare "x*2" in classic, "$x*2$" in markdown) and a trailing '='
            // is otherwise an error, so rewrite such lines to the correct evaluating form.
            code = EvaluateTrailingEquals(code, CurrentMarkdownFull());
            if (_inputValues.Count > 0)
                code = ApplyInputValues(code, _inputValues);
            // Computed view: neutralize #input/#pause so results calculate with the entered values
            // instead of the engine pausing the form (the F5 "continue" resume, ported). Also
            // neutralize when there is no ?{…} field left to edit — an orphaned #input directive
            // would otherwise freeze and blank the whole sheet below it with nothing to explain why.
            if (_computeInputs || !code.Contains("?{"))
                code = NeutralizeInputDirectives(code);

            var parser = new ExpressionParser { Settings = BuildSettings(), Debug = true };
            parser.Parse(code, true, false);
            return parser.HtmlResult ?? string.Empty;
        }

        // Rewrites a "result request" line ending in a lone '=' (e.g. "x*2=", "q*L^2/8 =") into the
        // form Calcpad evaluates: "$expr$" in markdown, bare "expr" in classic. Lines whose left
        // side is a valid assignment target (name or f(x)) are left alone — those are assignments
        // the user is still typing, not evaluations. Line count is preserved (input-N mapping safe).
        private static string EvaluateTrailingEquals(string code, bool md)
        {
            if (!code.Contains('=')) return code;
            var lines = code.Replace("\r\n", "\n").Split('\n');
            var changed = false;
            for (var i = 0; i < lines.Length; ++i)
            {
                var raw = lines[i];
                var t = raw.TrimEnd();
                if (t.Length < 2 || t[^1] != '=') continue;
                if (t.EndsWith(":=") || t.EndsWith("==") || t.EndsWith("<=") ||
                    t.EndsWith(">=") || t.EndsWith("!=") || t.EndsWith("≠") ||
                    t.EndsWith("≤") || t.EndsWith("≥")) continue;
                var expr = t[..^1].TrimEnd();
                var ts = expr.TrimStart();
                if (ts.Length == 0 || ts[0] is '#' or '\'' or '"' or '$' or '|') continue;
                // A plain assignment target (name / f(args)) is an incomplete assignment, not an
                // evaluation request — leave it untouched.
                if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"^\s*[A-Za-z_]\w*(?:\s*\([^)]*\))?\s*$"))
                    continue;
                var lead = raw[..(raw.Length - raw.TrimStart().Length)];
                lines[i] = md ? $"{lead}${ts}$" : $"{lead}{ts}";
                changed = true;
            }
            return changed ? string.Join('\n', lines) : code;
        }

        // Blanks bare #input / #pause directive lines (keeping line numbers, so the input-N
        // mapping stays valid) so the worksheet computes through what would otherwise pause.
        private static string NeutralizeInputDirectives(string code)
        {
            if (code.IndexOf("#input", StringComparison.OrdinalIgnoreCase) < 0 &&
                code.IndexOf("#pause", StringComparison.OrdinalIgnoreCase) < 0)
                return code;
            var lines = code.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; ++i)
            {
                var t = lines[i].TrimStart();
                if ((t.StartsWith("#input", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("#pause", StringComparison.OrdinalIgnoreCase)) &&
                    (t.Length == 6 || t[6] is ' ' or '\t' or '\'' or '"'))
                    lines[i] = "";
            }
            return string.Join('\n', lines);
        }

        // True when the worksheet declares an #input form (a bare #input directive line).
        private bool DocHasInputForm()
        {
            var text = _editor?.Text;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var raw in text.Split('\n'))
            {
                var t = raw.TrimStart();
                if (t.StartsWith("#input", StringComparison.OrdinalIgnoreCase) &&
                    (t.Length == 6 || t[6] is ' ' or '\t' or '\'' or '"'))
                    return true;
            }
            return false;
        }

        // A stable fingerprint of the input-field structure: the total line count plus the text of
        // every line that holds a ?{…} field, keyed by line number. If this is unchanged after an
        // edit, the entered #input values still map correctly and the computed view can be kept.
        private string InputSignature()
        {
            var text = _editor?.Text ?? string.Empty;
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            sb.Append(lines.Length).Append('|');
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("?{"))
                    sb.Append(i + 1).Append(':').Append(lines[i].Trim()).Append('\n');
            return sb.ToString();
        }

        // Reflects the form/computed state on the toolbar so the mode is never invisible: on an
        // input-form worksheet the Run button names the action it performs (compute vs edit) and is
        // tinted, and the "↻ Inputs" button only appears there; otherwise it is "Update preview (F5)".
        private void UpdateRunButton()
        {
            if (_runButton is null) return;
            if (DocHasInputForm())
            {
                _recalcInputsButton.IsVisible = true;
                if (_computeInputs)
                {
                    _runButton.Content = "✎ Edit inputs (F5)";
                    _runButton.Background = new SolidColorBrush(Color.Parse("#DCFCE7")); // green: showing results
                    ToolTip.SetTip(_runButton, "Showing the calculated result. Press F5 to go back to editing the input boxes.");
                }
                else
                {
                    _runButton.Content = "▶ Compute (F5)";
                    _runButton.Background = new SolidColorBrush(Color.Parse("#DBEAFE")); // blue: editable form
                    ToolTip.SetTip(_runButton, "Editing the input boxes. Press F5 to calculate the worksheet with the entered values.");
                }
            }
            else
            {
                _recalcInputsButton.IsVisible = false;
                _runButton.Content = "Update preview (F5)";
                _runButton.ClearValue(Button.BackgroundProperty);
                ToolTip.SetTip(_runButton, "Update the preview (F5).");
            }
        }

        // Shows the greyed empty-state guidance only while the worksheet has no real content yet
        // (nothing but an optional #md directive and whitespace), so a blank new document teaches
        // the core gesture instead of being an empty pane.
        private void UpdateEditorHint()
        {
            if (_editorHint is null) return;
            foreach (var raw in (_editor?.Text ?? string.Empty).Split('\n'))
            {
                var s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#md", StringComparison.OrdinalIgnoreCase)) continue;
                _editorHint.IsVisible = false;
                return;
            }
            _editorHint.IsVisible = true;
        }

        // Sets the status line and holds it against the next auto-render for ~4s, so an action
        // confirmation (input inserted, image linked…) isn't immediately wiped by "rendered in N ms".
        private void StatusHold(string message)
        {
            _status.Text = message;
            _status.Foreground = new SolidColorBrush(Color.Parse("#4B5563"));
            _statusHoldUntil = DateTime.UtcNow.AddSeconds(4);
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
                _computeInputs = true; // switch to the computed view (neutralizes #input)
                _inputSig = InputSignature(); // remember the structure so unrelated edits keep these values
                Render();
                StatusHold(_inputValues.Count > 0
                    ? $"Calculated with {_inputValues.Count} input value(s) — press Edit inputs (F5) to change them"
                    : "Calculated — press Edit inputs (F5) to change the inputs");
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
                        new FilePickerFileType("Calcpad worksheet or package") { Patterns = new[] { "*.cpd", "*.cpdz", "*.txt" } },
                        new FilePickerFileType("Calcpad package") { Patterns = new[] { "*.cpdz" } },
                        FilePickerFileTypes.All
                    }
                });
                if (files.Count == 0) return;
                var path = files[0].Path.LocalPath;
                // A .cpdz is a zipped package → extract and open in package mode, not as plain text.
                if (string.Equals(Path.GetExtension(path), ".cpdz", StringComparison.OrdinalIgnoreCase))
                {
                    OpenPackage(path);
                    return;
                }
                _packagePath = null; _packageWorkDir = null; // opening a plain worksheet leaves package mode
                OpenOrActivate(path, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace("\r", "\n"));
                AddRecent(path);
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
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Calcpad") { Patterns = new[] { "*.cpd" } },
                        new FilePickerFileType("Calcpad package") { Patterns = new[] { "*.cpdz" } }
                    }
                });
                if (file is null) return;
                var newPath = file.Path.LocalPath;
                // Chose the package format (or typed a .cpdz name) → save a self-contained package.
                if (string.Equals(Path.GetExtension(newPath), ".cpdz", StringComparison.OrdinalIgnoreCase))
                {
                    await SaveAsPackageTo(newPath);
                    return;
                }
                var oldDir = _currentFile is null ? null : Path.GetDirectoryName(_currentFile);
                await SaveTo(newPath);
                await MaybeCopyReferencesLocally(oldDir, Path.GetDirectoryName(newPath));
                AddRecent(newPath);
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
            if (_active is not null)
            {
                _active.DisplayName = Path.GetFileName(zipPath);  // tab shows the .cpdz name
                _active.PackagePath = zipPath;
                _active.PackageWorkDir = workDir;                 // browser shows its contents while this tab is active
            }
            RebuildTabs();
            if (cfg is not null) ApplyConfigJson(cfg);
            UpdateTitle();
            AddRecent(zipPath);
            UpdateFileBrowserForActiveDoc();     // show the package's libs/images/folders in the browser
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
                await SaveAsPackageTo(file.Path.LocalPath);
            }
            catch (Exception ex) { _status.Text = "Save package failed: " + ex.Message; }
        }

        // Writes the current worksheet (+ images, libraries, config, optional template) to a .cpdz
        // and switches into package mode so subsequent Save updates it in place.
        private async System.Threading.Tasks.Task SaveAsPackageTo(string dest)
        {
            var mainDir = string.IsNullOrEmpty(_currentFile) ? Environment.CurrentDirectory : (Path.GetDirectoryName(_currentFile) ?? Environment.CurrentDirectory);
            SetWorkingDirToWorksheet();
            var (tmplLocal, tmplName) = await ResolvePackageTemplateAsync();
            CpdPackage.Save(_editor.Text ?? string.Empty, mainDir, BuildConfigJson(tmplName), dest,
                templateLocalPath: tmplLocal, templateEntryName: tmplName);
            _status.Text = "Saved package " + Path.GetFileName(dest) + (tmplName is null ? "" : " (+ template)");
            OpenPackage(dest); // switch into package mode so later saves update it
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
                OpenExported(file.Path.LocalPath);
            }
            catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
        }

        // Opens an exported file in its default application.
        private void OpenExported(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = "Exported, but couldn't open it: " + ex.Message; }
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
                OpenExported(dest);
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
