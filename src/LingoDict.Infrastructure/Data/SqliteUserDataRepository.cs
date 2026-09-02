using System.Globalization;
using LingoDict.Core.Models;
using LingoDict.Core.Services;
using Microsoft.Data.Sqlite;

namespace LingoDict.Infrastructure.Data;

/// <summary>
/// 用户库（收藏 / 历史 / 笔记）。独立于只读的 dictionary.db，重新导入词典不会丢用户数据。
/// </summary>
public sealed class SqliteUserDataRepository : IUserDataRepository, IDisposable
{
    private const string FontScaleKey = "ui_font_scale";
    private const string FontFamilyKey = "ui_font_family";
    private const string SpeechVoiceKey = "ui_speech_voice";
    private const string PaneWidthKey = "ui_left_pane_width";
    private const string ThemeModeKey = "ui_theme_mode";
    private const string AccentKey = "ui_accent";

    private readonly string _connectionString;

    // UI 上多个动作（切换收藏、记录历史、保存笔记）可能同时触发，串行化写入避免 SQLITE_BUSY。
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteUserDataRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Background(() => InitializeCoreAsync(cancellationToken), cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await UserSchema.EnsureAsync(connection, cancellationToken);
    }

    public Task<UserDataStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        Background(() => GetStatsCoreAsync(cancellationToken), cancellationToken);

