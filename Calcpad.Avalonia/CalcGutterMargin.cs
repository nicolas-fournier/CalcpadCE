using System;
using System.Globalization;
using Avalonia;
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

        public CalcGutterMargin(Func<string, char> classify, Action<int> toggleLineByOffset)
        {
            _classify = classify;
            _toggleLineByOffset = toggleLineByOffset;
            Cursor = new Cursor(StandardCursorType.Hand);
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

                var glyph = badge == '#' ? "#" : "ƒ"; // ƒ
                var brush = badge == '#' ? HeadBrush : CalcBrush;
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _typeface, 11, brush);
                context.DrawText(ft, new Point(4, vl.VisualTop - tv.VerticalOffset + 1));
            }
        }
    }
}
