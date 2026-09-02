using LingoDict.Core.Text;

namespace LingoDict.Core.Tests;

public sealed class TextHighlighterTests
{
    [Fact]
    public void Split_MarksTheMatchingRun()
    {
        var segments = TextHighlighter.Split("cancellation", "cancel");

        Assert.Equal([new TextSegment("cancel", true), new TextSegment("lation", false)], segments);
    }

    [Fact]
    public void Split_IsCaseInsensitive()
    {
        var segments = TextHighlighter.Split("Cancel", "cancel");

        Assert.Equal([new TextSegment("Cancel", true)], segments);
    }

    [Fact]
    public void Split_MergesAdjacentIdeographTokens()
    {
        // "取消" 切成两个单字词元，命中后应合并成一段连续高亮。
        var segments = TextHighlighter.Split("v 取消订单", "取消");

        Assert.Equal(
            [new TextSegment("v ", false), new TextSegment("取消", true), new TextSegment("订单", false)],
            segments);
    }

    [Fact]
    public void Split_MarksEveryOccurrenceIncludingInsideOtherWords()
    {
        var segments = TextHighlighter.Split("can, cancel, uncanny", "can");

        Assert.Equal(3, segments.Count(x => x.IsMatch));
        Assert.All(segments.Where(x => x.IsMatch), x => Assert.Equal("can", x.Text));
    }

    [Fact]
    public void Split_IgnoresPunctuationInTheQuery()
    {
        var segments = TextHighlighter.Split("long-time", "long time");

        Assert.Equal(
            [new TextSegment("long", true), new TextSegment("-", false), new TextSegment("time", true)],
            segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Split_WithNothingSearchable_ReturnsOneUnmatchedSegment(string query)
    {
        var segments = TextHighlighter.Split("cancel", query);

        Assert.Equal([new TextSegment("cancel", false)], segments);
    }

    [Fact]
    public void Split_WithEmptyText_ReturnsNoSegments()
    {
        Assert.Empty(TextHighlighter.Split(string.Empty, "cancel"));
        Assert.Empty(TextHighlighter.Split(null, "cancel"));
    }

    [Fact]
    public void Split_AlwaysReconstructsTheOriginalText()
    {
        const string Text = "adv 突然地；忽然 quickly and unexpectedly";

        foreach (var query in new[] { "突然", "quick", "adv 忽然", "zzz", "" })
        {
            Assert.Equal(Text, string.Concat(TextHighlighter.Split(Text, query).Select(x => x.Text)));
        }
    }
}
