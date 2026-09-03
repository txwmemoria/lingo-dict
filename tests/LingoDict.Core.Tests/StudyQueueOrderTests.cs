using LingoDict.Core.Models;
using LingoDict.Core.Study;
using LingoDict.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LingoDict.Core.Tests;

/// <summary>
/// 新词按什么顺序投放。
/// </summary>
/// <remarks>
/// 原来是 <c>ORDER BY book_id, word_id</c>——入库顺序，没有任何教学含义。
///
/// 纯按词频也不对：实测用户那本 4,025 词的专四词书里，词频 1000 名以内的有
/// 164 个（make / way / day 这种），按每天 20 个算，头 8 天全在过已经会的词。
///
/// 所以分三档：**1000 名之后按词频升序 → 词频未知 → 1000 名以内**。
/// 太简单的不删，只是排到最后。
///
/// 词频在 dictionary.db，学习卡在 userdata.db，靠 ATTACH 打通；
/// 没有词典库时整套退回入库顺序，其余功能照常。这两条路径都要覆盖。
/// </remarks>
public sealed class StudyQueueOrderTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingodict-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 一本小词书：两个「太常用」的、三个「该学的」、一个词频未知的。
    /// word_id 刻意和期望顺序错开，否则分不清是排序生效了还是碰巧。
    /// </summary>
    private static readonly (long Id, string Word, int Frequency)[] Vocabulary =
    [
        (1, "make",      45),      // 太常用
        (2, "seizure",   9_400),   // 该学（最靠后）
        (3, "day",       90),      // 太常用
        (4, "delivery",  2_100),   // 该学（最靠前）
        (5, "chondrule", 0),       // 词频未知
        (6, "streamline", 6_800),  // 该学（中间）
    ];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
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

    /// <summary>只造出排序需要的那张表——不必跑整个导入器。</summary>
    private string WriteDictionary()
    {
        var path = Path.Combine(_directory, "dictionary.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE words(id INTEGER PRIMARY KEY, frequency INTEGER NOT NULL DEFAULT 0);";
            create.ExecuteNonQuery();
        }

        foreach (var (id, _, frequency) in Vocabulary)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO words(id, frequency) VALUES($id, $f);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$f", frequency);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    private async Task<SqliteStudyRepository> OpenAsync(string? dictionaryPath)
    {
        var study = new SqliteStudyRepository(Path.Combine(_directory, "userdata.db"), dictionaryPath);
        await study.InitializeAsync();
        await study.SaveSettingsAsync(new StudySettings(NewPerDay: 10, ReviewPerDay: 100));
        await study.AddCardsAsync(
            [.. Vocabulary.Select(x => new WordReference(x.Id, x.Id * 10, x.Word, "p", "释义"))],
            "BOOK_1",
            Now);
        return study;
    }

    [Fact]
    public async Task UsefulWordsComeFirstAndTheOnesYouAlreadyKnowComeLast()
    {
        using var study = await OpenAsync(WriteDictionary());

        var queue = await study.BuildDailyQueueAsync(Now);

        Assert.Equal(
            [
                "delivery",    // 2,100  ← 够常用、又不至于已经会
                "streamline",  // 6,800
                "seizure",     // 9,400
                "chondrule",   // 词频未知，排在有词频的后面
                "day",         // 90     ← 太常用，最后；越接近有用区间的越先出
                "make",        // 45
            ],
            queue.Select(x => x.Headword));
    }

    [Fact]
    public async Task WithoutADictionaryTheQueueStillWorks()
    {
        // 没有词典库时不能报错——退回入库顺序，其余功能一切照常
        using var study = await OpenAsync(dictionaryPath: null);

        var queue = await study.BuildDailyQueueAsync(Now);

        Assert.Equal(Vocabulary.Select(x => x.Word), queue.Select(x => x.Headword));
    }

    [Fact]
    public async Task APathThatDoesNotExistIsTreatedAsNoDictionary()
    {
        // 用户删了词典库、或者路径写错——不该让学习页整个打不开
        using var study = await OpenAsync(Path.Combine(_directory, "nope.db"));

        var queue = await study.BuildDailyQueueAsync(Now);

        Assert.Equal(Vocabulary.Select(x => x.Word), queue.Select(x => x.Headword));
    }

    [Fact]
    public async Task TheDailyCapStillTakesTheMostUsefulOnes()
    {
        using var study = await OpenAsync(WriteDictionary());
        await study.SaveSettingsAsync(new StudySettings(NewPerDay: 2, ReviewPerDay: 100));

        var queue = await study.BuildDailyQueueAsync(Now);

        // 上限砍在排序之后，所以拿到的是最该学的两个，不是入库最早的两个
        Assert.Equal(["delivery", "streamline"], queue.Select(x => x.Headword));
    }
}
