using LingoDict.Core.Models;
using LingoDict.Core.Services;
using LingoDict.Core.Study;

namespace LingoDict.App.Tests;

internal sealed class FakeDictionaryRepository : IDictionaryRepository
{
    public List<WordSummary> Results { get; } =
    [
        new(1, 10, "cancel", "'kænsl", "v 取消，撤销", 3) { Collocations = ["cancel the order", "cancel out"] },
        new(2, 20, "cancellation", "kænsə'leɪʃn", "n 取消；删除", 2),
    ];

    public WordDetail? Detail { get; set; } = new(
        1, 10, "cancel", "'kænsl", "'kænsl",
        ["v 取消，撤销"],
        [
            new ExampleSentence("Please cancel the order.", "请取消这个订单。", "cancel", ""),
            // 强调词存的是词头，句子里往往是屈折形式，加粗必须照样命中。
            new ExampleSentence("Our flight was cancelled.", "我们的航班取消了。", "cancel", ""),
        ],
        ExamSentences: [new ExampleSentence("...quitting abruptly...", "", "", "CET4 2017.6 阅读理解")],
        Phrases: [],
        Synonyms: [new SynonymGroup("v 取消", ["revoke", "call off"])],
        RelatedWords: [new WordLink("cancellation", "n 取消")],
        SourceBooks: ["四级词汇"]);

    public string? LastFindByHeadword { get; private set; }

    public List<AlternateEntry> Alternates { get; } = [];

