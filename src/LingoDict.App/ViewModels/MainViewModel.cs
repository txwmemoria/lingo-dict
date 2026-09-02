using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoDict.App.Services;
using LingoDict.App.Controls;
using LingoDict.Core.Models;
using LingoDict.Core.Services;

namespace LingoDict.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<WordSummary> _searchResults = [];

    [ObservableProperty]
    private WordSummary? _selectedSearchResult;

    /// <summary>
    /// 短语命中。中文母语者最典型的错误是逐词翻译（大雨→big rain），
    /// 词义对应救不了，得让搭配本身能被搜到。
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<PhraseHit> _phraseResults = [];

    [ObservableProperty]
    private ObservableCollection<BookSummary> _books = [];

    [ObservableProperty]
    private BookSummary? _selectedBook;

    [ObservableProperty]
    private ObservableCollection<WordSummary> _bookWords = [];

    [ObservableProperty]
    private WordSummary? _selectedBookWord;

    [ObservableProperty]
    private string _bookFilter = string.Empty;

    [ObservableProperty]
    private ObservableCollection<AlternateEntry> _alternateEntries = [];

    /// <summary>全部词头（键为归一化形式）。例句的 hover 判定与释义预览都查这里。</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, WordPreview> _wordIndex = new Dictionary<string, WordPreview>();

    [ObservableProperty]
    private ObservableCollection<FavoriteEntry> _favorites = [];

    [ObservableProperty]
    private FavoriteEntry? _selectedFavorite;

    [ObservableProperty]
    private ObservableCollection<HistoryEntry> _history = [];

    [ObservableProperty]
    private HistoryEntry? _selectedHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UkPhonemes), nameof(UsPhonemes))]
    private WordDetail? _selectedWord;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph))]
    private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudyGlyph))]
    private bool _isInStudy;

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private bool _isNoteDirty;

    [ObservableProperty]
    private ThemeModeOption _selectedThemeMode = ThemeModeOption.All[0];

    /// <summary>当前主题色的 id。设置页上的色点点一下就换，所以不做成 ComboBox 的选中项。</summary>
    [ObservableProperty]
    private string _selectedAccent = AppearanceTheme.DefaultAccent;

    [ObservableProperty]
    private FontScaleOption _selectedFontScale = FontScaleOption.All[1];

    [ObservableProperty]
    private string _selectedFontFamily = FontFamilyOption.Default;

    [ObservableProperty]
    private string _selectedSpeechVoice = SpeechVoiceOption.System;

    [ObservableProperty]
    private double _leftPaneWidth = AppearanceSettings.DefaultPaneWidth;

    /// <summary>
    /// 详情区够宽时正文分两列。由视图在尺寸变化时写入——
    /// Avalonia 没有 media query，断点只能在这一处判断。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(DetailMaxWidth),
        nameof(DetailGapWidth),
        nameof(DetailAsideWidth),
        nameof(DetailAsideColumn),
        nameof(DetailAsideRow))]
    private bool _isTwoColumnDetail;

    /// <summary>
    /// 「上一个 / 下一个」翻的是哪个列表。由视图在切标签页时写入——
    /// 只有视图知道用户正在看哪一页。
    /// </summary>
    [ObservableProperty]
    private BrowseScope _activeScope = BrowseScope.Search;

    [ObservableProperty]
    private string _statusMessage = "正在初始化词典…";

    [ObservableProperty]
    private bool _isBusy;

    private readonly IDictionaryRepository? _repository;
    private readonly ISpeechService? _speech;
    private IUserDataRepository? _userData;
    private IStudyRepository? _study;
    private CancellationTokenSource? _searchCancellation;
    private bool _suppressNoteTracking;
    private bool _suppressAppearanceSave;
    private string? _userDataFailure;

    // 每次加载词条自增；await 之后如果已经不是当前代，说明用户又切了词，结果必须丢弃。
    private int _loadGeneration;

    // 词书页的完整词表，BookWords 是按 BookFilter 过滤后的视图。
    private IReadOnlyList<WordSummary> _allBookWords = [];

    // 恢复书签时会给 SelectedBookWord 赋值，那次不该反过来又存一遍书签。
    private bool _restoringBookmark;
    private int _bookGeneration;
    private string? _loadedBookId;

    // 自动保存失败时暂存在这里，下次切词再补写，不让用户白打一遍。
    private (long WordId, string Text)? _unsavedNote;

    // 刷新收藏/历史列表时会重新设置选中项；不屏蔽的话会反过来覆盖用户刚点开的词条。
    private bool _suppressListSelection;

    // 浏览器式的前进/后退。只驱动右侧详情页，不去改左栏列表的选中项。
    private readonly Stack<WordReference> _back = new();
    private readonly Stack<WordReference> _forward = new();

    public MainViewModel()
    {
        Study = new StudyViewModel();
        Writing = new WritingViewModel();
        UserDataLocation = "（设计预览）";
        StatusMessage = "设计预览";
    }

    public MainViewModel(
        IDictionaryRepository repository,
        IUserDataRepository userData,
        IStudyRepository study,
        string? userDataLocation = null,
        ISpeechService? speech = null,
        IGrammarChecker? grammar = null,
        IGrammarModelInstaller? grammarModel = null)
    {
        _repository = repository;
        _speech = speech;
        _userData = userData;
        _study = study;
        UserDataLocation = userDataLocation ?? "（未知位置）";
        Study = new StudyViewModel(study, speech, () => SpeechVoiceOption.ToVoice(SelectedSpeechVoice));
        Study.CardRevealed += (_, card) => _ = LoadWordAsync(card);
        Writing = grammar is null ? new WritingViewModel() : new WritingViewModel(grammar, grammarModel);
        // 写作页给出的每条建议都能一键在右侧查证——这是这个功能的立身之本：
        // 模型只负责指出「这里可能有问题」，道理由词典的例句来讲。
        Writing.LookupRequested += (_, word) => NavigateToWordCommand.Execute(word);
    }

    public static MainViewModel CreateUnavailable(string message) => new()
    {
        StatusMessage = message,
    };

    /// <summary>
    /// 外观变更。真正写进 Application.Resources 的动作在 App 层做，
    /// ViewModel 不去碰全局单例，测试里也就不会互相污染。
    /// </summary>
    public event EventHandler<AppearanceSettings>? AppearanceChanged;

    public StudyViewModel Study { get; }

    public WritingViewModel Writing { get; }

    /// <summary>用户库的完整路径，显示在设置页方便备份。</summary>
    public string UserDataLocation { get; }

    public IReadOnlyList<ThemeModeOption> ThemeModeOptions => ThemeModeOption.All;

    /// <summary>默认那个要一开始就带着勾：赋同一个值不会触发变更处理，勾不上。</summary>
    public IReadOnlyList<AccentChoice> AccentOptions { get; } =
    [
        .. Palette.Accents.Select(x => new AccentChoice(x) { IsSelected = x.Id == AppearanceTheme.DefaultAccent }),
    ];

    public IReadOnlyList<FontScaleOption> FontScaleOptions => FontScaleOption.All;

    public IReadOnlyList<string> FontFamilyOptions => FontFamilyOption.All;

    public IReadOnlyList<string> SpeechVoiceOptions =>
        [SpeechVoiceOption.System, .. _speech?.Voices ?? []];

    public bool IsSpeechAvailable => _speech?.IsAvailable == true;

    public string SpeechHint => IsSpeechAvailable
        ? "点词头旁的喇叭朗读。使用系统内置语音合成，不联网。"
        : "当前系统没有可用的语音合成引擎，朗读按钮已隐藏。";

    /// <summary>
    /// 详情区宽度达到这个值才分两列：两栏各留 500 上下再加中缝，
    /// 比这窄的话每栏会挤到不好读，还不如单栏留白。
    /// </summary>
    public const double TwoColumnThreshold = 1040;

    /// <summary>
    /// 正文限宽。单栏 720 是舒服的阅读行长；分两列后总宽放到 1180，
    /// 每栏约 576，仍在同一区间里。再宽就该留白而不是继续摊开。
    /// </summary>
    public double DetailMaxWidth => IsTwoColumnDetail ? 1180 : 720;

    public GridLength DetailGapWidth => IsTwoColumnDetail ? new GridLength(28) : new GridLength(0);

    public GridLength DetailAsideWidth =>
        IsTwoColumnDetail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    /// <summary>参考类内容：宽时放右栏，窄时回到左栏正文下方。</summary>
    public int DetailAsideColumn => IsTwoColumnDetail ? 2 : 0;

    public int DetailAsideRow => IsTwoColumnDetail ? 0 : 1;

    /// <summary>
    /// 当前列表、当前位置、列表名。
    ///
    /// 「上一个」和「后退」是两回事：顺着词书看到第 37 个、点例句里的词跳走之后，
    /// 后退该回到第 37 个，上一个该去第 36 个。所以两者用不同的轴——
    /// Alt+←/→ 走历史，Alt+↑/↓ 走列表位置。
    /// </summary>
    private (int Index, int Count, string Name) Browse => ActiveScope switch
    {
        BrowseScope.Search => (
            SelectedSearchResult is null ? -1 : SearchResults.IndexOf(SelectedSearchResult),
            SearchResults.Count,
            "搜索结果"),
        BrowseScope.Book => (
            SelectedBookWord is null ? -1 : BookWords.IndexOf(SelectedBookWord),
            BookWords.Count,
            SelectedBook?.Title ?? "词书"),
        BrowseScope.Favorites => (
            SelectedFavorite is null ? -1 : Favorites.IndexOf(SelectedFavorite),
            Favorites.Count,
            "收藏"),
        BrowseScope.History => (
            SelectedHistory is null ? -1 : History.IndexOf(SelectedHistory),
            History.Count,
            "历史"),
        _ => (-1, 0, string.Empty),
    };

    public bool CanBrowseList => Browse.Index >= 0 && Browse.Count > 1;

    /// <summary>逐个看词时最有用的其实是这一句——不然读到哪了毫无概念。</summary>
    public string BrowseLabel
    {
        get
        {
            var (index, count, name) = Browse;
            return index < 0 ? string.Empty : $"{name} · {index + 1} / {count}";
        }
    }

    public bool CanGoPreviousInList => Browse.Index > 0;

    public bool CanGoNextInList => Browse is { Index: >= 0 } b && b.Index < b.Count - 1;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public string FavoriteGlyph => IsFavorite ? "★ 已收藏" : "☆ 收藏";

    public string StudyGlyph => IsInStudy ? "✓ 学习中" : "+ 加入学习";

    public async Task InitializeAsync()
    {
        if (_repository is null)
        {
            return;
        }

        try
        {
            IsBusy = true;

            if (_userData is not null)
            {
                try
                {
                    await _userData.InitializeAsync();
                    await _study!.InitializeAsync();
                }
                catch (Exception exception)
                {
                    // 用户库不可用（磁盘权限等）时降级为纯查词，不影响主功能。
                    _userData = null;
                    _study = null;
                    _userDataFailure = $"用户数据不可用：{exception.Message}";
                }
            }

            var statsTask = _repository.GetStatsAsync();
            var booksTask = _repository.GetBooksAsync();
            var resultsTask = _repository.SearchAsync(string.Empty, 80);
            await Task.WhenAll(statsTask, booksTask, resultsTask);

            Books = new ObservableCollection<BookSummary>(await booksTask);
            SearchResults = new ObservableCollection<WordSummary>(await resultsTask);
            SelectedSearchResult = SearchResults.FirstOrDefault();

            await LoadAppearanceAsync();
            await RefreshFavoritesAsync();
            await RefreshHistoryAsync();

            // 用户库里只有 book_id，标题得从内容库带过去。
            Study.SetBookTitles(Books.ToDictionary(x => x.Id, x => x.Title));
            await Study.RefreshAsync();

            var stats = await statsTask;
            var summary = $"{stats.WordCount:N0} 个词头 · {stats.BookCount} 本词书 · {stats.MembershipCount:N0} 条词书记录";

            // 降级警告要留在状态栏里，否则用户不知道收藏和笔记为什么没反应。
            StatusMessage = _userDataFailure is null ? summary : $"{summary} · {_userDataFailure}";

            // 词头索引要读两万多行（实测约 110ms），但直到鼠标移到例句上才需要，
            // 不该挡在首屏前面。
            _ = WarmWordIndexAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = $"加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_userData is null || SelectedWord is null)
        {
            return;
        }

        try
        {
            IsFavorite = await _userData.ToggleFavoriteAsync(ToReference(SelectedWord));
            await RefreshFavoritesAsync();
            StatusMessage = IsFavorite ? $"已收藏 {SelectedWord.Headword}" : $"已取消收藏 {SelectedWord.Headword}";
        }
        catch (Exception exception)
        {
            StatusMessage = $"收藏失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (_userData is null || SelectedWord is null)
        {
            return;
        }

        try
        {
            await _userData.SaveNoteAsync(SelectedWord.Id, NoteText);
            IsNoteDirty = false;
            StatusMessage = string.IsNullOrWhiteSpace(NoteText)
                ? $"已删除 {SelectedWord.Headword} 的笔记"
                : $"已保存 {SelectedWord.Headword} 的笔记";
        }
        catch (Exception exception)
        {
            StatusMessage = $"笔记保存失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleStudyAsync()
    {
        if (_study is null || SelectedWord is null)
        {
            return;
        }

        try
        {
            if (IsInStudy)
            {
                await _study.RemoveCardAsync(SelectedWord.Id);
                IsInStudy = false;
                StatusMessage = $"已把 {SelectedWord.Headword} 移出学习计划";
            }
            else
            {
                await _study.AddCardsAsync([ToReference(SelectedWord)], null, DateTimeOffset.Now);
                IsInStudy = true;
                StatusMessage = $"已把 {SelectedWord.Headword} 加入学习计划";
            }

            await Study.RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = $"操作失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task AddBookToStudyAsync()
    {
        // 词表还没跟上选中的词书时不能加，否则会用当前 id 把上一本的词加进去。
        if (_study is null || SelectedBook is null || _loadedBookId != SelectedBook.Id || _allBookWords.Count == 0)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var added = await _study.AddCardsAsync(_allBookWords, SelectedBook.Id, DateTimeOffset.Now);
            await Study.RefreshAsync();
            StatusMessage = added == 0
                ? $"{SelectedBook.Title} 的单词都已在学习计划里"
                : $"已从 {SelectedBook.Title} 加入 {added:N0} 张新卡片";
        }
        catch (Exception exception)
        {
            StatusMessage = $"加入学习失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>点击同近义词/同根词跳转。词典里没有这个词头时退回搜索框。</summary>
    [RelayCommand]
    private async Task NavigateToWordAsync(string? headword)
    {
        if (_repository is null || string.IsNullOrWhiteSpace(headword))
        {
            return;
        }

        try
        {
            var found = await _repository.FindByHeadwordAsync(headword);
            if (found is null)
            {
                SearchText = headword;
                StatusMessage = $"词典里没有 {headword}，已放进搜索框";
                return;
            }

            await LoadWordAsync(found);
        }
        catch (Exception exception)
        {
            StatusMessage = $"跳转失败：{exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPreviousInList))]
    private void GoPreviousInList() => MoveInList(-1);

    [RelayCommand(CanExecute = nameof(CanGoNextInList))]
    private void GoNextInList() => MoveInList(1);

    private void MoveInList(int delta)
    {
        var (index, count, _) = Browse;
        var next = index + delta;
        if (index < 0 || next < 0 || next >= count)
        {
            return;
        }

        // 赋值会触发各自的 OnSelectedXChanged，词条加载走原来那条路。
        switch (ActiveScope)
        {
            case BrowseScope.Search: SelectedSearchResult = SearchResults[next]; break;
            case BrowseScope.Book: SelectedBookWord = BookWords[next]; break;
            case BrowseScope.Favorites: SelectedFavorite = Favorites[next]; break;
            case BrowseScope.History: SelectedHistory = History[next]; break;
        }
    }

    /// <summary>位置由「哪个列表 + 选中谁 + 列表本身」三者共同决定，任一变化都要重算。</summary>
    private void NotifyBrowseChanged()
    {
        OnPropertyChanged(nameof(CanBrowseList));
        OnPropertyChanged(nameof(BrowseLabel));
        OnPropertyChanged(nameof(CanGoPreviousInList));
        OnPropertyChanged(nameof(CanGoNextInList));
        GoPreviousInListCommand.NotifyCanExecuteChanged();
        GoNextInListCommand.NotifyCanExecuteChanged();
    }

    /// <summary>点开一条短语命中，跳到它所属的词条。</summary>
    [RelayCommand]
    private async Task OpenPhraseAsync(PhraseHit? phrase)
    {
        if (_repository is null || phrase is null)
        {
            return;
        }

        var detail = await _repository.GetWordAsync(phrase.WordId);
        if (detail is not null)
        {
            await LoadWordAsync(ToReference(detail));
        }
    }

    [RelayCommand]
    private async Task RemoveFavoriteAsync(FavoriteEntry? entry)
    {
        if (_userData is null || entry is null)
        {
            return;
        }

        try
        {
            await _userData.RemoveFavoriteAsync(entry.WordId);
            await RefreshFavoritesAsync();

            // 删的正好是当前打开的词条时，详情页的收藏按钮要跟着变回未收藏。
            if (SelectedWord?.Id == entry.WordId)
            {
                IsFavorite = false;
            }

            StatusMessage = $"已取消收藏 {entry.Headword}";
        }
        catch (Exception exception)
        {
            StatusMessage = $"取消收藏失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveHistoryAsync(HistoryEntry? entry)
    {
        if (_userData is null || entry is null)
        {
            return;
        }

        try
        {
            await _userData.RemoveHistoryAsync(entry.WordId);
            await RefreshHistoryAsync();
            StatusMessage = $"已从历史中移除 {entry.Headword}";
        }
        catch (Exception exception)
        {
            StatusMessage = $"移除历史失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (_userData is null)
        {
            return;
        }

        try
        {
            await _userData.ClearHistoryAsync();
            await RefreshHistoryAsync();
            StatusMessage = "已清空查询历史";
        }
        catch (Exception exception)
        {
            StatusMessage = $"清空历史失败：{exception.Message}";
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_repository is null)
        {
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = SearchAfterDelayAsync(value, _searchCancellation.Token);
    }

    partial void OnNoteTextChanged(string value)
    {
        if (!_suppressNoteTracking)
        {
            IsNoteDirty = true;
        }
    }

    partial void OnSelectedSearchResultChanged(WordSummary? value)
    {
        NotifyBrowseChanged();

        if (value is not null)
        {
            _ = LoadWordAsync(value);
        }
    }

    /// <summary>这本词书上次看到哪个词。没看过、或书签就是当前这个词时为空。</summary>
    [ObservableProperty]
    private string _bookmarkHint = string.Empty;

    private BookProgress? _bookmark;

    partial void OnSelectedBookWordChanged(WordSummary? value)
    {
        NotifyBrowseChanged();

        if (value is null)
        {
            return;
        }

        _ = LoadWordAsync(value);

        // 翻到哪儿就把书签夹到哪儿——像合上纸质词典时顺手夹一下，
        // 不需要用户专门去点一个「保存进度」。
        //
        // 但过滤状态下不动书签：那时是在查某个词，不是在顺着读。
        // 纸质词典翻去别处查一下，夹着的书签也不会跟着跑。
        if (!_restoringBookmark && BookFilter.Length == 0 && _userData is not null && SelectedBook is not null)
        {
            _bookmark = new BookProgress(SelectedBook.Id, value.Id, value.Headword, DateTimeOffset.Now);
            _ = SaveBookmarkAsync(SelectedBook.Id, value);
        }

        UpdateBookmarkHint();
    }

    private async Task SaveBookmarkAsync(string bookId, WordSummary word)
    {
        try
        {
            await _userData!.SaveBookProgressAsync(bookId, word);
        }
        catch (Exception exception)
        {
            StatusMessage = $"书签保存失败：{exception.Message}";
        }
    }

    /// <summary>书签和当前看的不是同一个词时，才值得提示一句。</summary>
    private void UpdateBookmarkHint() =>
        BookmarkHint = _bookmark is not null && _bookmark.WordId != SelectedBookWord?.Id
            ? $"书签在 {_bookmark.Headword}"
            : string.Empty;

    /// <summary>跳回书签。词被过滤掉了就先清掉过滤，否则点了没反应。</summary>
    [RelayCommand]
    private void GoToBookmark()
    {
        if (_bookmark is null)
        {
            return;
        }

        if (BookWords.All(x => x.Id != _bookmark.WordId) && BookFilter.Length > 0)
        {
            BookFilter = string.Empty;
        }

        var target = BookWords.FirstOrDefault(x => x.Id == _bookmark.WordId);
        if (target is not null)
        {
            SelectedBookWord = target;
        }
    }

    partial void OnSelectedFavoriteChanged(FavoriteEntry? value)
    {
        NotifyBrowseChanged();

        if (value is not null && !_suppressListSelection)
        {
            _ = LoadWordAsync(value);
        }
    }

    partial void OnSelectedHistoryChanged(HistoryEntry? value)
    {
        NotifyBrowseChanged();

        if (value is not null && !_suppressListSelection)
        {
            // 从历史列表点进来的不刷新历史，否则列表会在指针下重排。
            _ = LoadWordAsync(value, refreshHistory: false);
        }
    }

    /// <summary>点设置页上的色点。做成命令而不是双向绑定，是因为要一眼看出哪个选中了。</summary>
    [RelayCommand]
    private void PickAccent(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            SelectedAccent = Palette.Resolve(id).Id;
        }
    }

    partial void OnActiveScopeChanged(BrowseScope value) => NotifyBrowseChanged();

    partial void OnSearchResultsChanged(ObservableCollection<WordSummary> value) => NotifyBrowseChanged();

    partial void OnBookWordsChanged(ObservableCollection<WordSummary> value) => NotifyBrowseChanged();

    partial void OnFavoritesChanged(ObservableCollection<FavoriteEntry> value) => NotifyBrowseChanged();

    partial void OnHistoryChanged(ObservableCollection<HistoryEntry> value) => NotifyBrowseChanged();

    partial void OnSelectedBookChanged(BookSummary? value)
    {
        if (value is not null)
        {
            _ = LoadBookAsync(value);
        }
    }

    private async Task SearchAfterDelayAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(140, cancellationToken);
            var wordsTask = _repository!.SearchAsync(query, 80, cancellationToken);
            var phrasesTask = _repository.SearchPhrasesAsync(query, 6, cancellationToken);
            await Task.WhenAll(wordsTask, phrasesTask);
            cancellationToken.ThrowIfCancellationRequested();

            var results = await wordsTask;
            PhraseResults = new ObservableCollection<PhraseHit>(await phrasesTask);
            SearchResults = new ObservableCollection<WordSummary>(results);
            SelectedSearchResult = SearchResults.FirstOrDefault();
            StatusMessage = string.IsNullOrWhiteSpace(query)
                ? "输入单词或中文释义开始查询"
                : $"找到 {results.Count} 条结果";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"查询失败：{exception.Message}";
        }
    }

    private async Task LoadBookAsync(BookSummary book)
    {
        if (_repository is null)
        {
            return;
        }

        var generation = ++_bookGeneration;
        try
        {
            IsBusy = true;
            var words = await _repository.GetBookWordsAsync(book.Id, 20_000);

            // 先点 A 再点 B 时 A 可能后返回，不拦住的话会用 A 的词表覆盖 B，
            // 「整本加入学习」就会拿着 B 的 id 加进 A 的单词。
            if (generation != _bookGeneration)
            {
                return;
            }

            _allBookWords = words;
            _loadedBookId = book.Id;
            ApplyBookFilter();

            // 打开一本书时翻回上次看到的那个词。找不到就从头开始——
            // 重新生成过词典库的话 word_id 会变，书签指向的词可能已经不在了。
            var bookmark = _userData is null ? null : await _userData.GetBookProgressAsync(book.Id);

            // 取书签也要再拦一次。用户库挪到线程池之后这里才是真的挂起点：
            // 不拦住的话，A 的书签会写进 _bookmark，而 BookWords 已经是 B 的了——
            // 于是浏览 B 却被 A 的书签把选中的词拽走，状态栏也报 A 的书名。
            if (generation != _bookGeneration)
            {
                return;
            }

            _bookmark = bookmark;
            var resume = _bookmark is null
                ? null
                : BookWords.FirstOrDefault(x => x.Id == _bookmark.WordId);

            _restoringBookmark = true;
            SelectedBookWord = resume ?? BookWords.FirstOrDefault();
            _restoringBookmark = false;

            // 头一回打开这本书：书签就夹在第一个词上。
            // 不补这一下，「书签」要等用户主动翻过一次才存在，
            // 于是刚打开就去查别的词时没有任何东西可以跳回来。
            if (_bookmark is null && SelectedBookWord is { } landed)
            {
                _bookmark = new BookProgress(book.Id, landed.Id, landed.Headword, DateTimeOffset.Now);
                if (_userData is not null)
                {
                    await _userData.SaveBookProgressAsync(book.Id, landed);
                    if (generation != _bookGeneration)
                    {
                        return;
                    }
                }
            }

            UpdateBookmarkHint();

            StatusMessage = resume is null
                ? $"{book.Title} · {_allBookWords.Count:N0} 个单词"
                : $"{book.Title} · 上次看到 {resume.Headword}（第 {BookWords.IndexOf(resume) + 1:N0} 个）";
        }
        catch (Exception exception)
        {
            StatusMessage = $"词书加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadWordAsync(
        IWordReference reference,
        bool refreshHistory = true,
        bool recordHistory = true)
    {
        if (_repository is null)
        {
            return;
        }

        // 前进/后退自己维护栈，普通跳转才记一笔。原地重选同一条不入栈。
        if (recordHistory && SelectedWord is { } current &&
            (current.Id != reference.WordId || current.EntryId != reference.EntryId))
        {
            _back.Push(ToReference(current));
            _forward.Clear();
            RefreshNavigationState();
        }

        var generation = ++_loadGeneration;
        await FlushPendingNoteAsync();

        try
        {
            var detail = await _repository.GetWordAsync(reference.WordId, reference.EntryId);
            if (generation != _loadGeneration)
            {
                return;
            }

            SelectedWord = detail;
            if (detail is null)
            {
                AlternateEntries = [];
                return;
            }

            var alternates = await _repository.GetAlternateEntriesAsync(detail.Id, detail.EntryId);
            if (generation != _loadGeneration)
            {
                return;
            }

            AlternateEntries = new ObservableCollection<AlternateEntry>(alternates);
            if (_userData is null)
            {
                return;
            }

            await _userData.RecordViewAsync(reference);
            var favorite = await _userData.IsFavoriteAsync(detail.Id);
            var inStudy = _study is not null && await _study.IsInStudyAsync(detail.Id);
            var note = await _userData.GetNoteAsync(detail.Id);

            // 上面几次 await 期间用户可能已经点开了别的词，这时不能把状态写回去。
            if (generation != _loadGeneration)
            {
                return;
            }

            IsFavorite = favorite;
            IsInStudy = inStudy;

            // 上面几次查询期间用户可能已经在给这个新词写笔记了（详情页早就切过来了），
            // 这时候不能拿库里的旧内容盖掉他刚打的字。
            if (!IsNoteDirty)
            {
                SetNoteWithoutTracking(note);
            }

            if (refreshHistory)
            {
                await RefreshHistoryAsync();
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"词条加载失败：{exception.Message}";
        }
    }

    /// <summary>
    /// 切换词条前把没保存的笔记落盘。否则输入到一半点了别的词，内容就无声丢了。
    /// 写失败时把草稿留在 <see cref="_unsavedNote"/> 里，下次再试，不清 dirty 也不丢内容。
    /// </summary>
    private async Task FlushPendingNoteAsync()
    {
        if (_userData is null)
        {
            return;
        }

        // 先补写上一次失败的草稿。
        if (_unsavedNote is { } pending && await TrySaveNoteAsync(pending.WordId, pending.Text))
        {
            _unsavedNote = null;
        }

        if (!IsNoteDirty || SelectedWord is null)
        {
            return;
        }

        var draft = (SelectedWord.Id, NoteText);
        var headword = SelectedWord.Headword;
        if (await TrySaveNoteAsync(draft.Id, draft.NoteText))
        {
            IsNoteDirty = false;
            StatusMessage = $"已自动保存 {headword} 的笔记";
        }
        else
        {
            _unsavedNote = draft;
        }
    }

    private async Task<bool> TrySaveNoteAsync(long wordId, string text)
    {
        try
        {
            await _userData!.SaveNoteAsync(wordId, text);
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"笔记暂时保存失败（已留存，稍后自动重试）：{exception.Message}";
            return false;
        }
    }

    partial void OnBookFilterChanged(string value) => ApplyBookFilter();

    /// <summary>
    /// 词书最多两万词，靠滚动找词不现实。整本已经在内存里，直接本地过滤即可，
    /// 词头和释义都参与匹配。
    /// </summary>
    private void ApplyBookFilter()
    {
        var keyword = BookFilter.Trim();
        var filtered = keyword.Length == 0
            ? _allBookWords
            : [.. _allBookWords.Where(x =>
                x.Headword.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.Translation.Contains(keyword, StringComparison.OrdinalIgnoreCase))];

        BookWords = new ObservableCollection<WordSummary>(filtered);
    }

    /// <summary>切换到同一词头的另一条释义。</summary>
    [RelayCommand]
    private async Task ShowAlternateAsync(AlternateEntry? entry)
    {
        if (entry is null || SelectedWord is null)
        {
            return;
        }

        await LoadWordAsync(new WordReference(
            SelectedWord.Id,
            entry.EntryId,
            SelectedWord.Headword,
            SelectedWord.UsPhone.Length > 0 ? SelectedWord.UsPhone : SelectedWord.UkPhone,
            entry.Translation));
    }

    partial void OnSelectedThemeModeChanged(ThemeModeOption value) => SaveAppearance();

    partial void OnSelectedAccentChanged(string value)
    {
        foreach (var choice in AccentOptions)
        {
            choice.IsSelected = choice.Id == value;
        }

        SaveAppearance();
    }

    partial void OnSelectedFontScaleChanged(FontScaleOption value) => SaveAppearance();

    partial void OnSelectedFontFamilyChanged(string value) => SaveAppearance();

    partial void OnSelectedSpeechVoiceChanged(string value) => SaveAppearance();

    partial void OnLeftPaneWidthChanged(double value) => SaveAppearance();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var target = _back.Pop();
        if (SelectedWord is not null)
        {
            _forward.Push(ToReference(SelectedWord));
        }

        await LoadWordAsync(target, recordHistory: false);
        RefreshNavigationState();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        var target = _forward.Pop();
        if (SelectedWord is not null)
        {
            _back.Push(ToReference(SelectedWord));
        }

        await LoadWordAsync(target, recordHistory: false);
        RefreshNavigationState();
    }

    private void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>打开设置。真正开窗的动作由外层接管，ViewModel 不引用 Window。</summary>
    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>打开音标表。同样由外层开窗。</summary>
    public event EventHandler? PhoneticsRequested;

    [RelayCommand]
    private void OpenPhonetics() => PhoneticsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>音标窗口的 ViewModel。嗓音跟着设置走，所以传的是取值的委托而不是当前值。</summary>
    public PhoneticsViewModel CreatePhonetics() =>
        new(_speech, () => SpeechVoiceOption.ToVoice(SelectedSpeechVoice));

    /// <summary>朗读当前词头。源数据没有音频，走系统 TTS。</summary>
    [RelayCommand]
    private void Speak()
    {
        if (_speech is not null && SelectedWord is not null)
        {
            _speech.Speak(SelectedWord.Headword, SpeechVoiceOption.ToVoice(SelectedSpeechVoice));
        }
    }

    /// <summary>音标拆成一格格，认得的音素可点。</summary>
    public IReadOnlyList<PhonemeChip> UkPhonemes => PhonemeChip.From(SelectedWord?.UkPhone);

    public IReadOnlyList<PhonemeChip> UsPhonemes => PhonemeChip.From(SelectedWord?.UsPhone);

    /// <summary>
    /// 点音标里的一个音，朗读它的例词。
    /// </summary>
    /// <remarks>
    /// 读的是例词而不是这个音本身：TTS 念不了孤立的音素，
    /// 而且把音放在熟词里本来就比单念更容易记住。
    /// </remarks>
    [RelayCommand]
    private void SpeakPhoneme(PhonemeChip? chip)
    {
        if (_speech is not null && chip?.Phoneme is { } phoneme)
        {
            _speech.Speak(phoneme.Example, SpeechVoiceOption.ToVoice(SelectedSpeechVoice));
        }
    }

    private async Task WarmWordIndexAsync()
    {
        try
        {
            WordIndex = await _repository!.GetWordIndexAsync();
        }
        catch (Exception)
        {
            // 索引只影响例句的悬停高亮和预览，读不到就退化成普通文本，不打扰用户。
        }
    }

    private async Task LoadAppearanceAsync()
    {
        if (_userData is null)
        {
            return;
        }

        var settings = await _userData.GetAppearanceAsync();
        _suppressAppearanceSave = true;
        SelectedThemeMode = ThemeModeOption.Resolve(settings.ThemeMode);
        SelectedAccent = Palette.Resolve(settings.Accent).Id;
        SelectedFontScale = FontScaleOption.Nearest(settings.FontScale);
        SelectedFontFamily = FontFamilyOption.ToLabel(settings.FontFamily);
        SelectedSpeechVoice = SpeechVoiceOption.ToLabel(settings.SpeechVoice, SpeechVoiceOptions);
        LeftPaneWidth = settings.LeftPaneWidth;
        _suppressAppearanceSave = false;

        AppearanceChanged?.Invoke(this, settings);
    }

    private void SaveAppearance()
    {
        if (_userData is null || _suppressAppearanceSave)
        {
            return;
        }

        var settings = new AppearanceSettings(
            SelectedFontScale.Scale,
            FontFamilyOption.ToFamily(SelectedFontFamily),
            SpeechVoiceOption.ToVoice(SelectedSpeechVoice),
            LeftPaneWidth,
            SelectedThemeMode.Id,
            SelectedAccent);

        // 先应用再落盘：界面立刻响应，保存失败也只影响下次启动。
        AppearanceChanged?.Invoke(this, settings);
        _ = PersistAppearanceAsync(settings);
    }

    private async Task PersistAppearanceAsync(AppearanceSettings settings)
    {
        try
        {
            await _userData!.SaveAppearanceAsync(settings);
        }
        catch (Exception exception)
        {
            StatusMessage = $"外观设置保存失败：{exception.Message}";
        }
    }

    private async Task RefreshFavoritesAsync()
    {
        if (_userData is null)
        {
            return;
        }

        var items = await _userData.GetFavoritesAsync();
        var selected = SelectedFavorite?.WordId;
        _suppressListSelection = true;
        try
        {
            Favorites = new ObservableCollection<FavoriteEntry>(items);
            SelectedFavorite = Favorites.FirstOrDefault(x => x.WordId == selected);
        }
        finally
        {
            _suppressListSelection = false;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_userData is null)
        {
            return;
        }

        var items = await _userData.GetHistoryAsync();
        var selected = SelectedHistory?.WordId;
        _suppressListSelection = true;
        try
        {
            History = new ObservableCollection<HistoryEntry>(items);
            SelectedHistory = History.FirstOrDefault(x => x.WordId == selected);
        }
        finally
        {
            _suppressListSelection = false;
        }
    }

    private void SetNoteWithoutTracking(string value)
    {
        _suppressNoteTracking = true;
        NoteText = value;
        _suppressNoteTracking = false;
        IsNoteDirty = false;
    }

    private static WordReference ToReference(WordDetail detail) => new WordReference(
        detail.Id,
        detail.EntryId,
        detail.Headword,
        detail.UsPhone.Length > 0 ? detail.UsPhone : detail.UkPhone,
        detail.Translations.FirstOrDefault() ?? string.Empty);
}
