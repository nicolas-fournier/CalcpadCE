using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaEdit;
using Calcpad.Core;
using WebViewControl;

namespace Calcpad.Avalonia
{
    public partial class MainWindow : Window
    {
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
        private readonly DispatcherTimer _debounce;
        private readonly string _template;   // worksheet HTML head + CSS
        private string _lastHtml = string.Empty;
        private bool _ready;

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _editor = this.FindControl<TextEditor>("Editor")!;
            _webView = this.FindControl<WebView>("WebView")!;
            _status = this.FindControl<TextBlock>("StatusText")!;

            _template = LoadTemplate();

            // Debounce re-render so typing stays smooth.
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };

            _editor.Text = SampleSource;
            _editor.TextChanged += (_, _) => { if (_ready) { _debounce.Stop(); _debounce.Start(); } };
            KeyDown += (_, e) => { if (e.Key == Key.F5) Render(); };

            // The embedded Chromium browser initializes asynchronously; first render once shown.
            Loaded += (_, _) => { _ready = true; Render(); };
        }

        private void Run_Click(object? sender, RoutedEventArgs e) => Render();

        // Parses the current source through the Calcpad engine and shows the styled result
        // in the embedded web view.
        private void Render()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                _lastHtml = RenderHtml(_editor.Text ?? string.Empty);
                sw.Stop();
                _webView.LoadHtml(BuildPage(_lastHtml));
                _status.Text = $"Rendered in {sw.ElapsedMilliseconds} ms";
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
            }
        }

        private static string RenderHtml(string source)
        {
            // Match the CLI preview pipeline: macro/#include expansion, then expression parse.
            var macro = new MacroParser { Include = static (_, _) => string.Empty };
            macro.Parse(source, out var unwrapped, null, 0, false);

            var settings = new Settings();
            settings.Math.Decimals = 6;
            settings.Markdown = MarkdownMode.Full; // markdown-first default (a #md directive overrides)

            var parser = new ExpressionParser { Settings = settings };
            parser.Parse(unwrapped ?? source, true, false);
            return parser.HtmlResult ?? string.Empty;
        }

        private string BuildPage(string bodyHtml)
        {
            var page = new StringBuilder(_template.Length + bodyHtml.Length + 32);
            page.Append(_template);
            page.Append(bodyHtml);
            page.Append(" </body></html>");
            return page.ToString();
        }

        // Opens the current render in the system default browser (fallback / print path).
        private void OpenInBrowser_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "calcpad-preview.html");
                File.WriteAllText(path, BuildPage(_lastHtml), new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _status.Text = "Open failed: " + ex.Message;
            }
        }

        private static string LoadTemplate()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "template.html");
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }
            catch { /* fall back to a minimal shell */ }
            return "<!DOCTYPE html><html><head><meta charset=\"UTF-8\"></head><body>";
        }
    }
}
