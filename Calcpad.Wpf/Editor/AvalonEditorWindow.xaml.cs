using System;
using System.Reflection;
using System.Windows;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Calcpad.Wpf.Editor
{
    /// <summary>
    /// Experimental AvalonEdit-based code editor for .cpd, hosted in its own window so it is fully
    /// additive — the main RichTextBox editor is untouched. First step of the editor migration:
    /// proves AvalonEdit + the Calcpad syntax highlighting, and can push edits back to the worksheet.
    /// </summary>
    public partial class AvalonEditorWindow : Window
    {
        /// <summary>Raised when the user applies the edited text back to the main worksheet.</summary>
        public event Action<string> ApplyRequested;

        public AvalonEditorWindow()
        {
            InitializeComponent();
            Editor.SyntaxHighlighting = LoadCalcpadHighlighting();
        }

        public void SetText(string text) => Editor.Text = text ?? string.Empty;
        public string GetText() => Editor.Text;

        private static IHighlightingDefinition LoadCalcpadHighlighting()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("Calcpad.Wpf.Editor.Calcpad.xshd");
                if (stream is null)
                    return null;

                using var reader = new XmlTextReader(stream);
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch
            {
                return null; // highlighting is a nicety; the editor still works without it
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            ApplyRequested?.Invoke(Editor.Text);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void WrapCheck_Changed(object sender, RoutedEventArgs e)
        {
            Editor.WordWrap = WrapCheck.IsChecked == true;
        }
    }
}
