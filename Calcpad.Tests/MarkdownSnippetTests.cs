using Calcpad.AI;
using Calcpad.Core;

namespace Calcpad.Tests;

// Phase C1 — the shared snippet catalogue must be valid markdown-first CalcpadCE.
public class MarkdownSnippetTests
{
    private static string Render(string source, bool calculate = false)
    {
        var macroParser = new MacroParser();
        macroParser.Parse(source, out var expanded, null, 0, false);
        var parser = new ExpressionParser();
        parser.Parse(expanded, calculate, false);
        return parser.HtmlResult;
    }

    [Fact]
    public void Catalogue_IsNotEmpty_And_KeysAreUnique()
    {
        Assert.NotEmpty(CalcpadSnippets.All);
        var keys = CalcpadSnippets.All.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(SnippetKeys))]
    public void EverySnippet_RendersWithoutError(string key)
    {
        Assert.True(CalcpadSnippets.TryGet(key, out var snippet));
        // Preamble defines the placeholder variables that generic fragments reference,
        // so only genuine syntax errors (not "undefined variable") can fail this test.
        var html = Render("#md full\n$a = 1$\n$b = 2$\n" + snippet.Body + "\n", calculate: true);
        Assert.DoesNotContain("class=\"err\"", html);
    }

    [Fact]
    public void BeamSnippet_EvaluatesMoment()
    {
        // Fully-defined snippet: M = 10 kN/m * (6 m)^2 / 8 = 45 kN·m
        var macroParser = new MacroParser();
        macroParser.Parse("#md full\n" + CalcpadSnippets.All.Single(s => s.Key == "beam").Body + "\n",
            out var expanded, null, 0, false);
        var parser = new ExpressionParser();
        parser.Parse(expanded, true, false);
        Assert.Contains("45", parser.HtmlResult);
    }

    public static IEnumerable<object[]> SnippetKeys =>
        CalcpadSnippets.All.Select(s => new object[] { s.Key });
}
