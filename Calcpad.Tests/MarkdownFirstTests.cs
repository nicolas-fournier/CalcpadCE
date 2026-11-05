using Calcpad.Core;

namespace Calcpad.Tests;

// Phase A1 — markdown-first mode (`#md full`): prose is the default,
// math is delimited by $…$ (inline) and $$…$$ (block).
public class MarkdownFirstTests
{
    private static string Render(string source)
    {
        var macroParser = new MacroParser();
        var hasMacroErrors = macroParser.Parse(source, out var expandedSource, null, 0, false);
        Assert.False(hasMacroErrors);

        var parser = new ExpressionParser();
        parser.Parse(expandedSource, true, false);
        return parser.HtmlResult;
    }

    [Theory]
    [InlineData("# Title", "<h1")]
    [InlineData("## Sub", "<h2")]
    [InlineData("### H3", "<h3")]
    [InlineData("#### H4", "<h4")]
    [InlineData("##### H5", "<h5")]
    [InlineData("###### H6", "<h6")]
    public void AtxHeadings_RenderCorrectLevel(string line, string expectedTag)
    {
        var html = Render("#md full\n" + line + "\n");
        Assert.Contains(expectedTag, html);
    }

    [Fact]
    public void PlainLine_IsProseNotExpression()
    {
        // Without $…$, the line is prose: it must NOT be evaluated as math.
        var html = Render("#md full\nThe span is 5 m long.\n");
        Assert.Contains("The span is 5 m long.", html);
    }

    [Fact]
    public void Emphasis_RendersBold()
    {
        var html = Render("#md full\nThis is **bold** text.\n");
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void InlineMath_IsEvaluated()
    {
        // $2 + 3$ must be computed to 5, not printed literally.
        var html = Render("#md full\nResult: $2 + 3$\n");
        Assert.Contains("5", html);
        Assert.DoesNotContain("$2 + 3$", html);
    }

    [Fact]
    public void BlockMath_IsEvaluated()
    {
        var html = Render("#md full\n$$ 6 * 7 $$\n");
        Assert.Contains("42", html);
    }

    [Fact]
    public void InlineBacktickCode_IsEvaluatedAsMath()
    {
        // `2 + 3` (markdown inline code) is evaluated like $2 + 3$.
        var html = Render("#md full\nResult: `2 + 3`\n");
        Assert.Contains("5", html);
        Assert.DoesNotContain("`2 + 3`", html);
    }

    [Fact]
    public void BacktickAndDollar_BothWork_OnSameLine()
    {
        var html = Render("#md full\n`2 + 3` and $4 + 4$\n");
        Assert.Contains("5", html);
        Assert.Contains("8", html);
    }

    [Fact]
    public void EscapedDollar_RendersLiteral()
    {
        var html = Render("#md full\nPrice is \\$5 today.\n");
        Assert.Contains("$5", html);
    }

    [Fact]
    public void Keyword_NotMistakenForHeading()
    {
        // `#if` is a keyword, not a markdown heading — the condition must hide the block.
        var html = Render("#md full\n#if 0\nhidden prose\n#end if\nvisible prose\n");
        Assert.DoesNotContain("hidden prose", html);
        Assert.Contains("visible prose", html);
    }

    [Fact]
    public void ClassicMode_StillUsesQuotes()
    {
        // Backward compat: without `#md full`, math is still the default.
        var html = Render("2 + 3\n");
        Assert.Contains("5", html);
    }
}
