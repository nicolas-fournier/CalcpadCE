using Calcpad.Core;

namespace Calcpad.Tests;

// Phase A2 — multi-line markdown blocks in `#md full` mode: consecutive plain-markdown
// lines are accumulated and rendered as one Markdig block so lists, tables, fenced code
// and multi-paragraph prose render correctly (not line-by-line).
public class MarkdownBlockTests
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

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
        {
            ++n;
            i += needle.Length;
        }
        return n;
    }

    [Fact]
    public void UnorderedList_MergesIntoSingleList()
    {
        var html = Render("#md full\n- apple\n- banana\n- cherry\n");
        Assert.Equal(1, Count(html, "<ul>"));
        Assert.Equal(3, Count(html, "<li>"));
        Assert.Contains("apple", html);
        Assert.Contains("cherry", html);
    }

    [Fact]
    public void OrderedList_MergesIntoSingleList()
    {
        var html = Render("#md full\n1. first\n2. second\n");
        Assert.Equal(1, Count(html, "<ol>"));
        Assert.Equal(2, Count(html, "<li>"));
    }

    [Fact]
    public void PipeTable_RendersTable()
    {
        var html = Render("#md full\n| A | B |\n|---|---|\n| 1 | 2 |\n");
        Assert.Contains("<table", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("<td>1</td>", html);
    }

    [Fact]
    public void FencedCode_RendersPre()
    {
        var html = Render("#md full\n```\nx = 1\ny = 2\n```\n");
        Assert.Contains("<pre>", html);
        Assert.Contains("<code>", html);
        Assert.Contains("x = 1", html);
    }

    [Fact]
    public void MultiParagraph_RendersSeparateParagraphs()
    {
        var html = Render("#md full\nFirst paragraph.\n\nSecond paragraph.\n");
        Assert.Contains("<p>First paragraph.</p>", html);
        Assert.Contains("<p>Second paragraph.</p>", html);
    }

    [Fact]
    public void Blockquote_Renders()
    {
        var html = Render("#md full\n> quoted line one\n> quoted line two\n");
        Assert.Contains("<blockquote>", html);
    }

    [Fact]
    public void MathLineAfterBlock_StillEvaluated()
    {
        // The list is a block; the trailing math line (has $) is handled per-line.
        var html = Render("#md full\n- a\n- b\nResult: $2 + 3$\n");
        Assert.Equal(1, Count(html, "<ul>"));
        Assert.Contains("5", html);
    }

    [Fact]
    public void SingleListItem_StillRendersPerLine()
    {
        // A one-line run falls through to the per-line path (unchanged A1 behaviour).
        var html = Render("#md full\n- lonely\n");
        Assert.Contains("<li>", html);
        Assert.Contains("lonely", html);
    }

    [Fact]
    public void HiddenBlock_InFalseCondition_NotRendered()
    {
        var html = Render("#md full\n#if 0\n- a\n- b\n#end if\nvisible prose\n");
        Assert.DoesNotContain("<li>", html);
        Assert.Contains("visible prose", html);
    }

    [Fact]
    public void HeadingThenProse_RenderBoth()
    {
        var html = Render("#md full\n# Title\nSome following text.\n");
        Assert.Contains("<h1", html);
        Assert.Contains("Some following text.", html);
    }
}
