using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;
using LingoDict.Core.Services;

namespace LingoDict.App.Tests;

/// <summary>
/// 写作页在拿到建议之后的那一半：文字改写、查词转发、模型缺席时的降级。
/// </summary>
/// <remarks>
/// 这里用假的检查器，不碰 129 MB 的模型——模型本身的准确率由
/// LingoDict.Grammar.Tests 保证，这里要验的是「建议怎么落到文本上」。
/// 采纳一条建议是纯字符串下标运算，差一个字符就会把词吃掉半个。
/// </remarks>
public sealed class WritingViewModelTests
{
    [AvaloniaFact]
    public void AcceptingAReplacement_SwapsExactlyThatWord()
    {
        const string text = "I arrived at Beijing yesterday.";
        var model = Create(new WritingIssue(
            text.IndexOf("at"), 2, "at", "in", GrammarEditKind.Replace, "介词", "$REPLACE_in", 0.82));

        model.Text = text;
        Pump();
        model.AcceptCommand.Execute(model.Suggestions[0]);

        Assert.Equal("I arrived in Beijing yesterday.", model.Text);
    }

    [AvaloniaFact]
    public void AcceptingAnInsertion_PutsTheWordAfterTheAnchor()
    {
        const string text = "I am student.";
        var model = Create(new WritingIssue(
            text.IndexOf("am"), 2, "am", "a", GrammarEditKind.Insert, "冠词", "$APPEND_a", 0.47));

        model.Text = text;
        Pump();
        model.AcceptCommand.Execute(model.Suggestions[0]);

        Assert.Equal("I am a student.", model.Text);
    }

    [AvaloniaFact]
    public void AcceptingADeletion_TakesTheLeadingSpaceWithIt()
    {
        const string text = "Because he was ill, so he did not come.";
        var model = Create(new WritingIssue(
            text.IndexOf("so"), 2, "so", "", GrammarEditKind.Delete, "多余的词", "$DELETE", 0.7));

        model.Text = text;
        Pump();
        model.AcceptCommand.Execute(model.Suggestions[0]);

        // 留下两个空格是最容易漏掉的那种 bug
        Assert.Equal("Because he was ill, he did not come.", model.Text);
    }

    [AvaloniaFact]
    public void LookingUpASuggestion_AsksTheHostToOpenTheReplacementWord()
    {
        var model = Create(new WritingIssue(10, 2, "at", "in", GrammarEditKind.Replace, "介词", "$REPLACE_in", 0.82));
        string? asked = null;
        model.LookupRequested += (_, word) => asked = word;

        model.Text = "I arrived at Beijing.";
        Pump();
        model.LookupCommand.Execute(model.Suggestions[0]);

        // 查的是建议里的那个词，不是原词——用户要看的是 in 的例句
        Assert.Equal("in", asked);
    }

    [AvaloniaFact]
    public void ClearingTheTextDropsTheSuggestions()
    {
        var model = Create(new WritingIssue(0, 1, "I", "in", GrammarEditKind.Replace, "介词", "$REPLACE_in", 0.9));

        model.Text = "I arrived at Beijing.";
        Pump();
        Assert.NotEmpty(model.Suggestions);

        model.ClearCommand.Execute(null);
        Pump();
        Assert.Empty(model.Suggestions);
    }

    [AvaloniaFact]
    public void WithoutTheModel_ThePageOffersToInstallIt()
    {
        var checker = new FakeGrammarChecker([]) { Ready = false, Reason = "没有安装写作检查模型" };
        var model = new WritingViewModel(checker, new FakeInstaller());

        Assert.False(model.IsAvailable);
        Assert.Contains("没有安装写作检查模型", model.Message);
        // 说明里要讲清体积、来源和许可，别让用户稀里糊涂点下载
        Assert.Contains("123 MiB", model.ModelNotice);
        Assert.Contains("非商业", model.ModelNotice);
    }

