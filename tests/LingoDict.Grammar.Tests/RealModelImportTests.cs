using LingoDict.Core.Services;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 拿真模型走一遍导入，包括真的 onnxruntime 校验。
/// </summary>
/// <remarks>
/// <see cref="ModelInstallerTests"/> 里的载荷是随机字节，校验那一步被换成了空实现——
/// 那些用例验的是搬运逻辑。这里补上剩下的一半：真文件真校验，装完检查器
/// 不重启就能用。这是用户点「从本地文件导入」时实际会走的路径。
/// </remarks>
public sealed class RealModelImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "lingodict-import-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportingTheRealModelMakesACheckerUsable()
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);

        var target = Path.Combine(_directory, "grammar", GrammarModelLocation.FileName);
        var installer = new GrammarModelInstaller(targetPath: () => target);
        Assert.False(installer.IsInstalled);

        // 同步记录，别用 Progress<T>：它是异步派发的，断言可能跑在回调之前
        var seen = new List<ModelInstallProgress>();
        var recorder = new DelegateProgress(seen.Add);
        await installer.InstallFromFileAsync(
            GrammarModel.Path!, recorder, TestContext.Current.CancellationToken);

        Assert.True(installer.IsInstalled);
        Assert.Contains(seen, x => x.Stage == "检查模型");

        // 装到哪儿就得能从哪儿用起来
        var checker = new GectorGrammarChecker(() => target);
        Assert.True(checker.IsReady);
        var issues = await checker.CheckAsync("I arrived at Beijing.", TestContext.Current.CancellationToken);
        Assert.Contains(issues, x => x is { Original: "at", Suggestion: "in" });
    }

    [Fact]
    public async Task ACheckerPickingUpAModelInstalledLaterNeedsNoRestart()
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);

        var target = Path.Combine(_directory, "grammar", GrammarModelLocation.FileName);
        var checker = new GectorGrammarChecker(() => File.Exists(target) ? target : null);

        // 应用启动时模型还没装：检查器应当报缺席，而不是抛异常
        Assert.False(checker.IsReady);
        Assert.Empty(await checker.CheckAsync("I am student.", TestContext.Current.CancellationToken));

        await new GrammarModelInstaller(targetPath: () => target)
            .InstallFromFileAsync(GrammarModel.Path!, null, TestContext.Current.CancellationToken);

        // 装完必须重新解析，否则用户得重启才用得上刚装好的权重
        checker.Invalidate();
        Assert.True(checker.IsReady);
        Assert.NotEmpty(await checker.CheckAsync("I am student.", TestContext.Current.CancellationToken));
    }

    private sealed class DelegateProgress(Action<ModelInstallProgress> record) : IProgress<ModelInstallProgress>
    {
        public void Report(ModelInstallProgress value) => record(value);
    }

    [Fact]
    public async Task RefusesAFileThatIsNotThisModel()
    {
        Directory.CreateDirectory(_directory);
        var bogus = Path.Combine(_directory, "bogus.onnx");
        await File.WriteAllTextAsync(bogus, "这不是模型", TestContext.Current.CancellationToken);

        var target = Path.Combine(_directory, "grammar", GrammarModelLocation.FileName);
        var installer = new GrammarModelInstaller(targetPath: () => target);

        await Assert.ThrowsAnyAsync<Exception>(
            () => installer.InstallFromFileAsync(bogus, null, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }
}
