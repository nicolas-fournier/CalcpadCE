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

        public CompletionData(string text, string? insert = null, string? description = null,
            double priority = 0, string? content = null, string? colorHex = null)
        {
            Text = text;
            _insert = insert ?? text;
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
            => textArea.Document.Replace(completionSegment, _insert);
    }
}
