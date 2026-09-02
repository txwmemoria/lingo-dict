using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoDict.Core.Models;
using LingoDict.Core.Services;

namespace LingoDict.App.ViewModels;

/// <summary>
/// 写作检查页。用户贴一段英文进来，逐条给出建议。
/// </summary>
/// <remarks>
/// 措辞一律是「建议」不是「错误」：模型在评测集上 81%，剩下的两成不该以
/// 「你写错了」的口气呈现。每条建议都能一键查词，让用户自己拿词典里的
/// 例句核对——这也是当初否掉「让模型讲语法」的替代方案（见 eval/RESULTS.md）。
///
/// 模型权重不随应用分发（123 MiB，且是仅限非商业用途的第三方产物），
/// 所以这一页还要负责把它装上：下载或从本地导入。
/// </remarks>
public partial class WritingViewModel : ViewModelBase
{
    /// <summary>敲完最后一个键多久开始检查。比搜索的 140ms 长：这里一次要跑整段。</summary>
    private const int DebounceMilliseconds = 500;

    private const string Idle = "在上面写或粘贴一段英文，停下来就会自动检查。";

    private readonly IGrammarChecker? _checker;
    private readonly IGrammarModelInstaller? _installer;
    private CancellationTokenSource? _pending;
    private CancellationTokenSource? _install;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private ObservableCollection<WritingSuggestion> _suggestions = [];

    [ObservableProperty]
    private string _message = Idle;

    [ObservableProperty]
    private bool _isChecking;

    /// <summary>模型装上了没有。没装时整页降级成一段说明加两个装模型的入口。</summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>正在下载或导入。这期间两个入口都禁掉，只留「取消」。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(ImportModelCommand), nameof(CancelInstallCommand))]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installPercent;

    [ObservableProperty]
    private string _installStatus = string.Empty;

    /// <summary>配了下载源没有。没配时只显示「从本地导入」，不摆一个点了没反应的按钮。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    private bool _canDownload;

    /// <summary>点建议里的词时，请主视图在右侧打开对应词条。</summary>
    public event EventHandler<string>? LookupRequested;

    /// <summary>
    /// 让用户挑一个本地 .onnx。由视图层接上——ViewModel 不该去碰 StorageProvider。
    /// </summary>
    public Func<Task<string?>>? PickModelFile { get; set; }

    public WritingViewModel()
    {
        Message = "（设计预览）";
    }

    public WritingViewModel(IGrammarChecker checker, IGrammarModelInstaller? installer = null)
    {
        _checker = checker;
        _installer = installer;
        CanDownload = installer?.CanDownload == true;
        Sync();
    }

    /// <summary>模型体积，显示在下载按钮上。</summary>
    public string ModelSize => _installer is null ? "" : $"{_installer.ExpectedBytes / 1024.0 / 1024.0:0} MiB";

    /// <summary>没装模型时那段说明。把来源和许可讲清楚，别让用户稀里糊涂点下载。</summary>
    public string ModelNotice =>
        $"写作检查需要一个 {ModelSize} 的语法模型（GECToR）。它是第三方产物"
        + "（gotutiyan/gector，仅限非商业用途），所以没有随应用一起分发。";

    /// <summary>切到这一页时先把模型读进内存：123 MiB 的会话要一两秒，等用户敲完字再加载就像卡住了。</summary>
    public async Task WarmUpAsync()
    {
        if (_checker is null || !_checker.IsReady)
        {
            return;
        }

        await _checker.WarmUpAsync().ConfigureAwait(true);
        Sync();
    }

    partial void OnTextChanged(string value)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;

        if (_checker is null || !_checker.IsReady || string.IsNullOrWhiteSpace(value))
        {
            Suggestions = [];
            if (_checker is not null && _checker.IsReady)
            {
                Message = Idle;
            }

            return;
        }

