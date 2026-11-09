using Calcpad.AI;
using Calcpad.Core;

namespace Calcpad.Tests;

// Phase C1 — the shared snippet catalogue must be valid CalcpadCE. Snippets are mode-aware:
// markdown-first entries render under #md full, classic entries under classic nomenclature,
// and within each mode (markdown-or-classic plus the mode-agnostic ones) keys are unique.
public class MarkdownSnippetTests
{
    private static string Render(string source, bool calculate = true)
    {
        var macroParser = new MacroParser();
        macroParser.Parse(source, out var expanded, null, 0, false);
        var parser = new ExpressionParser();
        parser.Parse(expanded, calculate, false);
        return parser.HtmlResult;
    }

    [Fact]
    public void Catalogue_IsNotEmpty() => Assert.NotEmpty(CalcpadSnippets.All);

    // The autocomplete trigger / menu key must be unambiguous within the active mode.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Keys_AreUnique_WithinEachMode(bool markdownFull)
    {
        var keys = CalcpadSnippets.ForMode(markdownFull).Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // Markdown-first snippets (plus mode-agnostic ones) render under #md full. The preamble
    // defines the placeholder variables generic fragments reference, so only genuine syntax
    // errors (not "undefined variable") can fail this test.
    [Theory]
    [MemberData(nameof(MarkdownSnippets))]
    public void MarkdownSnippet_RendersWithoutError(string key, string body)
    {
        _ = key;
        var html = Render("#md full\nL := 1\nq := 1\na := 1\nb := 2\n" + body + "\n");
        Assert.DoesNotContain("class=\"err\"", html);
    }

    // Classic snippets (plus mode-agnostic ones) render under classic nomenclature.
    [Theory]
    [MemberData(nameof(ClassicSnippets))]
    public void ClassicSnippet_RendersWithoutError(string key, string body)
    {
        _ = key;
        var html = Render("L = 1\nq = 1\na = 1\nb = 2\n" + body + "\n");
        Assert.DoesNotContain("class=\"err\"", html);
    }

    [Fact]
    public void BeamSnippet_EvaluatesMoment()
    {
        // Fully-defined snippet: M = 10 kN/m * (6 m)^2 / 8 = 45 kN·m
        var body = CalcpadSnippets.All.Single(s => s.Key == "beam" && s.Mode == SnippetMode.Markdown).Body;
        Assert.Contains("45", Render("#md full\n" + body + "\n"));
    }

    public static IEnumerable<object[]> MarkdownSnippets =>
        CalcpadSnippets.ForMode(true).Select(s => new object[] { s.Key, s.Body });

    public static IEnumerable<object[]> ClassicSnippets =>
        CalcpadSnippets.ForMode(false).Select(s => new object[] { s.Key, s.Body });
}
