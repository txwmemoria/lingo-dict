using System.Diagnostics;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 模型必须是懒加载的。
/// </summary>
/// <remarks>
/// 应用在启动时就构造了检查器（App.axaml.cs），如果构造函数顺手把 129 MB
/// 读进来，冷启动会从 1.5 秒变成 3 秒——而绝大多数用户这次根本不会打开写作页。
/// 这条不会自己报警，只会让启动慢下来，所以钉在测试里。
/// </remarks>
public sealed class LazyLoadingTests
{
    [Fact]
    public void ConstructingTheCheckerDoesNotTouchTheWeights()
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);

        var watch = Stopwatch.StartNew();
        var checker = new GectorGrammarChecker(GrammarModel.Path!);
        watch.Stop();

        Assert.False(checker.IsLoaded);
        Assert.True(checker.IsReady);
        Assert.True(watch.ElapsedMilliseconds < 200, $"构造花了 {watch.ElapsedMilliseconds} ms，八成是把权重读进来了");
    }

    [Fact]
    public async Task WarmUpLoadsOnceAndIsSafeToCallAgain()
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);

        var checker = new GectorGrammarChecker(GrammarModel.Path!);
        await checker.WarmUpAsync(TestContext.Current.CancellationToken);
        Assert.True(checker.IsLoaded, checker.UnavailableReason);

        // 切标签页会反复触发预热，第二次必须是空操作而不是再建一个 session
        var watch = Stopwatch.StartNew();
        await checker.WarmUpAsync(TestContext.Current.CancellationToken);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 100, $"第二次预热花了 {watch.ElapsedMilliseconds} ms");
    }
}