        _pending = new CancellationTokenSource();
        _ = CheckAfterDelayAsync(value, _pending.Token);
    }

    private async Task CheckAfterDelayAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
            IsChecking = true;
            var issues = await _checker!.CheckAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Suggestions = new ObservableCollection<WritingSuggestion>(
                issues.Select(x => new WritingSuggestion(x, Context(text, x))));
            IsAvailable = _checker.IsReady;
            Message = !IsAvailable
                ? _checker.UnavailableReason ?? "写作检查不可用"
                : Suggestions.Count == 0
                    ? "没看出问题。这不等于完全正确——模型只覆盖常见的语法和用词。"
                    : $"{Suggestions.Count} 条建议。点词条可以在右边查证，确认了再采纳。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Message = $"检查失败：{exception.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartDownload))]
    private Task DownloadModelAsync() =>
        InstallAsync((installer, progress, token) => installer.DownloadAsync(progress, token));

    private bool CanStartDownload() => CanDownload && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanStartImport))]
    private async Task ImportModelAsync()
    {
        if (PickModelFile is null || await PickModelFile() is not { Length: > 0 } picked)
        {
            return;
        }

        await InstallAsync((installer, progress, token) =>
            installer.InstallFromFileAsync(picked, progress, token));
    }

    private bool CanStartImport() => !IsInstalling;

    [RelayCommand(CanExecute = nameof(IsInstalling))]
    private void CancelInstall() => _install?.Cancel();

    private async Task InstallAsync(
        Func<IGrammarModelInstaller, IProgress<ModelInstallProgress>, CancellationToken, Task> run)
    {
        if (_installer is null || _checker is null)
        {
            return;
        }

        _install = new CancellationTokenSource();
        IsInstalling = true;
        InstallPercent = 0;
        InstallStatus = "准备中…";

        // Progress<T> 是异步派发的：最后几条会排在 run 返回之后才到，
        // 把「已取消」或错误原因盖成一句「下载中 …」。装完就不再理会它们。
        var running = true;
        var progress = new Progress<ModelInstallProgress>(x =>
        {
            if (!running)
            {
                return;
            }

            InstallPercent = x.Total > 0 ? x.Received * 100.0 / x.Total : 0;
            InstallStatus = Describe(x);
        });

        try
        {
            await run(_installer, progress, _install.Token);
            running = false;

            // 检查器构造时记下的是「没有安装模型」，装完必须让它重新解析，
            // 否则用户得重启应用才用得上刚装好的权重。
            _checker.Invalidate();
            await _checker.WarmUpAsync();
            Sync();
            InstallStatus = string.Empty;

            // 装之前用户可能已经把文字写好了，装完直接给他检查出来
            if (IsAvailable && !string.IsNullOrWhiteSpace(Text))
            {
                _pending = new CancellationTokenSource();
                _ = CheckAfterDelayAsync(Text, _pending.Token);
            }
        }
        catch (OperationCanceledException)
        {
            running = false;
            InstallStatus = "已取消。下好的部分留着，再点一次会接着下。";
        }
        catch (Exception exception)
        {
            running = false;
            InstallStatus = exception.Message;
        }
        finally
        {
            running = false;
            IsInstalling = false;
            _install?.Dispose();
            _install = null;
        }
    }

    /// <summary>在右侧词典里打开这条建议牵涉的词。</summary>
    [RelayCommand]
    private void Lookup(WritingSuggestion? suggestion)
    {
        if (suggestion?.LookupWord is { Length: > 0 } word)
        {
            LookupRequested?.Invoke(this, word);
        }
    }

    /// <summary>采纳一条建议：改文本，改完自动重跑（改一处常会牵出下一处）。</summary>
    [RelayCommand]
    private void Accept(WritingSuggestion? suggestion)
    {
        if (suggestion is null || suggestion.Issue.Start + suggestion.Issue.Length > Text.Length)
        {
            return;
        }

        var issue = suggestion.Issue;
        var head = Text[..issue.Start];
        var tail = Text[(issue.Start + issue.Length)..];

        Text = issue.Kind switch
        {
            GrammarEditKind.Insert => head + issue.Original + " " + issue.Suggestion + tail,
            // 删词要把它前面那个空格一起带走，否则留下两个空格
            GrammarEditKind.Delete => (head.EndsWith(' ') ? head[..^1] : head) + tail,
            _ => head + issue.Suggestion + tail,
        };
    }

    [RelayCommand]
    private void Clear() => Text = string.Empty;

    /// <summary>塞一段典型的中式英语，让人一眼看到这个功能在做什么。</summary>
    [RelayCommand]
    private void LoadSample() =>
        Text = "I arrived at Beijing last week. I am student of Peking University. "
             + "He go to school by bike every day. My teacher gave me some advices "
             + "about my study. Although it rained, but I still went to the library.";

    /// <summary>把检查器当前的可用状态同步到界面。</summary>
    private void Sync()
    {
        if (_checker is null)
        {
            return;
        }

        IsAvailable = _checker.IsReady;
        if (!IsAvailable)
        {
            Suggestions = [];
            Message = _checker.UnavailableReason ?? "写作检查不可用";
        }
        else
        {
            Message = Idle;
        }
    }

    private static string Describe(ModelInstallProgress progress)
    {
        var received = progress.Received / 1024.0 / 1024.0;
        var total = progress.Total / 1024.0 / 1024.0;
        var speed = progress.BytesPerSecond > 0
            ? $"，{progress.BytesPerSecond / 1024 / 1024:0.0} MiB/s"
            : "";
        return total > 0
            ? $"{progress.Stage} {received:0} / {total:0} MiB{speed}"
            : $"{progress.Stage} {received:0} MiB{speed}";
    }

    /// <summary>截出这条建议周围的一小段原文，让人不用回头找是哪一句。</summary>
    private static string Context(string text, WritingIssue issue)
    {
        const int Around = 24;
        var from = Math.Max(0, issue.Start - Around);
        var to = Math.Min(text.Length, issue.Start + issue.Length + Around);
        var slice = text[from..to].ReplaceLineEndings(" ");
        return (from > 0 ? "…" : "") + slice + (to < text.Length ? "…" : "");
    }
}

