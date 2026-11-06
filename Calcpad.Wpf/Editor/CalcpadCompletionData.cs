using System;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Calcpad.Wpf.Editor
{
    /// <summary>One AvalonEdit completion entry for the Calcpad editor.</summary>
    public sealed class CalcpadCompletionData : ICompletionData
    {
        private readonly string _insert;
        private readonly object _content;

        /// <param name="text">The word used to match/filter while typing.</param>
        /// <param name="insert">The text inserted on commit (may differ, e.g. a snippet body).</param>
        /// <param name="description">Tooltip text (shown beside the list).</param>
        /// <param name="priority">Higher sorts first within equal matches.</param>
        /// <param name="content">What the list row shows (defaults to <paramref name="text"/>).</param>
        public CalcpadCompletionData(string text, string insert = null, string description = null, double priority = 0, string content = null)
        {
            Text = text;
            _insert = insert ?? text;
            Description = description;
            Priority = priority;
            _content = content ?? text;
        }

        public ImageSource Image => null;
        public string Text { get; }
        public object Content => _content;
        public object Description { get; }
        public double Priority { get; }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, _insert);
        }
    }
}
