using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Calcpad.AI
{
    /// <summary>
    /// Which document nomenclature a snippet belongs to, so the editor can offer the right
    /// set depending on whether the worksheet is in markdown-first (<c>#md full</c>) mode.
    /// </summary>
    public enum SnippetMode
    {
        /// <summary>Valid in both classic and markdown documents (e.g. HTML tags, directives).</summary>
        Any,
        /// <summary>Markdown-first syntax (headings <c>#</c>, <c>:=</c>, <c>$…$</c>, md lists/tables).</summary>
        Markdown,
        /// <summary>Classic Calcpad nomenclature (<c>=</c>, quoted text, HTML markup).</summary>
        Classic,
    }

    /// <summary>A reusable worksheet fragment.</summary>
    /// <param name="Key">Short identifier / autocomplete trigger (e.g. <c>title</c>).</param>
    /// <param name="Title">Human-readable name for menus and completion lists.</param>
    /// <param name="Body">The <c>.cpd</c> text inserted into the editor.</param>
    /// <param name="Mode">Document type the snippet applies to (default <see cref="SnippetMode.Any"/>).</param>
    public sealed record CalcpadSnippet(string Key, string Title, string Body, SnippetMode Mode = SnippetMode.Any);

    /// <summary>
    /// Canonical catalogue of worksheet snippets. Shared by the WPF editor's completion/insertion
    /// UI and offered to the AI assistant as building blocks, so both speak the same up-to-date
    /// nomenclature. Each entry is tagged with the <see cref="SnippetMode"/> it belongs to; use
    /// <see cref="ForMode(bool)"/> to get the set appropriate to the current document.
    /// </summary>
    public static class CalcpadSnippets
    {
        public static ReadOnlyCollection<CalcpadSnippet> All { get; } = new(new[]
        {
            // ----- Markdown-first (#md full) -----
            new CalcpadSnippet("title", "Document title (H1)",
                "# Title", SnippetMode.Markdown),

            new CalcpadSnippet("section", "Section heading (H2)",
                "## Section", SnippetMode.Markdown),

            new CalcpadSnippet("note", "Prose paragraph",
                "Describe the step here, with **bold** and *italic* where useful.", SnippetMode.Markdown),

            new CalcpadSnippet("formula", "Formula (:=)",
                "M := q*L^2/8", SnippetMode.Markdown),

            new CalcpadSnippet("var", "Variable with unit (:=)",
                "L := 6*m", SnippetMode.Markdown),

            new CalcpadSnippet("convert", "Convert to target units ( | )",
                "p := 10*kN/m^2 | MPa", SnippetMode.Markdown),

            new CalcpadSnippet("math", "Inline math in prose",
                "The result is $a + b$", SnippetMode.Markdown),

            new CalcpadSnippet("block", "Block math",
                "$$ a + b $$", SnippetMode.Markdown),

            new CalcpadSnippet("list", "Bulleted list",
                "- first\n- second\n- third", SnippetMode.Markdown),

            new CalcpadSnippet("table", "Table",
                "| Quantity | Value |\n| --- | --- |\n| Length | $L$ |", SnippetMode.Markdown),

            new CalcpadSnippet("beam", "Simply-supported beam moment",
                "# Simply-supported beam\n" +
                "Span and load:\n" +
                "L := 6*m\n" +
                "q := 10*kN/m\n" +
                "Maximum bending moment:\n" +
                "M := q*L^2/8", SnippetMode.Markdown),

            // ----- Classic nomenclature -----
            new CalcpadSnippet("title", "Heading (classic)",
                "\"Title", SnippetMode.Classic),

            new CalcpadSnippet("note", "Text paragraph (classic)",
                "'Describe the step here, with <b>bold</b> and <i>italic</i> where useful.",
                SnippetMode.Classic),

            new CalcpadSnippet("formula", "Formula (=)",
                "M = q*L^2/8", SnippetMode.Classic),

            new CalcpadSnippet("var", "Variable with unit (=)",
                "L = 6*m", SnippetMode.Classic),

            new CalcpadSnippet("convert", "Convert to target units ( | )",
                "p = 10*kN/m^2 | MPa", SnippetMode.Classic),

            new CalcpadSnippet("if", "Condition block (classic)",
                "#if a > b\na\n#else\nb\n#end if", SnippetMode.Classic),

            new CalcpadSnippet("beam", "Simply-supported beam moment (classic)",
                "\"Simply-supported beam\n" +
                "'Span and load:\n" +
                "L = 6*m\n" +
                "q = 10*kN/m\n" +
                "'Maximum bending moment:\n" +
                "M = q*L^2/8", SnippetMode.Classic),

            // Markdown condition block uses $…$ math delimiters (classic variant is above).
            new CalcpadSnippet("if", "Condition block",
                "#if a > b\n$a$\n#else\n$b$\n#end if", SnippetMode.Markdown),

            // ----- Compute core (Calcpad's differentiators) -----
            new CalcpadSnippet("plot", "Plot a function", "$Plot{x^2 @ x = 0 : 10}$", SnippetMode.Markdown),
            new CalcpadSnippet("plot", "Plot a function", "$Plot{x^2 @ x = 0 : 10}", SnippetMode.Classic),

            new CalcpadSnippet("vector", "Vector / matrix literal", "v := [1; 2; 3]", SnippetMode.Markdown),
            new CalcpadSnippet("vector", "Vector / matrix literal", "v = [1; 2; 3]", SnippetMode.Classic),

            new CalcpadSnippet("repeat", "Repeat loop",
                "#repeat 3\na := a + 1\n#loop", SnippetMode.Markdown),
            new CalcpadSnippet("repeat", "Repeat loop",
                "#repeat 3\na = a + 1\n#loop", SnippetMode.Classic),

            new CalcpadSnippet("def", "Define a function", "$sq(x) = x^2$", SnippetMode.Markdown),
            new CalcpadSnippet("def", "Define a function", "sq(x) = x^2", SnippetMode.Classic),
        });

        /// <summary>
        /// The snippets appropriate to the current document: markdown-first entries when
        /// <paramref name="markdownFull"/> is true, classic entries otherwise. Mode-agnostic
        /// entries (<see cref="SnippetMode.Any"/>) are always included.
        /// </summary>
        public static IEnumerable<CalcpadSnippet> ForMode(bool markdownFull)
        {
            var wanted = markdownFull ? SnippetMode.Markdown : SnippetMode.Classic;
            foreach (var s in All)
                if (s.Mode == SnippetMode.Any || s.Mode == wanted)
                    yield return s;
        }

        public static bool TryGet(string key, out CalcpadSnippet snippet)
        {
            foreach (var s in All)
            {
                if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    snippet = s;
                    return true;
                }
            }
            snippet = null!;
            return false;
        }
    }
}
