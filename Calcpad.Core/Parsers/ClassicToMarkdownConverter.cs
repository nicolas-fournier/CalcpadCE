using System;
using System.Collections.Generic;
using System.Text;

namespace Calcpad.Core
{
    /// <summary>
    /// Best-effort converter from the classic Calcpad nomenclature
    /// (<c>'text'</c>, <c>"title"</c>, bare math) to the markdown-first form used by
    /// <c>#md full</c>: prose is the default and math is delimited by <c>$…$</c>.
    /// </summary>
    /// <remarks>
    /// This is an authoring aid, not a guaranteed round-trip: keyword lines, plots and
    /// HTML pass through unchanged, a <c>"title"</c> that is the whole line becomes an
    /// <c>###</c> heading (the classic heading level), and quoted text mixed with math on
    /// one line is rebuilt inline. Review the result before committing.
    /// </remarks>
    public static class ClassicToMarkdownConverter
    {
        private enum SegKind { Math, Text, Heading }

        public static string Convert(string source, bool addDirective = true)
        {
            if (string.IsNullOrEmpty(source))
                return source;

            var normalized = source.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');
            var sb = new StringBuilder(normalized.Length + 16);
            var directivePresent = false;
            foreach (var line in lines)
            {
                var t = line.AsSpan().TrimStart();
                if (t.StartsWith("#md", StringComparison.OrdinalIgnoreCase) &&
                    (t.Length == 3 || !char.IsLetter(t[3])))
                {
                    directivePresent = true;
                    break;
                }
            }

            if (addDirective && !directivePresent)
                sb.Append("#md full\n");

            for (int i = 0; i < lines.Length; ++i)
            {
                sb.Append(ConvertLine(lines[i]));
                if (i < lines.Length - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string ConvertLine(string line)
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                return line;

            // Keyword / directive / plot / HTML lines pass through untouched.
            if (trimmed[0] == '#' || trimmed[0] == '<' ||
                trimmed.StartsWith("$plot", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("$map", StringComparison.OrdinalIgnoreCase))
                return line;

            var segments = Tokenize(line);
            if (segments.Count == 0)
                return line;

            // A line that is exactly one heading → an ATX heading at the classic level (h3).
            if (segments.Count == 1 && segments[0].Kind == SegKind.Heading)
                return "### " + segments[0].Text.Trim();

            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                switch (seg.Kind)
                {
                    case SegKind.Math:
                        var m = seg.Text.Trim();
                        if (m.Length > 0)
                            sb.Append('$').Append(m).Append('$');
                        break;
                    case SegKind.Text:
                        sb.Append(EscapeProse(seg.Text));
                        break;
                    case SegKind.Heading:
                        // Heading mixed with other content → render bold inline.
                        var h = seg.Text.Trim();
                        if (h.Length > 0)
                            sb.Append("**").Append(EscapeProse(h)).Append("**");
                        break;
                }
            }
            return sb.ToString();
        }

        private readonly struct Segment(SegKind kind, string text)
        {
            public SegKind Kind { get; } = kind;
            public string Text { get; } = text;
        }

        // Mirrors ExpressionParser.GetTokens quote handling: outside quotes is math,
        // 'single' is text, "double" is a heading; a doubled quote is a literal.
        private static List<Segment> Tokenize(string line)
        {
            var segments = new List<Segment>();
            var buf = new StringBuilder();
            var sep = ' ';
            for (int i = 0, len = line.Length; i < len; ++i)
            {
                var c = line[i];
                if (c == '\'' || c == '\"')
                {
                    if (sep == ' ' || sep == c)
                    {
                        if (sep == c && i + 1 < len && line[i + 1] == c)
                        {
                            buf.Append(c); // doubled quote → literal
                            ++i;
                            continue;
                        }
                        Flush(segments, buf, sep);
                        sep = sep == c ? ' ' : c;
                    }
                    else
                        buf.Append(c);
                }
                else
                    buf.Append(c);
            }
            Flush(segments, buf, sep);
            return segments;
        }

        private static void Flush(List<Segment> segments, StringBuilder buf, char sep)
        {
            if (buf.Length == 0)
                return;

            var kind = sep switch
            {
                '\"' => SegKind.Heading,
                '\'' => SegKind.Text,
                _ => SegKind.Math
            };
            var text = buf.ToString();
            buf.Clear();
            if (kind == SegKind.Math && text.AsSpan().IsWhiteSpace())
                return;

            segments.Add(new Segment(kind, text));
        }

        private static string EscapeProse(string text)
        {
            if (text.IndexOfAny(['$', '`', '\\']) < 0)
                return text;

            var sb = new StringBuilder(text.Length + 4);
            foreach (var c in text)
            {
                if (c is '$' or '`' or '\\')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
