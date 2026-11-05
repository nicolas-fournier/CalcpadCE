using Calcpad.Core;

namespace Calcpad.Tests;

// Phase A3 — markdown-first via Settings.Markdown (default Off, directive overrides).
// Phase A4 — ClassicToMarkdownConverter (classic nomenclature → #md full).
// Phase A5 — heading levels h1–h6 (drive distinct DOCX heading styles).
public class MarkdownConverterTests
{
    private static string Render(string source, MarkdownMode mode = MarkdownMode.Off)
    {
        var parser = new ExpressionParser();
        parser.Settings.Markdown = mode;
        parser.Parse(source, true, false);
        return parser.HtmlResult;
    }

    // ---- A3: Settings.Markdown ----

    [Fact]
    public void SettingsFull_TreatsLineAsProse_WithoutDirective()
    {
        var html = Render("The beam is 5 m long.\n", MarkdownMode.Full);
        Assert.Contains("The beam is 5 m long.", html);
    }

    [Fact]
    public void SettingsFull_EvaluatesInlineMath()
    {
        var html = Render("Result: $2 + 3$\n", MarkdownMode.Full);
        Assert.Contains("5", html);
    }

    [Fact]
    public void SettingsOff_IsClassic_ByDefault()
    {
        // Default Off: bare expression is math.
        var html = Render("2 + 3\n");
        Assert.Contains("5", html);
    }

    [Fact]
    public void DirectiveOverridesSettings()
    {
        // Settings say Full, but a #md off directive reverts to classic math from its line.
        var html = Render("#md off\n2 + 3\n", MarkdownMode.Full);
        Assert.Contains("5", html);
    }

    // ---- A4: ClassicToMarkdownConverter ----

    [Fact]
    public void Convert_QuotedText_BecomesProse()
    {
        var md = ClassicToMarkdownConverter.Convert("'Hello world'\n", addDirective: false);
        Assert.Contains("Hello world", md);
        Assert.DoesNotContain("'", md);
    }

    [Fact]
    public void Convert_Title_BecomesHeading()
    {
        var md = ClassicToMarkdownConverter.Convert("\"My Title\"\n", addDirective: false);
        Assert.Contains("### My Title", md);
    }

    [Fact]
    public void Convert_BareMath_IsWrappedInDollars()
    {
        var md = ClassicToMarkdownConverter.Convert("a + b\n", addDirective: false);
        Assert.Contains("$a + b$", md);
    }

    [Fact]
    public void Convert_MixedTextAndMath_RebuildsInline()
    {
        var md = ClassicToMarkdownConverter.Convert("'The result is ' x ' m'\n", addDirective: false);
        Assert.Contains("The result is $x$ m", md);
    }

    [Fact]
    public void Convert_KeywordLine_PassesThrough()
    {
        var md = ClassicToMarkdownConverter.Convert("#if x > 0\n", addDirective: false);
        Assert.Contains("#if x > 0", md);
    }

    [Fact]
    public void Convert_LiteralDollarInText_IsEscaped()
    {
        var md = ClassicToMarkdownConverter.Convert("'Price $5 today'\n", addDirective: false);
        Assert.Contains("\\$5", md);
    }

    [Fact]
    public void Convert_PrependsDirective_WhenRequested()
    {
        var md = ClassicToMarkdownConverter.Convert("'hi'\n");
        Assert.StartsWith("#md full", md);
    }

    [Fact]
    public void Convert_DoesNotDuplicateExistingDirective()
    {
        var md = ClassicToMarkdownConverter.Convert("#md full\n'hi'\n");
        // Only one directive.
        var first = md.IndexOf("#md full", System.StringComparison.Ordinal);
        var last = md.LastIndexOf("#md full", System.StringComparison.Ordinal);
        Assert.Equal(first, last);
    }

    [Fact]
    public void Convert_RoundTrip_RendersEquivalentProse()
    {
        var md = ClassicToMarkdownConverter.Convert("'The span is ' 5 ' meters'\n");
        var parser = new ExpressionParser();
        parser.Parse(md, true, false);
        var html = parser.HtmlResult;
        Assert.Contains("The span is", html);
        Assert.Contains("meters", html);
    }

    // ---- A5: heading levels (feed distinct DOCX styles h1..h6) ----

    [Theory]
    [InlineData("# A", "<h1")]
    [InlineData("## B", "<h2")]
    [InlineData("### C", "<h3")]
    [InlineData("#### D", "<h4")]
    [InlineData("##### E", "<h5")]
    [InlineData("###### F", "<h6")]
    public void HeadingLevels_ProduceDistinctTags(string line, string tag)
    {
        var html = Render("#md full\n" + line + "\n");
        Assert.Contains(tag, html);
    }
}