/// <summary>把 <see cref="WritingIssue"/> 包成界面直接能绑的形状。</summary>
public sealed class WritingSuggestion
{
    public WritingSuggestion(WritingIssue issue, string context)
    {
        Issue = issue;
        Context = context;
    }

    public WritingIssue Issue { get; }

    /// <summary>建议所在的一小段原文。</summary>
    public string Context { get; }

    public string Category => Issue.Category;

    /// <summary>一句话说清要做什么。</summary>
    public string Action => Issue.Kind switch
    {
        GrammarEditKind.Insert => $"在 {Issue.Original} 后面加 {Issue.Suggestion}",
        GrammarEditKind.Delete => $"去掉 {Issue.Original}",
        GrammarEditKind.Merge => $"{Issue.Original} 和后面一个词连写成 {Issue.Suggestion}",
        GrammarEditKind.Split => $"{Issue.Original} 拆成 {Issue.Suggestion}",
        _ => $"{Issue.Original} → {Issue.Suggestion}",
    };

    /// <summary>把握度，显示成百分比。低于阈值的建议在内核那一层就已经滤掉了。</summary>
    public string Confidence => $"{Issue.Confidence * 100:0}%";

    /// <summary>
    /// 点「查词」时去查哪个词：建议改成什么就查什么，删词则查被删的那个。
    /// 词形变换（goes、advice）查的是变换后的形式，词典能按词形还原找到原词。
    /// </summary>
    public string LookupWord =>
        (Issue.Kind == GrammarEditKind.Delete ? Issue.Original : Issue.Suggestion).Trim('.', ',', '!', '?', ';', ':');

    /// <summary>没有可查的词（纯标点建议）时把按钮藏起来。</summary>
    public bool CanLookup => LookupWord.Length > 0 && LookupWord.Any(char.IsLetter);
}
