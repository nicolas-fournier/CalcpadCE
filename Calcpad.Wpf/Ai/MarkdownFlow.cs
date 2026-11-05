using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Calcpad.Wpf.Ai
{
    /// <summary>
    /// Minimal Markdown → WPF FlowDocument renderer for the assistant chat: headings, bold/italic,
    /// inline code, fenced code blocks and bullet/numbered lists. Deliberately small (no new deps);
    /// good enough for assistant replies, which are markdown-first anyway.
    /// </summary>
    internal static class MarkdownFlow
    {
        private static readonly FontFamily Mono = new("Consolas");
        private static readonly Brush CodeBg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4));
        private static readonly Brush CodeFg = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
        private static readonly Brush HeadingFg = new SolidColorBrush(Color.FromRgb(0x22, 0x33, 0x44));

        static MarkdownFlow()
        {
            CodeBg.Freeze();
            CodeFg.Freeze();
            HeadingFg.Freeze();
        }

        public static IEnumerable<Block> ToBlocks(string markdown)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(markdown))
                return blocks;

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Fenced code block
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var code = new List<string>();
                    ++i;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                        code.Add(lines[i++]);
                    if (i < lines.Length) ++i; // closing fence
                    blocks.Add(CodeBlock(string.Join("\n", code)));
                    continue;
                }

                // Heading
                var level = HeadingLevel(trimmed);
                if (level > 0)
                {
                    var text = trimmed[level..].TrimStart();
                    var p = new Paragraph { Margin = new Thickness(0, 6, 0, 2), Foreground = HeadingFg };
                    var run = new Run(text) { FontWeight = FontWeights.Bold, FontSize = 18 - level };
                    p.Inlines.Add(run);
                    blocks.Add(p);
                    ++i;
                    continue;
                }

                // List item (bullet or numbered)
                if (IsBullet(trimmed, out var bulletText) || IsNumbered(trimmed, out bulletText))
                {
                    var p = new Paragraph { Margin = new Thickness(14, 1, 0, 1), TextIndent = -10 };
                    p.Inlines.Add(new Run("•  ") { Foreground = Brushes.Gray });
                    AddInlines(p, bulletText);
                    blocks.Add(p);
                    ++i;
                    continue;
                }

                // Blank line → spacing (skip; paragraphs already have margins)
                if (trimmed.Length == 0)
                {
                    ++i;
                    continue;
                }

                // Paragraph (merge consecutive plain lines)
                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                var first = true;
                while (i < lines.Length)
                {
                    var l = lines[i];
                    var t = l.TrimStart();
                    if (t.Length == 0 || HeadingLevel(t) > 0 ||
                        t.StartsWith("```", StringComparison.Ordinal) ||
                        IsBullet(t, out _) || IsNumbered(t, out _))
                        break;

                    if (!first)
                        para.Inlines.Add(new LineBreak());
                    AddInlines(para, l);
                    first = false;
                    ++i;
                }
                blocks.Add(para);
            }
            return blocks;
        }

        private static Paragraph CodeBlock(string code)
        {
            var p = new Paragraph(new Run(code))
            {
                FontFamily = Mono,
                Foreground = CodeFg,
                Background = CodeBg,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 3, 0, 3)
            };
            return p;
        }

        private static int HeadingLevel(string trimmed)
        {
            var n = 0;
            while (n < trimmed.Length && n < 6 && trimmed[n] == '#')
                ++n;
            if (n > 0 && n < trimmed.Length && trimmed[n] == ' ')
                return n;
            return 0;
        }

        private static bool IsBullet(string t, out string text)
        {
            if (t.Length >= 2 && (t[0] == '-' || t[0] == '*' || t[0] == '+') && t[1] == ' ')
            {
                text = t[2..];
                return true;
            }
            text = string.Empty;
            return false;
        }

        private static bool IsNumbered(string t, out string text)
        {
            var k = 0;
            while (k < t.Length && char.IsDigit(t[k])) ++k;
            if (k > 0 && k + 1 < t.Length && t[k] == '.' && t[k + 1] == ' ')
            {
                text = t[(k + 2)..];
                return true;
            }
            text = string.Empty;
            return false;
        }

        // Inline: **bold**, *italic* / _italic_, `code`.
        private static void AddInlines(Paragraph p, string text)
        {
            for (int i = 0; i < text.Length;)
            {
                if (text[i] == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        p.Inlines.Add(new Run(text[(i + 1)..end])
                        {
                            FontFamily = Mono,
                            Background = CodeBg,
                            Foreground = CodeFg
                        });
                        i = end + 1;
                        continue;
                    }
                }
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i)
                    {
                        p.Inlines.Add(new Bold(new Run(text[(i + 2)..end])));
                        i = end + 2;
                        continue;
                    }
                }
                if ((text[i] == '*' || text[i] == '_') && i + 1 < text.Length && text[i + 1] != ' ')
                {
                    var end = text.IndexOf(text[i], i + 1);
                    if (end > i)
                    {
                        p.Inlines.Add(new Italic(new Run(text[(i + 1)..end])));
                        i = end + 1;
                        continue;
                    }
                }

                // plain run up to the next markup char
                var next = IndexOfAny(text, i + 1, '`', '*', '_');
                if (next < 0) next = text.Length;
                p.Inlines.Add(new Run(text[i..next]));
                i = next;
            }
        }

        private static int IndexOfAny(string s, int start, char a, char b, char c)
        {
            for (int i = start; i < s.Length; ++i)
                if (s[i] == a || s[i] == b || s[i] == c)
                    return i;
            return -1;
        }
    }
}