    public Task<IReadOnlyList<AlternateEntry>> GetAlternateEntriesAsync(
        long wordId, long excludeEntryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AlternateEntry>>(
            [.. Alternates.Where(x => x.EntryId != excludeEntryId)]);

    public Task<IReadOnlyDictionary<string, WordPreview>> GetWordIndexAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, WordPreview>>(Results.ToDictionary(
            x => LingoDict.Core.Text.WordNormalizer.Normalize(x.Headword),
            x => new WordPreview(x.Headword, x.Phonetic, x.Translation)));

    public List<PhraseHit> Phrases { get; } =
    [
        new(1, "cancel", "cancel out", "抵消"),
        new(1, "cancel", "cancel an order", "取消订单"),
    ];

    public Task<IReadOnlyList<PhraseHit>> SearchPhrasesAsync(
        string query, int limit = 8, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PhraseHit>>(
            [.. Phrases.Where(x => x.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || x.Translation.Contains(query, StringComparison.Ordinal)).Take(limit)]);

    public Task<DictionaryStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DictionaryStats(2, 1, 5));

    public Task<IReadOnlyList<WordSummary>> SearchAsync(string query, int limit = 80, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WordSummary>>(Results);

    /// <summary>词书列表。默认一本；需要测切换的用例自己往里加。</summary>
    public List<BookSummary> Books { get; } = [new BookSummary("CET4_1", "四级词汇", "测试", 2)];

    public Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BookSummary>>(Books);

    public Task<IReadOnlyList<WordSummary>> GetBookWordsAsync(string bookId, int limit = 500, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WordSummary>>(Results);

    public Task<WordDetail?> GetWordAsync(long wordId, long? entryId = null, CancellationToken cancellationToken = default)
    {
        if (Detail is not null && wordId == Detail.Id)
        {
            // 如实反映请求的 entry，否则"切换到另一条释义"这类断言测不出东西。
            return Task.FromResult<WordDetail?>(
                entryId is null ? Detail : Detail with { EntryId = entryId.Value });
        }

        // 其余词头给一份最小详情，这样"按 wordId 取到不同词条"的行为是真实的。
        var summary = Results.FirstOrDefault(x => x.Id == wordId);
        return Task.FromResult(summary is null
            ? null
            : new WordDetail(
                summary.Id, summary.EntryId, summary.Headword, summary.Phonetic, summary.Phonetic,
                [summary.Translation], [], [], [], [], [], []));
    }

    public Task<WordSummary?> FindByHeadwordAsync(string headword, CancellationToken cancellationToken = default)
    {
        LastFindByHeadword = headword;
        return Task.FromResult<WordSummary?>(Results.FirstOrDefault(x => x.Headword == headword));
    }
}

/// <summary>第一次 GetWordAsync 挂起，直到显式放行，用来制造前后两次加载交错的时序。</summary>
internal sealed class SlowFirstCallDictionaryRepository : IDictionaryRepository
{
    private readonly FakeDictionaryRepository _inner = new();
    private readonly TaskCompletionSource _gate = new();
    private bool _slowCallPending = true;

    public void ReleaseSlowCall() => _gate.TrySetResult();

    public async Task<WordDetail?> GetWordAsync(long wordId, long? entryId = null, CancellationToken cancellationToken = default)
    {
        if (_slowCallPending)
        {
            _slowCallPending = false;
            await _gate.Task;
            return new WordDetail(wordId, entryId ?? 0, "cancel", "", "", ["v 取消"], [], [], [], [], [], []);
        }

        return new WordDetail(wordId, entryId ?? 0, "cancellation", "", "", ["n 取消"], [], [], [], [], [], []);
    }

    public Task<DictionaryStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetStatsAsync(cancellationToken);

    public Task<IReadOnlyList<WordSummary>> SearchAsync(string query, int limit = 80, CancellationToken cancellationToken = default) =>
        _inner.SearchAsync(query, limit, cancellationToken);

    public Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default) =>
        _inner.GetBooksAsync(cancellationToken);

    public Task<IReadOnlyList<WordSummary>> GetBookWordsAsync(string bookId, int limit = 500, CancellationToken cancellationToken = default) =>
        _inner.GetBookWordsAsync(bookId, limit, cancellationToken);

    public Task<WordSummary?> FindByHeadwordAsync(string headword, CancellationToken cancellationToken = default) =>
        _inner.FindByHeadwordAsync(headword, cancellationToken);

    public Task<IReadOnlyList<AlternateEntry>> GetAlternateEntriesAsync(
        long wordId, long excludeEntryId, CancellationToken cancellationToken = default) =>
        _inner.GetAlternateEntriesAsync(wordId, excludeEntryId, cancellationToken);

    public Task<IReadOnlyDictionary<string, WordPreview>> GetWordIndexAsync(CancellationToken cancellationToken = default) =>
        _inner.GetWordIndexAsync(cancellationToken);

    public Task<IReadOnlyList<PhraseHit>> SearchPhrasesAsync(
        string query, int limit = 8, CancellationToken cancellationToken = default) =>
        _inner.SearchPhrasesAsync(query, limit, cancellationToken);
}

internal sealed class FakeUserDataRepository : IUserDataRepository
{
    private readonly Dictionary<long, FavoriteEntry> _favorites = [];
    private readonly Dictionary<long, string> _notes = [];
    private readonly List<HistoryEntry> _history = [];

    /// <summary>词书书签：书 id → 上次看到哪个词。</summary>
    public Dictionary<string, BookProgress> BookProgress { get; } = [];

    /// <summary>模拟磁盘不可写：应用应降级为纯查词而不是崩掉。</summary>
    public bool FailOnInitialize { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => FailOnInitialize
        ? Task.FromException(new IOException("磁盘不可写"))
        : Task.CompletedTask;

    /// <summary>
    /// 卡住某本书的书签查询。用户库挪到线程池之后，这一步是真正的挂起点——
    /// 用它把「A 的书签回来时用户已经切到 B」那个时序摆出来。
    /// </summary>
    public Dictionary<string, TaskCompletionSource> BookProgressGates { get; } = [];

    public async Task<BookProgress?> GetBookProgressAsync(string bookId, CancellationToken cancellationToken = default)
    {
        if (BookProgressGates.TryGetValue(bookId, out var gate))
        {
            await gate.Task;
        }

        return BookProgress.GetValueOrDefault(bookId);
    }

    public Task SaveBookProgressAsync(string bookId, IWordReference word, CancellationToken cancellationToken = default)
    {
        BookProgress[bookId] = new BookProgress(bookId, word.WordId, word.Headword, DateTimeOffset.Now);
        return Task.CompletedTask;
    }

    public Task<UserDataStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UserDataStats(_favorites.Count, _history.Count, _notes.Count));

    public Task<bool> IsFavoriteAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_favorites.ContainsKey(wordId));

    public Task<bool> ToggleFavoriteAsync(IWordReference word, CancellationToken cancellationToken = default)
    {
        if (_favorites.Remove(word.WordId))
        {
            return Task.FromResult(false);
        }

        _favorites[word.WordId] = new FavoriteEntry(
            word.WordId, word.EntryId, word.Headword, word.Phonetic, word.Translation, DateTimeOffset.UtcNow);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(int limit = 500, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FavoriteEntry>>([.. _favorites.Values.OrderByDescending(x => x.CreatedAt)]);

    public Task RecordViewAsync(IWordReference word, CancellationToken cancellationToken = default)
    {
        _history.RemoveAll(x => x.WordId == word.WordId);
        _history.Insert(0, new HistoryEntry(
            word.WordId, word.EntryId, word.Headword, word.Phonetic, word.Translation, DateTimeOffset.UtcNow, 1));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HistoryEntry>>(_history);

    public Task<bool> RemoveFavoriteAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_favorites.Remove(wordId));

    public Task<bool> RemoveHistoryAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_history.RemoveAll(x => x.WordId == wordId) > 0);

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        _history.Clear();
        return Task.CompletedTask;
    }

    public AppearanceSettings Appearance { get; set; } = AppearanceSettings.Default;

    public Task<AppearanceSettings> GetAppearanceAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Appearance);

    public Task SaveAppearanceAsync(AppearanceSettings settings, CancellationToken cancellationToken = default)
    {
        Appearance = settings.Clamp();
        return Task.CompletedTask;
    }

