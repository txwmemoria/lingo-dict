using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoDict.App.Services;
using LingoDict.Core.Models;
using LingoDict.Core.Services;
using LingoDict.Core.Study;

namespace LingoDict.App.ViewModels;

/// <summary>
/// 一次复习会话。队列取自到期卡片；评"重来"的卡片会立刻再次到期，
/// 因此队列跑完后重新拉取就能自然地把它们送回来。
/// </summary>
public partial class StudyViewModel : ViewModelBase
{
    private readonly IStudyRepository? _study;
    private readonly ISpeechService? _speech;
    private readonly Func<string?> _voice = () => null;
    private List<StudyCard> _queue = [];
    private int _index;
    private StudyCard? _lastGraded;
    private int _lastGradedIndex;
    private bool _suppressSettingsSave;
    private bool _suppressScopeReload;

    /// <summary>
    /// 当前这批队列是第几代。换词书、重拉队列都会 +1。
    /// </summary>
    /// <remarks>
    /// 每次异步加载都在开头记下自己那一代，await 回来后发现代号变了就整个作废。
    /// 没有这个的话，快速把范围从 A 切到 B 会出现：A 的队列配 B 的统计，
    /// 或者慢一步返回的 A 把 B 的卡片盖掉——用户于是在复习不在当前范围里的词。
    ///
    /// 目前还发生不了：Microsoft.Data.Sqlite 全是同步实现，这些 await 都是就地
    /// 完成的，方法体根本没有挂起点。但那是巧合不是设计，学习库一旦也挪到线程池上
    /// （它本来就该挪，SqliteDictionaryRepository 已经这么做了），竞态立刻就是真的。
    /// </remarks>
    private int _queueGeneration;
    private IReadOnlyDictionary<string, string> _bookTitles = new Dictionary<string, string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(AgainNext), nameof(HardNext), nameof(GoodNext), nameof(EasyNext),
        nameof(CardPhonemes), nameof(CanSpeak))]
    private StudyCard? _currentCard;

    [ObservableProperty]
    private bool _isAnswerVisible;

    /// <summary>
    /// 专注模式：卡片铺满整个窗口。状态放在这里而不是主 ViewModel，
    /// 因为进出专注模式只影响复习会话本身。
    /// </summary>
    [ObservableProperty]
    private bool _isFocusMode;

    /// <summary>撤销按钮的可见性；命令的 CanExecute 也走它。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayDone), nameof(TodayTotal), nameof(TodayProgress),
        nameof(TodayLabel), nameof(QueueDetail), nameof(IsTodayDone))]
    private StudyStats _stats = new(0, 0, 0, 0, 0);

    [ObservableProperty]
    private int _newPerDay = StudySettings.Default.NewPerDay;

    [ObservableProperty]
    private int _reviewPerDay = StudySettings.Default.ReviewPerDay;

    [ObservableProperty]
    private string _message = "还没有卡片。在词条详情页点“加入学习”，或在词书页整本加入。";

    [ObservableProperty]
    private ObservableCollection<StudyBook> _books = [];

    [ObservableProperty]
    private StudyBook? _selectedBook;

    [ObservableProperty]
    private ObservableCollection<StudyDayBar> _history = [];

    [ObservableProperty]
    private double _accuracy;

    [ObservableProperty]
    private int _reviewedLast30Days;

    public StudyViewModel()
    {
    }

    /// <param name="speech">复习时也要能听发音——卡片上原本连朗读按钮都没有。</param>
    /// <param name="voice">取当前嗓音的委托而不是当前值：设置里换了嗓音要立刻生效。</param>
    public StudyViewModel(IStudyRepository study, ISpeechService? speech = null, Func<string?>? voice = null)
    {
        _study = study;
        _speech = speech;
        _voice = voice ?? (() => null);
    }

    // ——— 今天的进度 ———
    //
    // 原来顶上并排五个数字（今日待学/今日已学/到期/未学/共），权重一样、
    // 还互相重复（到期 + 未学 基本就是今日待学），偏偏没回答背单词时唯一
    // 真正关心的问题：今天还剩多少。现在压成一行加一条进度条，
    // 拆解的数字挪到 tooltip 里——想看的人才看。

    public int TodayDone => Stats.ReviewedToday;

    public int TodayTotal => Stats.ReviewedToday + Stats.RemainingToday;

    /// <summary>0–100。今天一张都没有时给 100，进度条是满的而不是空的。</summary>
    public double TodayProgress => TodayTotal == 0 ? 100 : TodayDone * 100.0 / TodayTotal;

    public bool IsTodayDone => Stats.RemainingToday == 0;

    public string TodayLabel => TodayTotal == 0
        ? "今天没有要学的"
        : IsTodayDone
            ? $"今天学完了 · {TodayDone} 张"
            : $"今天 {TodayDone} / {TodayTotal}";

    /// <summary>拆解的数字放 tooltip，需要时才看。</summary>
    public string QueueDetail =>
        $"到期复习 {Stats.DueCount}　未学新词 {Stats.NewCount}　卡片共 {Stats.TotalCards}";

    /// <summary>翻面时通知外层加载完整词条详情。</summary>
    public event EventHandler<StudyCard>? CardRevealed;

    /// <summary>卡片上的音标，拆成一格格。和详情页共用同一张音素表。</summary>
    public IReadOnlyList<PhonemeChip> CardPhonemes => PhonemeChip.From(CurrentCard?.Phonetic);

    public bool CanSpeak => _speech?.IsAvailable == true && CurrentCard is not null;

    /// <summary>朗读卡片上的词。翻面前就能听——听音回忆本来就是一种复习方式。</summary>
    [RelayCommand]
    private void Speak()
    {
        if (_speech is not null && CurrentCard is not null)
        {
            _speech.Speak(CurrentCard.Headword, _voice());
        }
    }

    /// <summary>点音标里的一个音，朗读它的例词。</summary>
    [RelayCommand]
    private void SpeakPhoneme(PhonemeChip? chip)
    {
        if (_speech is not null && chip?.Phoneme is { } phoneme)
        {
            _speech.Speak(phoneme.Example, _voice());
        }
    }

    /// <summary>
    /// 四个档位下次什么时候再出现。
    /// </summary>
    /// <remarks>
    /// 和档位名分开两行显示。原来挤成「良好 · 1 天」一行，
    /// 看不出「良好」是在说我刚才记得怎么样、「1 天」是在说下次什么时候考——
    /// 两个完全不同的东西被一个间隔号连在一起。
    /// </remarks>
    public string AgainNext => Next(ReviewGrade.Again);

    public string HardNext => Next(ReviewGrade.Hard);

    public string GoodNext => Next(ReviewGrade.Good);

    public string EasyNext => Next(ReviewGrade.Easy);

    private string Next(ReviewGrade grade) => CurrentCard is null
        ? string.Empty
        : Describe(SpacedRepetitionScheduler.PreviewIntervalDays(CurrentCard, grade));

    public string AgainLabel => "重来";

    public string HardLabel => "困难";

    public string GoodLabel => "良好";

    public string EasyLabel => "简单";

    public async Task RefreshAsync()
    {
        if (_study is null)
        {
            return;
        }

        var settings = await _study.GetSettingsAsync();
        _suppressSettingsSave = true;
        NewPerDay = settings.NewPerDay;
        ReviewPerDay = settings.ReviewPerDay;
        _suppressSettingsSave = false;

        await RefreshBooksAsync();
        await RefreshHistoryAsync();

        await RefreshStatsAsync(_queueGeneration, ScopeBookId);
        if (CurrentCard is null)
        {
            await LoadQueueAsync();
        }
    }

    /// <summary>当前复习范围；<see langword="null"/> 表示全部词书。</summary>
    private string? ScopeBookId => SelectedBook?.BookId;

    private async Task RefreshBooksAsync()
    {
        var books = await _study!.GetStudyBooksAsync();
        var previous = ScopeBookId;

        _suppressScopeReload = true;
        Books =
        [
            new StudyBook(null, StudyBook.AllBooksTitle, 0),
            .. books.Select(x => x with
            {
                Title = x.BookId is not null && _bookTitles.TryGetValue(x.BookId, out var title)
                    ? title
                    : x.Title,
            }),
        ];
        SelectedBook = Books.FirstOrDefault(x => x.BookId == previous) ?? Books[0];
        _suppressScopeReload = false;
    }

    /// <summary>
    /// 刷新统计。<paramref name="generation"/> 是发起时的队列代号——
    /// 期间换过词书就把结果丢掉，别拿旧范围的数字盖掉新的。
    /// </summary>
    private async Task RefreshStatsAsync(int generation, string? bookId)
    {
        var stats = await _study!.GetStudyStatsAsync(DateTimeOffset.Now, bookId);
        if (generation == _queueGeneration)
        {
            Stats = stats;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var days = await _study!.GetReviewHistoryAsync(DateTimeOffset.Now);
        var peak = days.Count == 0 ? 0 : days.Max(x => x.Total);

        History = [.. days.Select(x => StudyDayBar.From(x, peak))];
        ReviewedLast30Days = days.Sum(x => x.Total);
        Accuracy = ReviewedLast30Days == 0
            ? 0
            : (double)days.Sum(x => x.Total - x.Again) / ReviewedLast30Days;
    }

    /// <summary>
    /// 用户库只存了 book_id，词书标题在内容库那边，由外层注入。
    /// </summary>
    public void SetBookTitles(IReadOnlyDictionary<string, string> titles)
    {
        _bookTitles = titles;
    }

    partial void OnSelectedBookChanged(StudyBook? value)
    {
        if (_study is null || _suppressScopeReload)
        {
            return;
        }

        // 换范围要把当前卡片丢掉重新排队，否则会继续复习不在范围里的词。
        CurrentCard = null;
        _ = LoadQueueAsync();
    }

    [RelayCommand]
    private void ToggleFocus() => IsFocusMode = !IsFocusMode;

    [RelayCommand]
    private void Reveal()
    {
        if (CurrentCard is null)
        {
            return;
        }

        IsAnswerVisible = true;
        CardRevealed?.Invoke(this, CurrentCard);
    }

    [RelayCommand]
    private async Task GradeAsync(ReviewGrade grade)
    {
        if (_study is null || CurrentCard is null)
        {
            return;
        }

        var generation = _queueGeneration;
        var bookId = ScopeBookId;

        try
        {
            var graded = CurrentCard;
            await _study.ReviewAsync(graded, grade, DateTimeOffset.Now);

            // 评这一张的当口换了词书，队列已经整批换掉了，下面这些写回都是旧的。
            if (generation != _queueGeneration)
            {
                return;
            }

            // 评分之前的卡片状态就是撤销要写回去的东西。
            _lastGraded = graded;
            _lastGradedIndex = _index;
            CanUndo = true;

            _index++;
            IsAnswerVisible = false;

            if (_index < _queue.Count)
            {
                CurrentCard = _queue[_index];
                await RefreshStatsAsync(generation, bookId);
            }
            else
            {
                // 队列见底，重新拉一批——评过"重来"的卡片会在这里回到队列。
                await LoadQueueAsync();
            }
        }
        catch (Exception exception)
        {
            Message = $"复习失败：{exception.Message}";
        }
    }

    /// <summary>
    /// 撤销上一次评分。只保留一步：评错一张能立刻挑回来，
    /// 再往前的记录已经影响了后续调度，回滚不安全。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_study is null || _lastGraded is null)
        {
            return;
        }

        var card = _lastGraded;
        var at = _lastGradedIndex;
        var generation = _queueGeneration;
        var bookId = ScopeBookId;

        // 先清掉，撤销过程中出错也不会让用户反复回滚同一张。
        _lastGraded = null;
        CanUndo = false;

        try
        {
            await _study.UndoReviewAsync(card);

            // 评分之后队列可能已经整批换过（换词书，或者 LoadQueueAsync 重拉），
            // 那时按下标回退没有意义。
            if (generation == _queueGeneration
                && at < _queue.Count && _queue[at].WordId == card.WordId)
            {
                _index = at;
                _queue[at] = card;
                CurrentCard = card;
                IsAnswerVisible = true;
                await RefreshStatsAsync(generation, bookId);
                await RefreshHistoryAsync();
            }
            else
            {
                await LoadQueueAsync();
            }
        }
        catch (Exception exception)
        {
            Message = $"撤销失败：{exception.Message}";
        }
    }

    partial void OnNewPerDayChanged(int value) => SaveSettings();

    partial void OnReviewPerDayChanged(int value) => SaveSettings();

    private void SaveSettings()
    {
        if (_study is null || _suppressSettingsSave)
        {
            return;
        }

        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        var generation = _queueGeneration;
        var bookId = ScopeBookId;

        try
        {
            await _study!.SaveSettingsAsync(new StudySettings(NewPerDay, ReviewPerDay));
            if (CurrentCard is null)
            {
                // 调高上限后立刻把新放出来的卡片接上。
                await LoadQueueAsync();
            }
            else
            {
                await RefreshStatsAsync(generation, bookId);
            }
        }
        catch (Exception exception)
        {
            Message = $"保存设置失败：{exception.Message}";
        }
    }

    private async Task LoadQueueAsync()
    {
        if (_study is null)
        {
            return;
        }

        // 范围只在这里读一次：中途再读一次，就会出现队列来自 A、统计来自 B。
        var generation = ++_queueGeneration;
        var bookId = ScopeBookId;
        var now = DateTimeOffset.Now;

        var queue = await _study.BuildDailyQueueAsync(now, bookId);
        if (generation != _queueGeneration)
        {
            return;
        }

        _queue = [.. queue];
        _index = 0;
        IsAnswerVisible = false;
        CurrentCard = _queue.Count > 0 ? _queue[0] : null;

        var stats = await _study.GetStudyStatsAsync(now, bookId);
        if (generation != _queueGeneration)
        {
            return;
        }

        Stats = stats;
        await RefreshHistoryAsync();
        if (generation != _queueGeneration)
        {
            return;
        }

        if (CurrentCard is null)
        {
            Message = Stats switch
            {
                { TotalCards: 0 } => "还没有卡片。在词条详情页点“加入学习”，或在词书页整本加入。",
                { DueCount: 0 } => $"{Stats.TotalCards} 张卡片都不到期，明天再来。",
                _ => $"今天的额度用完了（已学 {Stats.ReviewedToday} 张）。还有 {Stats.DueCount} 张到期，"
                     + "可以调高下面的每日上限继续。",
            };
        }
    }

    /// <summary>下次什么时候再考。写成「…后」，让人一眼看出这是时间不是别的。</summary>
    private static string Describe(int days) => days switch
    {
        <= 0 => "稍后再考",
        1 => "1 天后",
        < 30 => $"{days} 天后",
        < 365 => $"{days / 30} 个月后",
        _ => $"{days / 365} 年后",
    };
}
