using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;
using LingoDict.Core.Services;
using LingoDict.Core.Study;

namespace LingoDict.App.Tests;

/// <summary>
/// 快速切换复习范围。
/// </summary>
/// <remarks>
/// 换词书是 fire-and-forget 的一次异步加载。没有代号保护时，从 A 快切到 B 会出现
/// 三种错法：队列来自 A 而统计来自 B；慢一步返回的 A 把 B 的卡片盖掉；
/// 于是用户在复习根本不在当前范围里的词。
///
/// 真实的 SQLite 学习库现在跑在线程池上，这些 await 是真正的挂起点，
/// 所以这里用一个能卡住的假仓库把那个时序精确摆出来。
/// </remarks>
public sealed class StudyScopeRaceTests
{
    /// <summary>能把指定词书的队列请求卡住不返回的假仓库。</summary>
    private sealed class GatedStudyRepository : IStudyRepository
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();

        public List<string?> QueueRequests { get; } = [];

        /// <summary>让这本书的 BuildDailyQueueAsync 挂起，直到 <see cref="Release"/>。</summary>
        public TaskCompletionSource Gate(string bookId) =>
            _gates.GetOrAdd(bookId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Release(string bookId) => Gate(bookId).TrySetResult();

        private static StudyCard Card(string bookId) =>
            SpacedRepetitionScheduler.CreateCard(
                new WordReference(bookId.GetHashCode(), 1, bookId + "-word", "'w", "n 词"),
                bookId,
                DateTimeOffset.Now.AddMinutes(-1));

        public async Task<IReadOnlyList<StudyCard>> BuildDailyQueueAsync(
            DateTimeOffset now, string? bookId = null, CancellationToken cancellationToken = default)
        {
            QueueRequests.Add(bookId);
            if (bookId is not null && _gates.TryGetValue(bookId, out var gate))
            {
                await gate.Task;
            }

            return bookId is null ? [] : [Card(bookId)];
        }

        public async Task<StudyStats> GetStudyStatsAsync(
            DateTimeOffset now, string? bookId = null, CancellationToken cancellationToken = default)
        {
            if (bookId is not null && _gates.TryGetValue(bookId, out var gate))
            {
                await gate.Task;
            }

            // 用书名的长度当计数，好在断言里认出统计来自哪一本
            return new StudyStats(bookId?.Length ?? 0, 0, 0, 0, bookId?.Length ?? 0);
        }

        public Task<IReadOnlyList<StudyBook>> GetStudyBooksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudyBook>>(
                [new StudyBook("AAAA", "AAAA", 1), new StudyBook("BB", "BB", 1)]);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsInStudyAsync(long wordId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<StudyCard?> GetCardAsync(long wordId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StudyCard?>(null);

        public Task<int> AddCardsAsync(
            IEnumerable<IWordReference> words, string? bookId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<bool> RemoveCardAsync(long wordId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<StudyCard>> GetDueCardsAsync(
            DateTimeOffset now, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudyCard>>([]);

        public Task<StudyCard> ReviewAsync(
            StudyCard card, ReviewGrade grade, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(card);

        public Task UndoReviewAsync(StudyCard previous, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StudyDay>> GetReviewHistoryAsync(
            DateTimeOffset now, int days = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudyDay>>([]);

        public Task<StudySettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StudySettings.Default);

        public Task SaveSettingsAsync(StudySettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [AvaloniaFact]
    public void TheSlowerBookCannotOverwriteTheOneTheUserSwitchedTo()
    {
        var repository = new GatedStudyRepository();
        var study = new StudyViewModel(repository);
        repository.Gate("AAAA");            // A 的队列请求会卡住
        study.RefreshAsync();
        Pump();

        var a = study.Books.Single(x => x.BookId == "AAAA");
        var b = study.Books.Single(x => x.BookId == "BB");

        study.SelectedBook = a;             // 发起 A 的加载，卡在半路
        Pump();
        Assert.Null(study.CurrentCard);

        study.SelectedBook = b;             // 切到 B，B 的加载一路跑完
        Pump();
        Assert.Equal("BB-word", study.CurrentCard?.Headword);

        repository.Release("AAAA");         // A 这时才返回
        Pump();

        // A 是上一代的，整批作废——卡片和统计都必须还是 B 的。
        // 没有代号保护时这里会变成 AAAA-word：用户在复习不在当前范围里的词。
        Assert.Equal("BB-word", study.CurrentCard?.Headword);
        Assert.Equal(2, study.Stats.TotalCards);
    }

    [AvaloniaFact]
    public void TheQueueAndTheStatsAlwaysDescribeTheSameBook()
    {
        var repository = new GatedStudyRepository();
        var study = new StudyViewModel(repository);
        study.RefreshAsync();
        Pump();

        foreach (var book in study.Books.Where(x => x.BookId is not null))
        {
            study.SelectedBook = book;
            Pump();

            // 统计里的 TotalCards 是书名长度——对不上就说明队列和统计来自两本书
            Assert.Equal(book.BookId!.Length, study.Stats.TotalCards);
            Assert.StartsWith(book.BookId, study.CurrentCard?.Headword);
        }
    }

    private static void Pump()
    {
        for (var round = 0; round < 12; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