    public Task<string> GetNoteAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_notes.TryGetValue(wordId, out var note) ? note : string.Empty);

    /// <summary>模拟磁盘满/库被占用：写笔记失败。</summary>
    public bool FailNoteWrites { get; set; }

    public Task SaveNoteAsync(long wordId, string text, CancellationToken cancellationToken = default)
    {
        if (FailNoteWrites)
        {
            return Task.FromException(new IOException("磁盘已满"));
        }

        _notes[wordId] = text;
        return Task.CompletedTask;
    }
}

internal sealed class FakeStudyRepository : IStudyRepository
{
    private readonly Dictionary<long, StudyCard> _cards = [];

    public int ReviewCount { get; private set; }

    /// <summary>记录界面层传进来的时间，用于验证每日额度按本地日而非 UTC 切分。</summary>
    public List<DateTimeOffset> ReceivedTimes { get; } = [];

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsInStudyAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cards.ContainsKey(wordId));

    public Task<StudyCard?> GetCardAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cards.GetValueOrDefault(wordId));

    public Task<int> AddCardsAsync(
        IEnumerable<IWordReference> words,
        string? bookId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var added = 0;
        foreach (var word in words)
        {
            if (_cards.TryAdd(word.WordId, SpacedRepetitionScheduler.CreateCard(word, bookId, now)))
            {
                added++;
            }
        }

        return Task.FromResult(added);
    }

    public Task<bool> RemoveCardAsync(long wordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cards.Remove(wordId));

    public StudySettings Settings { get; private set; } = StudySettings.Default;

    public Task<IReadOnlyList<StudyCard>> GetDueCardsAsync(DateTimeOffset now, int limit = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StudyCard>>([.. _cards.Values.Where(x => x.DueAt <= now).OrderBy(x => x.DueAt)]);

    public Task<IReadOnlyList<StudyCard>> BuildDailyQueueAsync(
        DateTimeOffset now, string? bookId = null, CancellationToken cancellationToken = default)
    {
        ReceivedTimes.Add(now);
        var scope = _cards.Values.Where(x => bookId is null || x.BookId == bookId).ToList();
        var reviews = scope
            .Where(x => !x.IsNew && x.DueAt <= now)
            .OrderBy(x => x.DueAt)
            .Take(Settings.ReviewPerDay);
        var fresh = scope
            .Where(x => x.IsNew)
            .OrderBy(x => x.WordId)
            .Take(Settings.NewPerDay);
        return Task.FromResult<IReadOnlyList<StudyCard>>([.. reviews, .. fresh]);
    }

    public Task<IReadOnlyList<StudyBook>> GetStudyBooksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StudyBook>>(
        [
            .. _cards.Values
                .Where(x => x.BookId is not null)
                .GroupBy(x => x.BookId!)
                .Select(g => new StudyBook(g.Key, g.Key, g.Count()))
                .OrderByDescending(x => x.CardCount),
        ]);

    public Task<IReadOnlyList<StudyDay>> GetReviewHistoryAsync(
        DateTimeOffset now, int days = 30, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StudyDay>>(
        [
            .. Enumerable.Range(0, days).Select(i =>
                new StudyDay(DateOnly.FromDateTime(now.AddDays(-(days - 1 - i)).Date), 0, 0, 0, 0)),
        ]);

    public Task<StudySettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task SaveSettingsAsync(StudySettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings.Clamp();
        return Task.CompletedTask;
    }

    public Task<StudyCard> ReviewAsync(StudyCard card, ReviewGrade grade, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ReviewCount++;
        var updated = SpacedRepetitionScheduler.Apply(card, grade, now);
        _cards[card.WordId] = updated;
        return Task.FromResult(updated);
    }

    public Task UndoReviewAsync(StudyCard previous, CancellationToken cancellationToken = default)
    {
        UndoCount++;
        _cards[previous.WordId] = previous;
        return Task.CompletedTask;
    }

    public int UndoCount { get; private set; }

    public Task<StudyStats> GetStudyStatsAsync(
        DateTimeOffset now, string? bookId = null, CancellationToken cancellationToken = default)
    {
        ReceivedTimes.Add(now);
        var scope = _cards.Values.Where(x => bookId is null || x.BookId == bookId).ToList();
        return Task.FromResult(new StudyStats(
            scope.Count,
            scope.Count(x => x.DueAt <= now),
            scope.Count(x => x.IsNew),
            ReviewCount,
            BuildDailyQueueAsync(now, bookId, cancellationToken).Result.Count));
    }
}

internal sealed class FakeSpeechService(IReadOnlyList<string> voices) : LingoDict.App.Services.ISpeechService
{
    public bool IsAvailable => Voices.Count > 0;

    public IReadOnlyList<string> Voices { get; } = voices;

    public (string Text, string? Voice)? LastSpoken { get; private set; }

    public void Speak(string text, string? voice) => LastSpoken = (text, voice);
}
