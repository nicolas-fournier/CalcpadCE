using System.Collections.ObjectModel;

namespace Calcpad.AI
{
    /// <summary>A reusable markdown-first worksheet fragment.</summary>
    /// <param name="Key">Short identifier / autocomplete trigger (e.g. <c>title</c>).</param>
    /// <param name="Title">Human-readable name for menus and completion lists.</param>
    /// <param name="Body">The <c>.cpd</c> text inserted into the editor.</param>
    public sealed record CalcpadSnippet(string Key, string Title, string Body);

    /// <summary>
    /// Canonical catalogue of markdown-first (<c>#md full</c>) snippets. Shared by the WPF
    /// editor's completion/insertion UI and offered to the AI assistant as building blocks,
    /// so both speak the same up-to-date nomenclature.
    /// </summary>
    public static class CalcpadSnippets
    {
        public static ReadOnlyCollection<CalcpadSnippet> All { get; } = new(new[]
        {
            new CalcpadSnippet("md", "Enable markdown mode",
                "#md full"),

            new CalcpadSnippet("title", "Document title (H1)",
                "# Title"),

            new CalcpadSnippet("section", "Section heading (H2)",
                "## Section"),

            // HTML heading tags (opening + closing) — work in classic and markdown modes.
            new CalcpadSnippet("h1", "HTML heading <h1></h1>", "<h1>Heading 1</h1>"),
            new CalcpadSnippet("h2", "HTML heading <h2></h2>", "<h2>Heading 2</h2>"),
            new CalcpadSnippet("h3", "HTML heading <h3></h3>", "<h3>Heading 3</h3>"),
            new CalcpadSnippet("h4", "HTML heading <h4></h4>", "<h4>Heading 4</h4>"),
            new CalcpadSnippet("h5", "HTML heading <h5></h5>", "<h5>Heading 5</h5>"),
            new CalcpadSnippet("h6", "HTML heading <h6></h6>", "<h6>Heading 6</h6>"),

            new CalcpadSnippet("note", "Prose paragraph",
                "Describe the step here, with **bold** and *italic* where useful."),

            new CalcpadSnippet("math", "Inline math",
                "The result is $a + b$"),

            new CalcpadSnippet("block", "Block math",
                "$$ a + b $$"),

            new CalcpadSnippet("var", "Variable with unit",
                "$L = 6*m$"),

            new CalcpadSnippet("list", "Bulleted list",
                "- first\n- second\n- third"),

            new CalcpadSnippet("table", "Table",
                "| Quantity | Value |\n| --- | --- |\n| Length | $L = 6*m$ |"),

            new CalcpadSnippet("if", "Condition block",
                "#if a > b\n$a$\n#else\n$b$\n#end if"),

            new CalcpadSnippet("beam", "Simply-supported beam moment",
                "# Simply-supported beam\n" +
                "Span and load:\n" +
                "$L = 6*m$\n" +
                "$q = 10*kN/m$\n" +
                "Maximum bending moment:\n" +
                "$M = q*L^2/8$"),
        });

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
