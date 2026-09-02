using LingoDict.Core.Text;

namespace LingoDict.Core.Tests;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("突然地", " 突  然  地 ")]
    [InlineData("adv 突然地", "adv  突  然  地 ")]
    [InlineData("", "")]
    public void Segment_SplitsIdeographsIntoSeparateTokens(string input, string expected)
    {
        Assert.Equal(expected, SearchText.Segment(input));
    }

    [Fact]
    public void Segment_LeavesLatinRunsIntact()
    {
        // 英文词元必须保持完整，否则 unicode61 无法做前缀匹配。
        Assert.Equal("long-time", SearchText.Segment("long-time"));
    }

    [Theory]
    [InlineData("cancel", "\"cancel\" *")]
    [InlineData("  Cancel  ", "\"cancel\" *")]
    [InlineData("long-time", "\"long time\" *")]
    [InlineData("取消", "\"取 消\" *")]
    [InlineData("取消 order", "\"取 消 order\" *")]
    public void BuildMatchExpression_ProducesPrefixedPhrase(string input, string expected)
    {
        Assert.Equal(expected, SearchText.BuildMatchExpression(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ***")]
    public void BuildMatchExpression_ReturnsNullWhenNothingSearchable(string input)
    {
        Assert.Null(SearchText.BuildMatchExpression(input));
    }

    [Theory]
    [InlineData("a\"b OR c*")]
    [InlineData("NEAR(x y)")]
    [InlineData("^start")]
    public void BuildMatchExpression_StripsFtsOperators(string input)
    {
        var expression = SearchText.BuildMatchExpression(input);

        Assert.NotNull(expression);

        // 词元只允许字母/数字/汉字，因此表达式里除了首尾的短语引号不会再出现引号。
        Assert.Equal(2, expression!.Count(x => x == '"'));
        Assert.DoesNotContain("(", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("^", expression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Please cancel the order.", 7, "cancel")]   // 词首
    [InlineData("Please cancel the order.", 10, "cancel")]  // 词中
    [InlineData("Please cancel the order.", 12, "cancel")]  // 词尾
    [InlineData("Please cancel the order.", 19, "order")]   // 紧邻句号
    [InlineData("Please cancel the order.", 23, "order")]   // 点在句号上，退回前一个词
    [InlineData("a long-time friend", 7, "long-time")]      // 连字符不拆
    [InlineData("I don't know", 4, "don't")]                // 撇号不拆
    public void WordAt_ExtractsTheWholeWordWithoutPunctuation(string text, int index, string expected)
    {
        Assert.Equal(expected, SearchText.WordAt(text, index));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 1)]
    [InlineData("...", 1)]
    public void WordAt_ReturnsEmptyWhenThereIsNoWord(string text, int index)
    {
        Assert.Equal(string.Empty, SearchText.WordAt(text, index));
    }

    [Fact]
    public void WordAt_ClampsOutOfRangeIndexes()
    {
        // 命中测试可能返回文本长度（点在行尾之后），不能越界。
        Assert.Equal("order", SearchText.WordAt("the order", 99));
        Assert.Equal("the", SearchText.WordAt("the order", -5));
    }

    [Fact]
    public void WordAt_FeedsStraightIntoTheSameNormalizationAsSearch()
    {
        // 双击 "order." 之后走的是 FindByHeadwordAsync，那里会再归一化一次。
        Assert.Equal("order", WordNormalizer.Normalize(SearchText.WordAt("cancel the order.", 15)));
        Assert.Equal("longtime", WordNormalizer.Normalize(SearchText.WordAt("a long-time friend", 5)));
    }

    [Fact]
    public void SplitWords_AlternatesWordsAndSeparators()
    {
        var runs = SearchText.SplitWords("Please cancel the order.");

        Assert.Equal(
            ["Please", " ", "cancel", " ", "the", " ", "order", "."],
            runs.Select(x => x.Text));
        Assert.Equal([true, false, true, false, true, false, true, false], runs.Select(x => x.IsWord));
    }

    [Fact]
    public void SplitWords_KeepsHyphensAndApostrophesInsideWords()
    {
        Assert.Equal(["long-time"], SearchText.SplitWords("long-time").Select(x => x.Text));
        Assert.Equal(["don't"], SearchText.SplitWords("don't").Select(x => x.Text));
    }

    [Theory]
    [InlineData("Please cancel the order.")]
    [InlineData("...")]
    [InlineData("a")]
    public void SplitWords_AlwaysReconstructsTheOriginal(string text)
    {
        Assert.Equal(text, string.Concat(SearchText.SplitWords(text).Select(x => x.Text)));
    }

    [Fact]
    public void SplitWords_WithEmptyText_ReturnsNothing()
    {
        Assert.Empty(SearchText.SplitWords(string.Empty));
        Assert.Empty(SearchText.SplitWords(null));
    }

    [Theory]
    [InlineData("cancel", "cancel", true)]
    [InlineData("Cancel", "cancel", true)]      // 大小写不敏感
    [InlineData("cancelled", "cancel", true)]   // 追加三字符以内
    [InlineData("cancels", "cancel", true)]
    [InlineData("cancellation", "cancel", false)] // 追加六字符，已是另一个词
    [InlineData("cancel", "can", false)]        // 词干太短，不许前缀放宽
    [InlineData("can", "can", true)]
    [InlineData("order", "cancel", false)]
    [InlineData("", "cancel", false)]
    [InlineData("cancel", "", false)]
    public void IsInflectionOf_MatchesTheSameWordAndItsShortInflections(string word, string stem, bool expected)
    {
        Assert.Equal(expected, SearchText.IsInflectionOf(word, stem));
    }

    [Fact]
    public void Tokenize_TreatsEachIdeographAsOneToken()
    {
        Assert.Equal(["取", "消", "order", "2"], SearchText.Tokenize("取消-Order_2"));
    }
}