    [AvaloniaFact]
    public void NoDownloadSourceConfigured_HidesTheDownloadButton()
    {
        var model = new WritingViewModel(
            new FakeGrammarChecker([]) { Ready = false }, new FakeInstaller { Downloadable = false });

        Assert.False(model.CanDownload);
        Assert.False(model.DownloadModelCommand.CanExecute(null));
        // 本地导入始终可用——README 教用户自己生成模型，那条路不能堵
        Assert.True(model.ImportModelCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void InstallingTheModel_MakesThePageUsableWithoutARestart()
    {
        // 检查器构造时记的是「没有安装」。装完不通知它，用户得重启才用得上。
        var checker = new FakeGrammarChecker([Issue]) { Ready = false, Reason = "没有安装写作检查模型" };
        var installer = new FakeInstaller { OnInstall = () => checker.Ready = true };
        var model = new WritingViewModel(checker, installer);
        Assert.False(model.IsAvailable);

        model.DownloadModelCommand.Execute(null);
        Pump();

        Assert.True(model.IsAvailable);
        Assert.True(checker.Invalidated);
        Assert.Empty(model.InstallStatus);
    }

    [AvaloniaFact]
    public void InstallingRunsACheckOnTextTheUserAlreadyTyped()
    {
        var checker = new FakeGrammarChecker([Issue]) { Ready = false };
        var model = new WritingViewModel(checker, new FakeInstaller { OnInstall = () => checker.Ready = true });

        // 没装模型时也允许先写：装完应当直接给出结果，而不是等用户再敲一个字
        model.Text = "I arrived at Beijing.";
        model.DownloadModelCommand.Execute(null);
        Pump();

        Assert.NotEmpty(model.Suggestions);
    }

    [AvaloniaFact]
    public void AFailedInstall_ShowsWhyAndLeavesThePageInTheNotInstalledState()
    {
        var checker = new FakeGrammarChecker([]) { Ready = false };
        var model = new WritingViewModel(
            checker,
            new FakeInstaller { OnInstall = () => throw new IOException("所有下载源都没成功") });

        model.DownloadModelCommand.Execute(null);
        Pump();

        Assert.False(model.IsAvailable);
        Assert.Contains("所有下载源都没成功", model.InstallStatus);
        Assert.False(model.IsInstalling);
    }

    private static readonly WritingIssue Issue =
        new(10, 2, "at", "in", GrammarEditKind.Replace, "介词", "$REPLACE_in", 0.82);

    [AvaloniaFact]
    public void NoIssues_SaysSoWithoutClaimingTheTextIsPerfect()
    {
        var model = Create();

        model.Text = "I arrived in Beijing yesterday.";
        Pump();

        Assert.Empty(model.Suggestions);
        // 81% 的模型不能说「完全正确」
        Assert.Contains("不等于完全正确", model.Message);
    }

    private static WritingViewModel Create(params WritingIssue[] issues) =>
        new(new FakeGrammarChecker(issues));

    /// <summary>输入后有 500ms 防抖，所以这里要比别处多转一会儿。</summary>
    private static void Pump()
    {
        for (var round = 0; round < 32; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeInstaller : IGrammarModelInstaller
    {
        public bool Downloadable { get; init; } = true;

        public Action? OnInstall { get; init; }

        public bool IsInstalled { get; private set; }

        public string ModelPath => "(fake)";

        public long ExpectedBytes => 128_683_357;

        public bool CanDownload => Downloadable;

        public Task DownloadAsync(IProgress<ModelInstallProgress>? progress, CancellationToken cancellationToken = default)
        {
            progress?.Report(new ModelInstallProgress(1, 2, 0, "下载中"));
            OnInstall?.Invoke();
            IsInstalled = true;
            return Task.CompletedTask;
        }

        public Task InstallFromFileAsync(
            string sourcePath, IProgress<ModelInstallProgress>? progress, CancellationToken cancellationToken = default)
            => DownloadAsync(progress, cancellationToken);

        public void Remove() => IsInstalled = false;
    }

    private sealed class FakeGrammarChecker(IReadOnlyList<WritingIssue> issues) : IGrammarChecker
    {
        public bool Ready { get; set; } = true;

        public string? Reason { get; init; }

        public bool Invalidated { get; private set; }

        public bool IsReady => Ready;

        public string? UnavailableReason => Reason;

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Invalidate() => Invalidated = true;

        public Task<IReadOnlyList<WritingIssue>> CheckAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(issues);
    }
}