    private async Task<UserDataStats> GetStatsCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM favorites),
                (SELECT COUNT(*) FROM history),
                (SELECT COUNT(*) FROM notes);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new UserDataStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public Task<bool> IsFavoriteAsync(long wordId, CancellationToken cancellationToken = default) =>
        Background(() => IsFavoriteCoreAsync(wordId, cancellationToken), cancellationToken);

    private async Task<bool> IsFavoriteCoreAsync(long wordId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM favorites WHERE word_id=$word);";
        command.Parameters.AddWithValue("$word", wordId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    public Task<bool> ToggleFavoriteAsync(IWordReference word, CancellationToken cancellationToken = default) =>
        Background(() => ToggleFavoriteCoreAsync(word, cancellationToken), cancellationToken);

    private async Task<bool> ToggleFavoriteCoreAsync(IWordReference word, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(word);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM favorites WHERE word_id=$word;";
            remove.Parameters.AddWithValue("$word", word.WordId);
            if (await remove.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                return false;
            }

            await using var add = connection.CreateCommand();
            add.CommandText = """
                INSERT INTO favorites(word_id, entry_id, headword, phonetic, translation, created_at)
                VALUES($word, $entry, $headword, $phonetic, $translation, $at);
                """;
            BindWord(add, word);
            add.Parameters.AddWithValue("$at", Timestamp());
            await add.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(
        int limit = 500,
        CancellationToken cancellationToken = default) =>
        Background(() => GetFavoritesCoreAsync(limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<FavoriteEntry>> GetFavoritesCoreAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT word_id, entry_id, headword, phonetic, translation, created_at
            FROM favorites
            ORDER BY created_at DESC, word_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20_000));

        var result = new List<FavoriteEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FavoriteEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5))));
        }

        return result;
    }

    public Task RecordViewAsync(IWordReference word, CancellationToken cancellationToken = default) =>
        Background(() => RecordViewCoreAsync(word, cancellationToken), cancellationToken);

    private async Task RecordViewCoreAsync(IWordReference word, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(word);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO history(word_id, entry_id, headword, phonetic, translation, viewed_at, view_count)
                VALUES($word, $entry, $headword, $phonetic, $translation, $at, 1)
                ON CONFLICT(word_id) DO UPDATE SET
                    entry_id=excluded.entry_id,
                    headword=excluded.headword,
                    phonetic=excluded.phonetic,
                    translation=excluded.translation,
                    viewed_at=excluded.viewed_at,
                    view_count=history.view_count + 1;
                """;
            BindWord(command, word);
            command.Parameters.AddWithValue("$at", Timestamp());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        Background(() => GetHistoryCoreAsync(limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<HistoryEntry>> GetHistoryCoreAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT word_id, entry_id, headword, phonetic, translation, viewed_at, view_count
            FROM history
            ORDER BY viewed_at DESC, word_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20_000));

        var result = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HistoryEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                reader.GetInt32(6)));
        }

        return result;
    }

    public Task<bool> RemoveFavoriteAsync(long wordId, CancellationToken cancellationToken = default) =>
        DeleteByWordAsync("favorites", wordId, cancellationToken);

    public Task<bool> RemoveHistoryAsync(long wordId, CancellationToken cancellationToken = default) =>
        DeleteByWordAsync("history", wordId, cancellationToken);

    private async Task<bool> DeleteByWordAsync(string table, long wordId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();

            // 表名是本类内部写死的常量，不来自调用方输入。
            command.CommandText = $"DELETE FROM {table} WHERE word_id=$word;";
            command.Parameters.AddWithValue("$word", wordId);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        Background(() => ClearHistoryCoreAsync(cancellationToken), cancellationToken);

    private async Task ClearHistoryCoreAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM history;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<BookProgress?> GetBookProgressAsync(
        string bookId,
        CancellationToken cancellationToken = default) =>
        Background(() => GetBookProgressCoreAsync(bookId, cancellationToken), cancellationToken);

    private async Task<BookProgress?> GetBookProgressCoreAsync(
        string bookId,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT word_id, headword, updated_at FROM book_progress WHERE book_id=$book;";
        command.Parameters.AddWithValue("$book", bookId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BookProgress(
            bookId,
            reader.GetInt64(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
    }

    public Task SaveBookProgressAsync(
        string bookId,
        IWordReference word,
        CancellationToken cancellationToken = default) =>
        Background(() => SaveBookProgressCoreAsync(bookId, word, cancellationToken), cancellationToken);

    private async Task SaveBookProgressCoreAsync(
        string bookId,
        IWordReference word,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(word);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO book_progress(book_id, word_id, headword, updated_at)
                VALUES($book, $word, $headword, $at)
                ON CONFLICT(book_id) DO UPDATE SET
                    word_id=excluded.word_id,
                    headword=excluded.headword,
                    updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$book", bookId);
            command.Parameters.AddWithValue("$word", word.WordId);
            command.Parameters.AddWithValue("$headword", word.Headword);
            command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<AppearanceSettings> GetAppearanceAsync(CancellationToken cancellationToken = default) =>
        Background(() => GetAppearanceCoreAsync(cancellationToken), cancellationToken);

    private async Task<AppearanceSettings> GetAppearanceCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT key, value FROM meta WHERE key IN ($scale, $family, $voice, $pane, $theme, $accent);";
        command.Parameters.AddWithValue("$scale", FontScaleKey);
        command.Parameters.AddWithValue("$family", FontFamilyKey);
        command.Parameters.AddWithValue("$voice", SpeechVoiceKey);
        command.Parameters.AddWithValue("$pane", PaneWidthKey);
        command.Parameters.AddWithValue("$theme", ThemeModeKey);
        command.Parameters.AddWithValue("$accent", AccentKey);

        var settings = AppearanceSettings.Default;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(1);
            settings = reader.GetString(0) switch
            {
                FontScaleKey => settings with
                {
                    FontScale = double.TryParse(value, CultureInfo.InvariantCulture, out var scale) ? scale : 1.0,
                },
                FontFamilyKey => settings with { FontFamily = value },
                PaneWidthKey => settings with
                {
                    LeftPaneWidth = double.TryParse(value, CultureInfo.InvariantCulture, out var pane)
                        ? pane
                        : AppearanceSettings.DefaultPaneWidth,
                },
                ThemeModeKey => settings with { ThemeMode = value },
                AccentKey => settings with { Accent = value },
                _ => settings with { SpeechVoice = value },
            };
        }

        return settings.Clamp();
    }

    public Task SaveAppearanceAsync(AppearanceSettings settings, CancellationToken cancellationToken = default) =>
        Background(() => SaveAppearanceCoreAsync(settings, cancellationToken), cancellationToken);

    private async Task SaveAppearanceCoreAsync(AppearanceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clamped = settings.Clamp();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO meta(key, value)
                VALUES($scaleKey, $scale), ($paneKey, $pane), ($themeKey, $theme), ($accentKey, $accent)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                DELETE FROM meta WHERE key IN ($familyKey, $voiceKey);
                INSERT INTO meta(key, value)
                SELECT $familyKey, $family WHERE $family IS NOT NULL;
                INSERT INTO meta(key, value)
                SELECT $voiceKey, $voice WHERE $voice IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$scaleKey", FontScaleKey);
            command.Parameters.AddWithValue("$scale", clamped.FontScale.ToString("0.###", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$familyKey", FontFamilyKey);
            command.Parameters.AddWithValue("$family", (object?)clamped.FontFamily ?? DBNull.Value);
            command.Parameters.AddWithValue("$voiceKey", SpeechVoiceKey);
            command.Parameters.AddWithValue("$voice", (object?)clamped.SpeechVoice ?? DBNull.Value);
            command.Parameters.AddWithValue("$paneKey", PaneWidthKey);
            command.Parameters.AddWithValue("$pane", clamped.LeftPaneWidth.ToString("0.#", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$themeKey", ThemeModeKey);
            command.Parameters.AddWithValue("$theme", clamped.ThemeMode);
            command.Parameters.AddWithValue("$accentKey", AccentKey);
            command.Parameters.AddWithValue("$accent", clamped.Accent);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<string> GetNoteAsync(long wordId, CancellationToken cancellationToken = default) =>
        Background(() => GetNoteCoreAsync(wordId, cancellationToken), cancellationToken);

    private async Task<string> GetNoteCoreAsync(long wordId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM notes WHERE word_id=$word;";
        command.Parameters.AddWithValue("$word", wordId);
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? string.Empty;
    }

    public Task SaveNoteAsync(long wordId, string text, CancellationToken cancellationToken = default) =>
        Background(() => SaveNoteCoreAsync(wordId, text, cancellationToken), cancellationToken);

    private async Task SaveNoteCoreAsync(long wordId, string text, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(text))
            {
                command.CommandText = "DELETE FROM notes WHERE word_id=$word;";
                command.Parameters.AddWithValue("$word", wordId);
            }
            else
            {
                command.CommandText = """
                    INSERT INTO notes(word_id, text, updated_at)
                    VALUES($word, $text, $at)
                    ON CONFLICT(word_id) DO UPDATE SET
                        text=excluded.text,
                        updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$word", wordId);
                command.Parameters.AddWithValue("$text", text.Trim());
                command.Parameters.AddWithValue("$at", Timestamp());
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private static void BindWord(SqliteCommand command, IWordReference word)
    {
        command.Parameters.AddWithValue("$word", word.WordId);
        command.Parameters.AddWithValue("$entry", word.EntryId);
        command.Parameters.AddWithValue("$headword", word.Headword);
        command.Parameters.AddWithValue("$phonetic", word.Phonetic);
        command.Parameters.AddWithValue("$translation", word.Translation);
    }

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// 把一次操作整个搬到线程池上。
    /// </summary>
    /// <remarks>
    /// 同 <see cref="SqliteDictionaryRepository"/> 和 <see cref="SqliteStudyRepository"/>：
    /// Microsoft.Data.Sqlite 的 *Async 全是同步实现，不这么做整套用户数据的读写
    /// 都跑在 UI 线程上。这里单条查询比词典轻，但收藏页和历史页是整表取（各 500 / 200 条），
    /// 而且每查一个词都会写一条浏览记录——高频，只是每次不多。
    /// </remarks>
    private static Task<T> Background<T>(Func<Task<T>> work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);

    private static Task Background(Func<Task> work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);

    /// <summary>开连接。同步的——调用方已经在后台任务里了。</summary>
    private SqliteConnection Open()
    {
        LastQueryThreadId = Environment.CurrentManagedThreadId;
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>最后一次查询跑在哪个线程上。测试用：必须不等于调用方的线程。</summary>
    internal int LastQueryThreadId { get; private set; }
}
