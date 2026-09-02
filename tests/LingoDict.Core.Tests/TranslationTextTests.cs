using LingoDict.Core.Text;

namespace LingoDict.Core.Tests;

/// <summary>
/// 「其他词书的释义」里去掉重复的英文尾巴。
/// </summary>
/// <remarks>
/// 源数据把词性、中文、英文用空格拼成一行，回头切不开，所以这里靠公共后缀
/// 而不是解析。既然是纯文本比对，误伤的边界就得逐条钉住。
/// </remarks>
public sealed class TranslationTextTests
{
    private const string Reference =
        "v 废除，废止；取消，革除 to officially end a law, system etc, especially one that has existed for a long time";

    [Fact]
    public void TheRepeatedEnglishGoesAndTheChineseStays()
    {
        var value = "vt 彻底废除，废止 to officially end a law, system etc, especially one that has existed for a long time";

        Assert.Equal("vt 彻底废除，废止", TranslationText.WithoutRepeatedTail(value, Reference));
    }

    [Fact]
    public void EnglishThatIsNotRepeatedIsKept()
    {
        // 别的词书给了不一样的解释，那才是这一段存在的意义
        var value = "vt 废除 to put an end to something completely";

        Assert.Equal(value, TranslationText.WithoutRepeatedTail(value, Reference));
    }

    [Fact]
    public void ALineThatIsOnlyEnglishSurvivesIntact()
    {
        // 砍完什么都不剩就别砍——宁可重复，不可留白
        var value = "to officially end a law, system etc, especially one that has existed for a long time";

        Assert.Equal(value, TranslationText.WithoutRepeatedTail(value, Reference));
    }

    [Fact]
    public void ChineseTailsAreLeftAlone()
    {
        // 只吃 ASCII 开头的尾巴。两条都以「；用于比较级」收尾是常有的事，
        // 那是中文内容，不是重复的英文释义。
        var reference = "adj 更好的；较好的；用于比较级和最高级之前";
        var value = "adj 更佳的；用于比较级和最高级之前";

        Assert.Equal(value, TranslationText.WithoutRepeatedTail(value, reference));
    }

    [Fact]
    public void TheCutLandsOnAWordBoundaryNotInsideAWord()
    {
        // 最长公共后缀是 "order of two countries nearby"，但它在 value 里
        // 从 "b|order" 的词中间开始。只在空格处下刀，所以退到上一个空格。
        var reference = "v 命令 to give an order of two countries nearby";
        var value = "n 边境 a line at the border of two countries nearby";

        Assert.Equal("n 边境 a line at the border", TranslationText.WithoutRepeatedTail(value, reference));
    }

    [Fact]
    public void ShortSharedTailsAreNotWorthCutting()
    {
        var reference = "n 猫 a cat";
        var value = "n 小猫 a cat";

        // 太短的尾巴切了省不下什么，反而更容易误伤
        Assert.Equal(value, TranslationText.WithoutRepeatedTail(value, reference));
    }

    [Fact]
    public void EveryLineIsHandledSeparately()
    {
        var reference = "v 废除 to officially end a law, system etc, especially one that has existed"
            + "\nn 废止 the act of ending something officially and completely";
        var value = "vt 取消 to officially end a law, system etc, especially one that has existed"
            + "\nn 撤销 the act of ending something officially and completely";

        Assert.Equal("vt 取消\nn 撤销", TranslationText.WithoutRepeatedTail(value, reference));
    }

    [Theory]
    [InlineData("", "anything")]
    [InlineData("anything", "")]
    public void EmptyInputIsReturnedUnchanged(string value, string reference)
    {
        Assert.Equal(value, TranslationText.WithoutRepeatedTail(value, reference));
    }
}
