using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Calcpad.Avalonia
{
    // A thin left-margin glyph strip: marks each line as a calculation (ƒ) or heading (#), blank
    // for prose — so the calc/prose split is visible at a glance. Classification is delegated so
    // it follows the editor's current mode. Clicking a badge toggles that line calc<->prose.
    internal sealed class CalcGutterMargin : AbstractMargin
    {
        private readonly Func<string, char> _classify;
        private readonly Action<int> _toggleLineByOffset;
        private readonly Typeface _typeface = new("Segoe UI");
        private static readonly IBrush CalcBrush = new SolidColorBrush(Color.Parse("#16A34A"));
        private static readonly IBrush HeadBrush = new SolidColorBrush(Color.Parse("#B2379B"));
        private static readonly IBrush TableBrush = new SolidColorBrush(Color.Parse("#1F7FAE"));
        private static readonly IBrush InputBrush = new SolidColorBrush(Color.Parse("#EA580C"));
        private static readonly IBrush ImageBrush = new SolidColorBrush(Color.Parse("#0891B2"));
        private static readonly IBrush ResultBrush = new SolidColorBrush(Color.Parse("#2563EB"));

        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
        private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

        public CalcGutterMargin(Func<string, char> classify, Action<int> toggleLineByOffset)
        {
            _classify = classify;
            _toggleLineByOffset = toggleLineByOffset;
        }

        private static string BadgeTip(char badge) => badge switch
        {
            't' => "Table — click to edit it in the grid",
            'i' => "Input field — click to edit it",
            'g' => "Image — click to select the line",
            'r' => "Result — click to toggle between calculation and prose",
            'f' => "Calculation — click to toggle it back to prose",
            '#' => "Heading",
            _   => "Prose — click to make this line a calculation",
        };

        // Hovering a row: show what its badge means and offer the Hand cursor only where a click
        // actually does something (a heading isn't clickable).
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var tv = TextView;
            if (tv is null || !tv.VisualLinesValid || tv.Document is null) { Cursor = ArrowCursor; return; }
            var y = e.GetPosition(this).Y + tv.VerticalOffset;
            foreach (var vl in tv.VisualLines)
            {
                if (y >= vl.VisualTop && y < vl.VisualTop + vl.Height)
                {
                    var dl = vl.FirstDocumentLine;
                    var badge = _classify(tv.Document.GetText(dl.Offset, dl.Length));
                    ToolTip.SetTip(this, BadgeTip(badge));
                    Cursor = badge == '#' ? ArrowCursor : HandCursor;
                    return;
                }
            }
            Cursor = ArrowCursor;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var tv = TextView;
            if (tv is null || !tv.VisualLinesValid) return;
            var y = e.GetPosition(this).Y + tv.VerticalOffset;
            foreach (var vl in tv.VisualLines)
            {
                if (y >= vl.VisualTop && y < vl.VisualTop + vl.Height)
                {
                    _toggleLineByOffset(vl.FirstDocumentLine.Offset);
                    e.Handled = true;
                    return;
                }
            }
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnRedraw;
                oldTextView.ScrollOffsetChanged -= OnRedraw;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnRedraw;
                newTextView.ScrollOffsetChanged += OnRedraw;
            }
            InvalidateVisual();
        }

        private void OnRedraw(object? sender, EventArgs e) => InvalidateVisual();

        protected override Size MeasureOverride(Size availableSize) => new(18, 0);

        public override void Render(DrawingContext context)
        {
            var tv = TextView;
            if (tv is null || !tv.VisualLinesValid || tv.Document is null) return;

            foreach (var vl in tv.VisualLines)
            {
                var dl = vl.FirstDocumentLine;
                var text = tv.Document.GetText(dl.Offset, dl.Length);
                var badge = _classify(text);
                if (badge == ' ') continue;

                var glyph = badge switch { '#' => "#", 't' => "⊞", 'i' => "▭", 'g' => "▣", 'r' => "=", _ => "ƒ" };
                var brush = badge switch { '#' => HeadBrush, 't' => TableBrush, 'i' => InputBrush, 'g' => ImageBrush, 'r' => ResultBrush, _ => CalcBrush };
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _typeface, 11, brush);
                context.DrawText(ft, new Point(4, vl.VisualTop - tv.VerticalOffset + 1));
            }
        }
    }
}
