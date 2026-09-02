using LingoDict.Core.Models;
using LingoDict.Infrastructure.Data;

namespace LingoDict.Core.Tests;

public sealed class SqliteUserDataRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lingodict-tests", Guid.NewGuid().ToString("N"));
    private SqliteUserDataRepository _repository = null!;

    private static readonly WordReference Cancel = new(1, 10, "cancel", "'kænsl", "v 取消");
    private static readonly WordReference Abrupt = new(2, 20, "abrupt", "ə'brʌpt", "adj 突然的");

    public async Task InitializeAsync()
    {
        _repository = new SqliteUserDataRepository(Path.Combine(_directory, "userdata.db"));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _repository.Dispose();
        SqliteConnectionCleanup();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 每个入口都得离开调用方的线程。
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite 的 *Async 全是同步实现，漏包一个，那个入口就在
    /// UI 线程上跑 SQL。收藏页和历史页是整表取，每查一个词还要写一条浏览记录。
    /// </remarks>
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

            Check(() => _repository.GetStatsAsync(), "GetStatsAsync");
            Check(() => _repository.ToggleFavoriteAsync(Cancel), "ToggleFavoriteAsync");
            Check(() => _repository.IsFavoriteAsync(Cancel.WordId), "IsFavoriteAsync");
            Check(() => _repository.GetFavoritesAsync(), "GetFavoritesAsync");
            Check(() => _repository.RecordViewAsync(Cancel), "RecordViewAsync");
            Check(() => _repository.GetHistoryAsync(), "GetHistoryAsync");
            Check(() => _repository.SaveBookProgressAsync("CET4_1", Cancel), "SaveBookProgressAsync");
            Check(() => _repository.GetBookProgressAsync("CET4_1"), "GetBookProgressAsync");
            Check(() => _repository.SaveNoteAsync(Cancel.WordId, "记一笔"), "SaveNoteAsync");
            Check(() => _repository.GetNoteAsync(Cancel.WordId), "GetNoteAsync");
            Check(() => _repository.GetAppearanceAsync(), "GetAppearanceAsync");
            Check(() => _repository.ClearHistoryAsync(), "ClearHistoryAsync");
        });
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _repository.ToggleFavoriteAsync(Cancel);
        await _repository.InitializeAsync();

        Assert.True(await _repository.IsFavoriteAsync(Cancel.WordId));
    }

    [Fact]
    public async Task ToggleFavoriteAsync_AddsThenRemoves()
    {
        Assert.False(await _repository.IsFavoriteAsync(Cancel.WordId));

        Assert.True(await _repository.ToggleFavoriteAsync(Cancel));
        Assert.True(await _repository.IsFavoriteAsync(Cancel.WordId));

        var favorite = Assert.Single(await _repository.GetFavoritesAsync());
        Assert.Equal("cancel", favorite.Headword);
        Assert.Equal("'kænsl", favorite.Phonetic);
        Assert.Equal("v 取消", favorite.Translation);
        Assert.Equal(10, favorite.EntryId);

        Assert.False(await _repository.ToggleFavoriteAsync(Cancel));
        Assert.False(await _repository.IsFavoriteAsync(Cancel.WordId));
        Assert.Empty(await _repository.GetFavoritesAsync());
    }

    [Fact]
    public async Task RecordViewAsync_KeepsOneRowPerWordAndCountsViews()
    {
        await _repository.RecordViewAsync(Cancel);
        await _repository.RecordViewAsync(Abrupt);
        await _repository.RecordViewAsync(Cancel);

        var history = await _repository.GetHistoryAsync();

        Assert.Equal(2, history.Count);
        var cancel = history.Single(x => x.WordId == Cancel.WordId);
        Assert.Equal(2, cancel.ViewCount);
        Assert.Equal(1, history.Single(x => x.WordId == Abrupt.WordId).ViewCount);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsMostRecentFirst()
    {
        await _repository.RecordViewAsync(Cancel);
        await Task.Delay(20);
        await _repository.RecordViewAsync(Abrupt);

        var history = await _repository.GetHistoryAsync();

        Assert.Equal(Abrupt.WordId, history[0].WordId);
        Assert.Equal(Cancel.WordId, history[1].WordId);
    }

    [Fact]
    public async Task ClearHistoryAsync_LeavesFavoritesAlone()
    {
        await _repository.ToggleFavoriteAsync(Cancel);
        await _repository.RecordViewAsync(Cancel);

        await _repository.ClearHistoryAsync();

        Assert.Empty(await _repository.GetHistoryAsync());
        Assert.Single(await _repository.GetFavoritesAsync());
    }

    [Fact]
    public async Task RemoveFavoriteAsync_DropsOneRowAndReportsWhetherItExisted()
    {
        await _repository.ToggleFavoriteAsync(Cancel);
        await _repository.ToggleFavoriteAsync(Abrupt);

        Assert.True(await _repository.RemoveFavoriteAsync(Cancel.WordId));
        Assert.False(await _repository.RemoveFavoriteAsync(Cancel.WordId));

        Assert.Equal(Abrupt.WordId, Assert.Single(await _repository.GetFavoritesAsync()).WordId);
        Assert.False(await _repository.IsFavoriteAsync(Cancel.WordId));
    }

    [Fact]
    public async Task RemoveHistoryAsync_DropsOneRowAndLeavesFavoritesAlone()
    {
        await _repository.ToggleFavoriteAsync(Cancel);
        await _repository.RecordViewAsync(Cancel);
        await _repository.RecordViewAsync(Abrupt);

        Assert.True(await _repository.RemoveHistoryAsync(Cancel.WordId));
        Assert.False(await _repository.RemoveHistoryAsync(Cancel.WordId));

        Assert.Equal(Abrupt.WordId, Assert.Single(await _repository.GetHistoryAsync()).WordId);
        Assert.Single(await _repository.GetFavoritesAsync());
    }

    [Fact]
    public async Task SaveNoteAsync_WritesUpdatesAndDeletes()
    {
        Assert.Equal(string.Empty, await _repository.GetNoteAsync(Cancel.WordId));

        await _repository.SaveNoteAsync(Cancel.WordId, "  和 postpone 区分  ");
        Assert.Equal("和 postpone 区分", await _repository.GetNoteAsync(Cancel.WordId));

        await _repository.SaveNoteAsync(Cancel.WordId, "改写后的笔记");
        Assert.Equal("改写后的笔记", await _repository.GetNoteAsync(Cancel.WordId));

        // 空白内容视为删除，而不是存一行空笔记。
        await _repository.SaveNoteAsync(Cancel.WordId, "   ");
        Assert.Equal(string.Empty, await _repository.GetNoteAsync(Cancel.WordId));
        Assert.Equal(0, (await _repository.GetStatsAsync()).NoteCount);
    }

    [Fact]
    public async Task Appearance_RoundTripsAndClampsOutOfRangeValues()
    {
        Assert.Equal(AppearanceSettings.Default, await _repository.GetAppearanceAsync());

        await _repository.SaveAppearanceAsync(new AppearanceSettings(1.15, "SimSun"));
        var saved = await _repository.GetAppearanceAsync();
        Assert.Equal(1.15, saved.FontScale, 3);
        Assert.Equal("SimSun", saved.FontFamily);

        await _repository.SaveAppearanceAsync(new AppearanceSettings(99, "  "));
        var clamped = await _repository.GetAppearanceAsync();
        Assert.Equal(AppearanceSettings.MaxScale, clamped.FontScale, 3);

        // 空白字体名视为"用默认"，不该在库里留下一行空值。
        Assert.Null(clamped.FontFamily);
    }

    [Fact]
    public async Task BookProgress_RoundTripsAndKeepsOneRowPerBook()
    {
        Assert.Null(await _repository.GetBookProgressAsync("CET4_1"));

        var first = new WordReference(1, 10, "cancel", "'kænsl", "v 取消");
        var second = new WordReference(2, 20, "abandon", "ə'bændən", "v 放弃");

        await _repository.SaveBookProgressAsync("CET4_1", first);
        await _repository.SaveBookProgressAsync("CET4_1", second);
        await _repository.SaveBookProgressAsync("CET6_1", first);

        // 一本书只留一行：书签是「挪」不是「攒」
        var cet4 = await _repository.GetBookProgressAsync("CET4_1");
        Assert.Equal(2, cet4!.WordId);
        Assert.Equal("abandon", cet4.Headword);

        // 各本书互不干扰
        Assert.Equal("cancel", (await _repository.GetBookProgressAsync("CET6_1"))!.Headword);
    }

    [Fact]
    public async Task Appearance_RoundTripsThemeModeAndAccent()
    {
        var fresh = await _repository.GetAppearanceAsync();
        Assert.Equal(AppearanceTheme.System, fresh.ThemeMode);
        Assert.Equal(AppearanceTheme.DefaultAccent, fresh.Accent);

        await _repository.SaveAppearanceAsync(
            AppearanceSettings.Default with { ThemeMode = AppearanceTheme.Dark, Accent = "violet" });

        var saved = await _repository.GetAppearanceAsync();
        Assert.Equal(AppearanceTheme.Dark, saved.ThemeMode);
        Assert.Equal("violet", saved.Accent);
    }

    [Fact]
    public async Task Appearance_FallsBackWhenThemeModeIsGarbage()
    {
        // 手改坏的库、或者旧版本存下来的值，不该让应用起不来
        await _repository.SaveAppearanceAsync(AppearanceSettings.Default with { ThemeMode = "midnight" });

        Assert.Equal(AppearanceTheme.System, (await _repository.GetAppearanceAsync()).ThemeMode);
    }

    [Fact]
    public async Task Appearance_SurvivesReopeningTheDatabase()
    {
        await _repository.SaveAppearanceAsync(new AppearanceSettings(0.85, "KaiTi"));

        using var reopened = new SqliteUserDataRepository(Path.Combine(_directory, "userdata.db"));
        var settings = await reopened.GetAppearanceAsync();

        Assert.Equal(0.85, settings.FontScale, 3);
        Assert.Equal("KaiTi", settings.FontFamily);
    }

    [Fact]
    public async Task GetStatsAsync_CountsEachTable()
    {
        await _repository.ToggleFavoriteAsync(Cancel);
        await _repository.RecordViewAsync(Cancel);
        await _repository.RecordViewAsync(Abrupt);
        await _repository.SaveNoteAsync(Abrupt.WordId, "词根 rupt = 断");

        Assert.Equal(new UserDataStats(1, 2, 1), await _repository.GetStatsAsync());
    }

    [Fact]
    public async Task WritesFromMultipleTasksDoNotCollide()
    {
        var writes = Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
        {
            var word = new WordReference(index, index * 10, $"word{index}", string.Empty, "释义");
            await _repository.RecordViewAsync(word);
            await _repository.SaveNoteAsync(word.WordId, $"note {index}");
        }));

        await Task.WhenAll(writes);

        var stats = await _repository.GetStatsAsync();
        Assert.Equal(40, stats.HistoryCount);
        Assert.Equal(40, stats.NoteCount);
    }

    private void SqliteConnectionCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应让测试变红。
        }
    }
}
