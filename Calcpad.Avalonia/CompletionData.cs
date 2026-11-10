using System;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Calcpad.Avalonia
{
    // One entry in the editor's autocomplete popup (snippet, function, unit, variable, directive).
    // Colour-coded by kind; higher Priority sorts earlier in the list.
    internal sealed class CompletionData : ICompletionData
    {
        private readonly string _insert;
        private readonly Action? _action;
        private readonly Action? _postComplete;

        public CompletionData(string text, string? insert = null, string? description = null,
            double priority = 0, string? content = null, string? colorHex = null,
            Action? action = null, Action? postComplete = null)
        {
            Text = text;
            _insert = insert ?? text;
            _action = action;
            _postComplete = postComplete;
            Description = description ?? string.Empty;
            Priority = priority;

            var label = content ?? text;
            Content = colorHex is null
                ? label
                : new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.Parse(colorHex)) };
        }

        public IImage? Image => null;
        public string Text { get; }
        public object Content { get; }
        public object Description { get; }
        public double Priority { get; }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (_action is not null) { _action(); return; } // run an action instead of inserting text
            textArea.Document.Replace(completionSegment, _insert);
            _postComplete?.Invoke();
        }
    }
}
