using LingoDict.Core.Models;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 从「一段英文」到「一条条带位置的建议」的整体行为。
/// </summary>
/// <remarks>
/// <see cref="TaggingGoldenTests"/> 保证 C# 和 Python 拿到同样的标签；
/// 这里保证标签之后的那一半——词形变换、位置换算、置信度过滤——是对的。
/// </remarks>
public sealed class WritingCheckTests
{
    private static readonly Lazy<GectorGrammarChecker> Shared = new(() =>
    {
        var checker = new GectorGrammarChecker(GrammarModel.Path!);
        checker.WarmUpAsync().GetAwaiter().GetResult();
        Assert.True(checker.IsReady, checker.UnavailableReason);
        return checker;
    });

    private static IReadOnlyList<WritingIssue> Check(string text)
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);
        return Shared.Value.CheckAsync(text).GetAwaiter().GetResult();
    }

    [Theory]
    // 生成式模型讲错的那道经典题
    [InlineData("I arrived at Beijing yesterday.", "at", "in", "介词")]
    // 主谓一致要查动词词形表，不是标签里现成的
    [InlineData("He go to school by bike.", "go", "goes", "动词形式")]
    // 不可数名词，走 $TRANSFORM_AGREEMENT_SINGULAR
    [InlineData("He gave me some advices.", "advices", "advice", "单复数")]
    // 缺冠词，走 $APPEND
    [InlineData("I am student.", "am", "a", "冠词")]
    public void SuggestsTheExpectedFix(string text, string original, string suggestion, string category)
    {
        var issues = Check(text);

        var hit = Assert.Single(issues, x => x.Original == original);
        Assert.Equal(suggestion, hit.Suggestion);
        Assert.Equal(category, hit.Category);
        // 位置要能直接拿去在原文上画下划线
        Assert.Equal(original, text.Substring(hit.Start, hit.Length));
    }

    [Theory]
    [InlineData("I arrived in Beijing yesterday.")]
    [InlineData("He goes to school by bike.")]
    [InlineData("I am a student.")]
    [InlineData("The film was boring.")]
    public void LeavesCorrectSentencesAlone(string text)
    {
        // 误报比漏报要命：写对了还被划红线，用户第二次就不开这个功能了
        Assert.Empty(Check(text));
    }

    [Fact]
    public void FindsIssuesAcrossSentencesWithRightOffsets()
    {
        const string text = "I am student. He go to school. I arrived at Beijing.";

        var issues = Check(text);

        Assert.True(issues.Count >= 3, $"只找到 {issues.Count} 条：{string.Join(" / ", issues.Select(x => x.Label))}");
        foreach (var issue in issues)
        {
            Assert.Equal(issue.Original, text.Substring(issue.Start, issue.Length));
        }

        // 建议按原文顺序排列，界面直接照单渲染
        Assert.Equal(issues.Select(x => x.Start).Order(), issues.Select(x => x.Start));
    }

    [Fact]
    public void EmptyTextIsNotAnError()
    {
        Assert.Empty(Check(""));
        Assert.Empty(Check("   \n  "));
    }

    [Fact]
    public async Task ReportsMissingModelInsteadOfThrowing()
    {
        // 模型是单独分发的 129 MB 产物，没装上时功能应当优雅缺席
        var checker = new GectorGrammarChecker(Path.Combine(Path.GetTempPath(), "no-such-model.onnx"));

        Assert.False(checker.IsReady);
        Assert.NotNull(checker.UnavailableReason);
        Assert.Empty(await checker.CheckAsync("I am student.", TestContext.Current.CancellationToken));
    }
}
