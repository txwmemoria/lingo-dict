using LingoDict.Core.Models;
using LingoDict.Core.Study;
using LingoDict.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LingoDict.Core.Tests;

public sealed class SqliteStudyRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lingodict-tests", Guid.NewGuid().ToString("N"));
    private SqliteStudyRepository _repository = null!;

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly WordReference Cancel = new(1, 10, "cancel", "'kænsl", "v 取消");
    private static readonly WordReference Abrupt = new(2, 20, "abrupt", "ə'brʌpt", "adj 突然的");

    private static WordReference[] Words(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new WordReference(i, i * 10, $"word{i:D3}", "p", $"释义 {i}"))];

    public async Task InitializeAsync()
    {
        _repository = new SqliteStudyRepository(Path.Combine(_directory, "userdata.db"));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _repository.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 每个入口都得离开调用方的线程。换词书要重建整个队列，是这里最重的一次查询。
    /// </summary>
    [Fact]
    public void EveryEntryPointLeavesTheCallersThread()
    {
        DedicatedThread.Run(() =>
        {
            var caller = Environment.CurrentManagedThreadId;

            void Check(Func<Task> call, string what)
            {
                call().GetAwaiter().GetResult();
                Assert.True(caller != _repository.LastQueryThreadId, $"{what} 跑在调用线程上");
            }

            Check(() => _repository.AddCardsAsync([Cancel, Abrupt], "CET4_1", Now), "AddCardsAsync");
            Check(() => _repository.IsInStudyAsync(Cancel.WordId), "IsInStudyAsync");
            Check(() => _repository.GetCardAsync(Cancel.WordId), "GetCardAsync");
            Check(() => _repository.GetDueCardsAsync(Now), "GetDueCardsAsync");
            Check(() => _repository.BuildDailyQueueAsync(Now), "BuildDailyQueueAsync");
            Check(() => _repository.GetStudyStatsAsync(Now), "GetStudyStatsAsync");
            Check(() => _repository.GetStudyBooksAsync(), "GetStudyBooksAsync");
            Check(() => _repository.GetReviewHistoryAsync(Now), "GetReviewHistoryAsync");
            Check(() => _repository.GetSettingsAsync(), "GetSettingsAsync");
            Check(() => _repository.SaveSettingsAsync(StudySettings.Default), "SaveSettingsAsync");
            Check(() => _repository.RemoveCardAsync(Abrupt.WordId), "RemoveCardAsync");
        });
    }

    [Fact]
    public async Task AddCardsAsync_StoresEveryFieldRoundTrip()
    {
        await _repository.AddCardsAsync([Cancel], "CET4_1", Now);

        var card = await _repository.GetCardAsync(Cancel.WordId);

        Assert.NotNull(card);
        Assert.Equal("cancel", card!.Headword);
        Assert.Equal("'kænsl", card.Phonetic);
        Assert.Equal("v 取消", card.Translation);
        Assert.Equal("CET4_1", card.BookId);
        Assert.Equal(10, card.EntryId);
        Assert.Equal(SpacedRepetitionScheduler.InitialEase, card.Ease);
        Assert.Equal(Now, card.DueAt);
        Assert.Null(card.LastReviewedAt);
    }

    [Fact]
    public async Task AddCardsAsync_SkipsWordsAlreadyInThePlan()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        await _repository.ReviewAsync((await _repository.GetCardAsync(Cancel.WordId))!, ReviewGrade.Good, Now);

        var added = await _repository.AddCardsAsync([Cancel, Abrupt], null, Now);

        Assert.Equal(1, added);

        // 重复加入不能把已有进度重置回新卡片。
        var existing = await _repository.GetCardAsync(Cancel.WordId);
        Assert.Equal(1, existing!.Repetition);
        Assert.NotNull(existing.LastReviewedAt);
    }

    [Fact]
    public async Task AddCardsAsync_WithoutBookStoresNull()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);

        Assert.Null((await _repository.GetCardAsync(Cancel.WordId))!.BookId);
    }

    [Fact]
    public async Task IsInStudyAndRemove()
    {
        Assert.False(await _repository.IsInStudyAsync(Cancel.WordId));

        await _repository.AddCardsAsync([Cancel], null, Now);
        Assert.True(await _repository.IsInStudyAsync(Cancel.WordId));

        Assert.True(await _repository.RemoveCardAsync(Cancel.WordId));
        Assert.False(await _repository.IsInStudyAsync(Cancel.WordId));
        Assert.False(await _repository.RemoveCardAsync(Cancel.WordId));
    }

    [Fact]
    public async Task GetDueCardsAsync_ExcludesCardsScheduledForLater()
    {
        await _repository.AddCardsAsync([Cancel, Abrupt], null, Now);
        await _repository.ReviewAsync((await _repository.GetCardAsync(Cancel.WordId))!, ReviewGrade.Good, Now);

        // cancel 被推到一天后，abrupt 仍然到期。
        var due = await _repository.GetDueCardsAsync(Now);
        Assert.Equal(Abrupt.WordId, Assert.Single(due).WordId);

        var later = await _repository.GetDueCardsAsync(Now.AddDays(2));
        Assert.Equal(2, later.Count);
    }

    [Fact]
    public async Task GetDueCardsAsync_ComparesDatesChronologicallyAcrossYearBoundary()
    {
        // 到期时间以文本存储，格式必须保证字典序等于时间序。
        await _repository.AddCardsAsync([Cancel], null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(await _repository.GetDueCardsAsync(new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero)));
        Assert.Single(await _repository.GetDueCardsAsync(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task GetDueCardsAsync_PutsOldestDueFirst()
    {
        await _repository.AddCardsAsync([Abrupt], null, Now.AddMinutes(5));
        await _repository.AddCardsAsync([Cancel], null, Now);

        var due = await _repository.GetDueCardsAsync(Now.AddHours(1));

        Assert.Equal([Cancel.WordId, Abrupt.WordId], due.Select(x => x.WordId));
    }

    [Fact]
    public async Task ReviewAsync_PersistsSchedulerResultAndLogsIt()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        var card = (await _repository.GetCardAsync(Cancel.WordId))!;

        var returned = await _repository.ReviewAsync(card, ReviewGrade.Easy, Now);
        var reloaded = (await _repository.GetCardAsync(Cancel.WordId))!;

        Assert.Equal(3, returned.IntervalDays);
        Assert.Equal(returned.IntervalDays, reloaded.IntervalDays);
        Assert.Equal(returned.Repetition, reloaded.Repetition);
        Assert.Equal(returned.Ease, reloaded.Ease, 3);
        Assert.Equal(returned.DueAt, reloaded.DueAt);
        Assert.Equal(Now, reloaded.LastReviewedAt);
        Assert.Equal(1, (await _repository.GetStudyStatsAsync(Now)).ReviewedToday);
    }

    [Fact]
    public async Task ReviewAsync_WithAgain_KeepsCardDueRightNow()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        var card = (await _repository.GetCardAsync(Cancel.WordId))!;

        await _repository.ReviewAsync(card, ReviewGrade.Again, Now);

        Assert.Single(await _repository.GetDueCardsAsync(Now));
        Assert.Equal(1, (await _repository.GetCardAsync(Cancel.WordId))!.LapseCount);
    }

    [Fact]
    public async Task UndoReviewAsync_PutsTheCardAndTheDailyCountBackWhereTheyWere()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        var before = (await _repository.GetCardAsync(Cancel.WordId))!;

        await _repository.ReviewAsync(before, ReviewGrade.Easy, Now);
        await _repository.UndoReviewAsync(before);

        var after = (await _repository.GetCardAsync(Cancel.WordId))!;
        Assert.Equal(before, after);

        // 删掉复习日志才算真撤销，否则今日已学和 30 天统计里还留着这一笔。
        Assert.Equal(0, (await _repository.GetStudyStatsAsync(Now)).ReviewedToday);
        Assert.Equal(0, (await _repository.GetReviewHistoryAsync(Now)).Sum(x => x.Total));

        // 卡片回到未学状态，今天的队列里还得能再遇到它。
        Assert.Single(await _repository.BuildDailyQueueAsync(Now));
    }

    [Fact]
    public async Task UndoReviewAsync_OnlyRollsBackTheLastReviewOfThatWord()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        var first = (await _repository.GetCardAsync(Cancel.WordId))!;

        var afterFirst = await _repository.ReviewAsync(first, ReviewGrade.Good, Now);
        await _repository.ReviewAsync(afterFirst, ReviewGrade.Good, Now.AddMinutes(1));

        await _repository.UndoReviewAsync(afterFirst);

        Assert.Equal(afterFirst, await _repository.GetCardAsync(Cancel.WordId));

        // 同一分钟内评了两次，只能撤掉一条——按自增 id 取而不是按时间戳。
        Assert.Equal(1, (await _repository.GetStudyStatsAsync(Now)).ReviewedToday);
    }

    [Fact]
    public async Task GetStudyStatsAsync_CountsTotalDueNewAndToday()
    {
        await _repository.AddCardsAsync([Cancel, Abrupt], null, Now);
        await _repository.ReviewAsync((await _repository.GetCardAsync(Cancel.WordId))!, ReviewGrade.Good, Now);

        var stats = await _repository.GetStudyStatsAsync(Now);

        Assert.Equal(2, stats.TotalCards);
        Assert.Equal(1, stats.DueCount);       // cancel 已排到明天
        Assert.Equal(1, stats.NewCount);       // abrupt 还没复习过
        Assert.Equal(1, stats.ReviewedToday);
    }

    [Fact]
    public async Task DailyQuotaFollowsTheCallersLocalDayNotUtc()
    {
        // 东八区：本地 09:00 相当于 UTC 前一天 01:00。额度必须按本地日切分，
        // 否则中国用户的每日配额会在早上 08:00 重置。
        var offset = TimeSpan.FromHours(8);
        var morning = new DateTimeOffset(2026, 8, 26, 9, 0, 0, offset);
        var lateNight = new DateTimeOffset(2026, 8, 26, 23, 30, 0, offset);
        var nextMorning = new DateTimeOffset(2026, 8, 27, 0, 30, 0, offset);

        await _repository.SaveSettingsAsync(new StudySettings(1, 100));
        await _repository.AddCardsAsync(Words(5), null, morning);
        var card = Assert.Single(await _repository.BuildDailyQueueAsync(morning));
        await _repository.ReviewAsync(card, ReviewGrade.Good, morning);

        // 同一个本地日的深夜：额度已用完。
        Assert.DoesNotContain(await _repository.BuildDailyQueueAsync(lateNight), x => x.IsNew);
        Assert.Equal(1, (await _repository.GetStudyStatsAsync(lateNight)).ReviewedToday);

        // 跨过本地零点：额度重置。
        Assert.Single(await _repository.BuildDailyQueueAsync(nextMorning), x => x.IsNew);
        Assert.Equal(0, (await _repository.GetStudyStatsAsync(nextMorning)).ReviewedToday);
    }

    [Fact]
    public async Task GetStudyStatsAsync_ReviewedTodayResetsOnTheNextDay()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        await _repository.ReviewAsync((await _repository.GetCardAsync(Cancel.WordId))!, ReviewGrade.Good, Now);

        Assert.Equal(0, (await _repository.GetStudyStatsAsync(Now.AddDays(1))).ReviewedToday);
    }

    [Fact]
    public async Task BuildDailyQueueAsync_ReleasesNewCardsInDailyBatches()
    {
        await _repository.SaveSettingsAsync(new StudySettings(NewPerDay: 3, ReviewPerDay: 100));
        await _repository.AddCardsAsync(Words(20), "CET4_1", Now);

        // 整本加入后 20 张全部到期，但今天只应放出 3 张。
        Assert.Equal(20, (await _repository.GetDueCardsAsync(Now, limit: 100)).Count);
        var queue = await _repository.BuildDailyQueueAsync(Now);
        Assert.Equal(3, queue.Count);
        Assert.All(queue, x => Assert.True(x.IsNew));

        var stats = await _repository.GetStudyStatsAsync(Now);
        Assert.Equal(20, stats.DueCount);
        Assert.Equal(3, stats.RemainingToday);
    }

    [Fact]
    public async Task BuildDailyQueueAsync_StopsAfterTheDailyNewQuotaIsUsed()
    {
        await _repository.SaveSettingsAsync(new StudySettings(3, 100));
        await _repository.AddCardsAsync(Words(20), null, Now);

        foreach (var card in await _repository.BuildDailyQueueAsync(Now))
        {
            await _repository.ReviewAsync(card, ReviewGrade.Good, Now);
        }

        Assert.Empty(await _repository.BuildDailyQueueAsync(Now));

        // 第二天配额重置：昨天那 3 张到期回来复习，另外再放 3 张新词。
        var tomorrow = Now.AddDays(1);
        var next = await _repository.BuildDailyQueueAsync(tomorrow);
        Assert.Equal(3, next.Count(x => !x.IsNew));
        Assert.Equal(3, next.Count(x => x.IsNew));
    }

    [Fact]
    public async Task BuildDailyQueueAsync_AlwaysBringsBackCardsFailedToday()
    {
        await _repository.SaveSettingsAsync(new StudySettings(1, 0));
        await _repository.AddCardsAsync(Words(5), null, Now);

        var card = Assert.Single(await _repository.BuildDailyQueueAsync(Now));
        await _repository.ReviewAsync(card, ReviewGrade.Again, Now);

        // 新词配额已用完，但答错的卡片必须回到队列，否则今天没法收尾。
        var again = Assert.Single(await _repository.BuildDailyQueueAsync(Now));
        Assert.Equal(card.WordId, again.WordId);
        Assert.False(again.IsNew);
    }

    [Fact]
    public async Task BuildDailyQueueAsync_CountsReviewsAgainstTheirOwnQuota()
    {
        await _repository.SaveSettingsAsync(new StudySettings(10, 100));
        await _repository.AddCardsAsync(Words(4), null, Now);
        foreach (var card in await _repository.BuildDailyQueueAsync(Now))
        {
            await _repository.ReviewAsync(card, ReviewGrade.Good, Now);
        }

        // 次日全部到期，把复习上限压到 2。
        var tomorrow = Now.AddDays(1);
        await _repository.SaveSettingsAsync(new StudySettings(0, 2));

        var queue = await _repository.BuildDailyQueueAsync(tomorrow);
        Assert.Equal(2, queue.Count);
        Assert.All(queue, x => Assert.False(x.IsNew));
    }

    [Fact]
    public async Task ReviewAsync_StampsIntroducedAtOnlyOnce()
    {
        await _repository.AddCardsAsync([Cancel], null, Now);
        var card = (await _repository.GetCardAsync(Cancel.WordId))!;
        Assert.True(card.IsNew);

        await _repository.ReviewAsync(card, ReviewGrade.Good, Now);
        var introduced = (await _repository.GetCardAsync(Cancel.WordId))!;
        Assert.Equal(Now, introduced.IntroducedAt);

        await _repository.ReviewAsync(introduced, ReviewGrade.Good, Now.AddDays(3));
        Assert.Equal(Now, (await _repository.GetCardAsync(Cancel.WordId))!.IntroducedAt);
    }

    [Fact]
    public async Task Settings_RoundTripAndClampOutOfRangeValues()
    {
        Assert.Equal(StudySettings.Default, await _repository.GetSettingsAsync());

        await _repository.SaveSettingsAsync(new StudySettings(30, 400));
        Assert.Equal(new StudySettings(30, 400), await _repository.GetSettingsAsync());

        await _repository.SaveSettingsAsync(new StudySettings(-5, 999_999));
        Assert.Equal(new StudySettings(0, StudySettings.MaxPerDay), await _repository.GetSettingsAsync());
    }

    [Fact]
    public async Task InitializeAsync_AddsIntroducedAtToADatabaseCreatedBeforeIt()
    {
        // 模拟 v2 的库：建表时故意不带 introduced_at。
        var path = Path.Combine(_directory, "legacy.db");
        await using (var legacy = new SqliteConnection($"Data Source={path}"))
        {
            await legacy.OpenAsync();
            await using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE study_cards(
                    word_id INTEGER PRIMARY KEY, entry_id INTEGER NOT NULL, headword TEXT NOT NULL,
                    phonetic TEXT NOT NULL, translation TEXT NOT NULL, book_id TEXT,
                    repetition INTEGER NOT NULL, interval_days INTEGER NOT NULL, ease REAL NOT NULL,
                    due_at TEXT NOT NULL, last_reviewed_at TEXT, correct_count INTEGER NOT NULL,
                    lapse_count INTEGER NOT NULL);
                INSERT INTO study_cards VALUES(1, 10, 'cancel', 'k', 'v 取消', NULL, 0, 0, 2.5,
                    '2020-01-01T00:00:00.0000000Z', NULL, 0, 0);
                """;
            await create.ExecuteNonQueryAsync();
        }

        using var upgraded = new SqliteStudyRepository(path);
        await upgraded.InitializeAsync();

        var card = await upgraded.GetCardAsync(1);
        Assert.NotNull(card);
        Assert.True(card!.IsNew);
        Assert.Single(await upgraded.BuildDailyQueueAsync(Now));
    }

    [Fact]
    public async Task BuildDailyQueueAsync_CanBeNarrowedToOneBook()
    {
        await _repository.SaveSettingsAsync(new StudySettings(100, 100));
        await _repository.AddCardsAsync(Words(3), "CET4", Now);
        await _repository.AddCardsAsync(
            [new WordReference(90, 900, "extra", "p", "释义")], "CET6", Now);

        Assert.Equal(4, (await _repository.BuildDailyQueueAsync(Now)).Count);

        var narrowed = await _repository.BuildDailyQueueAsync(Now, "CET6");
        Assert.Equal(90, Assert.Single(narrowed).WordId);
    }

    [Fact]
    public async Task GetStudyStatsAsync_CountsOnlyTheSelectedBook()
    {
        await _repository.AddCardsAsync(Words(3), "CET4", Now);
        await _repository.AddCardsAsync(
            [new WordReference(90, 900, "extra", "p", "释义")], "CET6", Now);

        Assert.Equal(4, (await _repository.GetStudyStatsAsync(Now)).TotalCards);
        Assert.Equal(1, (await _repository.GetStudyStatsAsync(Now, "CET6")).TotalCards);
        Assert.Equal(3, (await _repository.GetStudyStatsAsync(Now, "CET4")).NewCount);
    }

    [Fact]
    public async Task GetStudyBooksAsync_ListsBooksByCardCount()
    {
        await _repository.AddCardsAsync(Words(3), "CET4", Now);
        await _repository.AddCardsAsync(
            [new WordReference(90, 900, "extra", "p", "释义")], "CET6", Now);
        // 单词加入时不带词书，不该出现在列表里。
        await _repository.AddCardsAsync([new WordReference(91, 910, "loose", "p", "释义")], null, Now);

        var books = await _repository.GetStudyBooksAsync();

        Assert.Equal(["CET4", "CET6"], books.Select(x => x.BookId));
        Assert.Equal(3, books[0].CardCount);
    }

    [Fact]
    public async Task GetReviewHistoryAsync_FillsGapsAndSplitsByGrade()
    {
        await _repository.AddCardsAsync(Words(3), null, Now);
        var queue = await _repository.BuildDailyQueueAsync(Now);
        await _repository.ReviewAsync(queue[0], ReviewGrade.Good, Now);
        await _repository.ReviewAsync(queue[1], ReviewGrade.Again, Now);

        var history = await _repository.GetReviewHistoryAsync(Now, days: 7);

        Assert.Equal(7, history.Count);
        Assert.Equal(DateOnly.FromDateTime(Now.Date), history[^1].Date);

        var today = history[^1];
        Assert.Equal(1, today.Good);
        Assert.Equal(1, today.Again);
        Assert.Equal(2, today.Total);
        Assert.Equal(0.5, today.Accuracy, 3);

        // 没有复习的日子补 0，界面画图时不用管缺口。
        Assert.All(history.Take(6), x => Assert.Equal(0, x.Total));
    }

    [Fact]
    public async Task GetReviewHistoryAsync_GroupsByTheCallersLocalDay()
    {
        // 东八区本地 00:30 复习，UTC 还是前一天 16:30。必须归到本地的这一天。
        var offset = TimeSpan.FromHours(8);
        var afterMidnight = new DateTimeOffset(2026, 8, 26, 0, 30, 0, offset);

        await _repository.AddCardsAsync(Words(1), null, afterMidnight);
        var card = Assert.Single(await _repository.BuildDailyQueueAsync(afterMidnight));
        await _repository.ReviewAsync(card, ReviewGrade.Good, afterMidnight);

        var history = await _repository.GetReviewHistoryAsync(afterMidnight, days: 3);

        Assert.Equal(new DateOnly(2026, 8, 26), history[^1].Date);
        Assert.Equal(1, history[^1].Total);
    }

    [Fact]
    public async Task SharesTheDatabaseFileWithUserData()
    {
        // 两个仓储指向同一个文件，谁先建表都要能拿到完整架构。
        var path = Path.Combine(_directory, "shared.db");
        using var study = new SqliteStudyRepository(path);
        using var userData = new SqliteUserDataRepository(path);

        await study.InitializeAsync();
        await userData.InitializeAsync();

        await study.AddCardsAsync([Cancel], null, Now);
        await userData.ToggleFavoriteAsync(Cancel);

        Assert.True(await study.IsInStudyAsync(Cancel.WordId));
        Assert.True(await userData.IsFavoriteAsync(Cancel.WordId));
    }
}
