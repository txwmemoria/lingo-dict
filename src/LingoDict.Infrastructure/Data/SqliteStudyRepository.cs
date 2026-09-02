using System.Globalization;
using LingoDict.Core.Models;
using LingoDict.Core.Services;
using LingoDict.Core.Study;
using Microsoft.Data.Sqlite;

namespace LingoDict.Infrastructure.Data;

/// <summary>
/// 学习卡片存储。调度逻辑在 <see cref="SpacedRepetitionScheduler"/> 里，这里只负责读写。
/// </summary>
public sealed class SqliteStudyRepository : IStudyRepository, IDisposable
{
    private const string CardColumns = """
        word_id, entry_id, headword, phonetic, translation, book_id,
        repetition, interval_days, ease, due_at, last_reviewed_at, correct_count, lapse_count,
        introduced_at
        """;

    private const string NewPerDayKey = "study_new_per_day";
    private const string ReviewPerDayKey = "study_review_per_day";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteStudyRepository(string databasePath)
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

    public Task<bool> IsInStudyAsync(long wordId, CancellationToken cancellationToken = default) =>
        Background(() => IsInStudyCoreAsync(wordId, cancellationToken), cancellationToken);

    private async Task<bool> IsInStudyCoreAsync(long wordId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM study_cards WHERE word_id=$word);";
        command.Parameters.AddWithValue("$word", wordId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    public Task<StudyCard?> GetCardAsync(long wordId, CancellationToken cancellationToken = default) =>
        Background(() => GetCardCoreAsync(wordId, cancellationToken), cancellationToken);

    private async Task<StudyCard?> GetCardCoreAsync(long wordId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {CardColumns} FROM study_cards WHERE word_id=$word;";
        command.Parameters.AddWithValue("$word", wordId);
        return (await ReadCardsAsync(command, cancellationToken)).FirstOrDefault();
    }

    public Task<int> AddCardsAsync(
        IEnumerable<IWordReference> words,
        string? bookId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Background(() => AddCardsCoreAsync(words, bookId, now, cancellationToken), cancellationToken);

    private async Task<int> AddCardsCoreAsync(
        IEnumerable<IWordReference> words,
        string? bookId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(words);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;

            // OR IGNORE：已在计划里的词保持原有进度，不会被重置成新卡片。
            command.CommandText = """
                INSERT OR IGNORE INTO study_cards(
                    word_id, entry_id, headword, phonetic, translation, book_id,
                    repetition, interval_days, ease, due_at, last_reviewed_at, correct_count, lapse_count,
                    introduced_at)
                VALUES($word, $entry, $headword, $phonetic, $translation, $book,
                    $repetition, $interval, $ease, $due, NULL, 0, 0, NULL);
                """;
            var word = command.Parameters.Add("$word", SqliteType.Integer);
            var entry = command.Parameters.Add("$entry", SqliteType.Integer);
            var headword = command.Parameters.Add("$headword", SqliteType.Text);
            var phonetic = command.Parameters.Add("$phonetic", SqliteType.Text);
            var translation = command.Parameters.Add("$translation", SqliteType.Text);
            var book = command.Parameters.Add("$book", SqliteType.Text);
            var repetition = command.Parameters.Add("$repetition", SqliteType.Integer);
            var interval = command.Parameters.Add("$interval", SqliteType.Integer);
            var ease = command.Parameters.Add("$ease", SqliteType.Real);
            var due = command.Parameters.Add("$due", SqliteType.Text);

            var added = 0;
            foreach (var item in words)
            {
                var card = SpacedRepetitionScheduler.CreateCard(item, bookId, now);
                word.Value = card.WordId;
                entry.Value = card.EntryId;
                headword.Value = card.Headword;
                phonetic.Value = card.Phonetic;
                translation.Value = card.Translation;
                book.Value = (object?)card.BookId ?? DBNull.Value;
                repetition.Value = card.Repetition;
                interval.Value = card.IntervalDays;
                ease.Value = card.Ease;
                due.Value = Format(card.DueAt);
                added += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return added;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<bool> RemoveCardAsync(long wordId, CancellationToken cancellationToken = default) =>
        Background(() => RemoveCardCoreAsync(wordId, cancellationToken), cancellationToken);

    private async Task<bool> RemoveCardCoreAsync(long wordId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM study_cards WHERE word_id=$word;";
            command.Parameters.AddWithValue("$word", wordId);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<StudyCard>> GetDueCardsAsync(
        DateTimeOffset now,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Background(() => GetDueCardsCoreAsync(now, limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<StudyCard>> GetDueCardsCoreAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {CardColumns}
            FROM study_cards
            WHERE due_at <= $now
            ORDER BY due_at, word_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
        return await ReadCardsAsync(command, cancellationToken);
    }

    public Task<StudyCard> ReviewAsync(
        StudyCard card,
        ReviewGrade grade,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Background(() => ReviewCoreAsync(card, grade, now, cancellationToken), cancellationToken);

    private async Task<StudyCard> ReviewCoreAsync(
        StudyCard card,
        ReviewGrade grade,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);
        var updated = SpacedRepetitionScheduler.Apply(card, grade, now);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var save = connection.CreateCommand())
            {
                save.Transaction = (SqliteTransaction)transaction;
                save.CommandText = """
                    UPDATE study_cards SET
                        repetition=$repetition,
                        interval_days=$interval,
                        ease=$ease,
                        due_at=$due,
                        last_reviewed_at=$reviewed,
                        correct_count=$correct,
                        lapse_count=$lapse,
                        introduced_at=$introduced
                    WHERE word_id=$word;
                    """;
                save.Parameters.AddWithValue("$introduced", Format(updated.IntroducedAt ?? now));
                save.Parameters.AddWithValue("$repetition", updated.Repetition);
                save.Parameters.AddWithValue("$interval", updated.IntervalDays);
                save.Parameters.AddWithValue("$ease", updated.Ease);
                save.Parameters.AddWithValue("$due", Format(updated.DueAt));
                save.Parameters.AddWithValue("$reviewed", Format(now));
                save.Parameters.AddWithValue("$correct", updated.CorrectCount);
                save.Parameters.AddWithValue("$lapse", updated.LapseCount);
                save.Parameters.AddWithValue("$word", updated.WordId);
                await save.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var log = connection.CreateCommand())
            {
                log.Transaction = (SqliteTransaction)transaction;
                log.CommandText = """
                    INSERT INTO review_log(word_id, grade, interval_days, reviewed_at)
                    VALUES($word, $grade, $interval, $at);
                    """;
                log.Parameters.AddWithValue("$word", updated.WordId);
                log.Parameters.AddWithValue("$grade", (int)grade);
                log.Parameters.AddWithValue("$interval", updated.IntervalDays);
                log.Parameters.AddWithValue("$at", Format(now));
                await log.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UndoReviewAsync(StudyCard previous, CancellationToken cancellationToken = default) =>
        Background(() => UndoReviewCoreAsync(previous, cancellationToken), cancellationToken);

    private async Task UndoReviewCoreAsync(StudyCard previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var restore = connection.CreateCommand())
            {
                restore.Transaction = (SqliteTransaction)transaction;
                restore.CommandText = """
                    UPDATE study_cards SET
                        repetition=$repetition,
                        interval_days=$interval,
                        ease=$ease,
                        due_at=$due,
                        last_reviewed_at=$reviewed,
                        correct_count=$correct,
                        lapse_count=$lapse,
                        introduced_at=$introduced
                    WHERE word_id=$word;
                    """;
                restore.Parameters.AddWithValue("$repetition", previous.Repetition);
                restore.Parameters.AddWithValue("$interval", previous.IntervalDays);
                restore.Parameters.AddWithValue("$ease", previous.Ease);
                restore.Parameters.AddWithValue("$due", Format(previous.DueAt));
                restore.Parameters.AddWithValue(
                    "$reviewed",
                    previous.LastReviewedAt is { } at ? Format(at) : DBNull.Value);
                restore.Parameters.AddWithValue(
                    "$introduced",
                    previous.IntroducedAt is { } introduced ? Format(introduced) : DBNull.Value);
                restore.Parameters.AddWithValue("$correct", previous.CorrectCount);
                restore.Parameters.AddWithValue("$lapse", previous.LapseCount);
                restore.Parameters.AddWithValue("$word", previous.WordId);
                await restore.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var log = connection.CreateCommand())
            {
                log.Transaction = (SqliteTransaction)transaction;

                // 按自增 id 取最后一条，而不是按 reviewed_at——同一秒内评了多张时时间戳会打平。
                log.CommandText = """
                    DELETE FROM review_log
                    WHERE id = (SELECT MAX(id) FROM review_log WHERE word_id=$word);
                    """;
                log.Parameters.AddWithValue("$word", previous.WordId);
                await log.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<StudyCard>> BuildDailyQueueAsync(
        DateTimeOffset now,
        string? bookId = null,
        CancellationToken cancellationToken = default) =>
        Background(() => BuildDailyQueueCoreAsync(now, bookId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<StudyCard>> BuildDailyQueueCoreAsync(
        DateTimeOffset now,
        string? bookId,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        return await BuildQueueAsync(connection, now, settings, bookId, cancellationToken);
    }

    public Task<IReadOnlyList<StudyBook>> GetStudyBooksAsync(CancellationToken cancellationToken = default) =>
        Background(() => GetStudyBooksCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<StudyBook>> GetStudyBooksCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT book_id, COUNT(*)
            FROM study_cards
            WHERE book_id IS NOT NULL
            GROUP BY book_id
            ORDER BY COUNT(*) DESC, book_id;
            """;

        var result = new List<StudyBook>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // 用户库里只存了 book_id；标题在内容库那边，由界面层补。
            result.Add(new StudyBook(reader.GetString(0), reader.GetString(0), reader.GetInt32(1)));
        }

        return result;
    }

    public Task<IReadOnlyList<StudyDay>> GetReviewHistoryAsync(
        DateTimeOffset now,
        int days = 30,
        CancellationToken cancellationToken = default) =>
        Background(() => GetReviewHistoryCoreAsync(now, days, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<StudyDay>> GetReviewHistoryCoreAsync(
        DateTimeOffset now,
        int days,
        CancellationToken cancellationToken)
    {
        var span = Math.Clamp(days, 1, 365);
        var first = StartOfDay(now).AddDays(-(span - 1));

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // reviewed_at 存的是 UTC，按调用方时区偏移后再切日，才和"今天"的口径一致。
        command.CommandText = """
            SELECT date(reviewed_at, $offset) AS day,
                   SUM(grade = 0), SUM(grade = 1), SUM(grade = 2), SUM(grade = 3)
            FROM review_log
            WHERE reviewed_at >= $from
            GROUP BY day;
            """;
        command.Parameters.AddWithValue("$from", Format(first));
        command.Parameters.AddWithValue("$offset", FormatOffset(now.Offset));

        var counts = new Dictionary<DateOnly, (int Again, int Hard, int Good, int Easy)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (DateOnly.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out var day))
                {
                    counts[day] = (reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
                }
            }
        }

        // 补齐没有复习记录的日子，界面画柱状图时不必再关心缺口。
        var result = new List<StudyDay>(span);
        for (var index = 0; index < span; index++)
        {
            var day = DateOnly.FromDateTime(first.AddDays(index).Date);
            var value = counts.GetValueOrDefault(day);
            result.Add(new StudyDay(day, value.Again, value.Hard, value.Good, value.Easy));
        }

        return result;
    }

    private static string FormatOffset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

    public Task<StudySettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Background(() => GetSettingsCoreAsync(cancellationToken), cancellationToken);

    private async Task<StudySettings> GetSettingsCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        return await ReadSettingsAsync(connection, cancellationToken);
    }

    public Task SaveSettingsAsync(StudySettings settings, CancellationToken cancellationToken = default) =>
        Background(() => SaveSettingsCoreAsync(settings, cancellationToken), cancellationToken);

    private async Task SaveSettingsCoreAsync(StudySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clamped = settings.Clamp();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO meta(key, value) VALUES($newKey, $newValue), ($reviewKey, $reviewValue)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            command.Parameters.AddWithValue("$newKey", NewPerDayKey);
            command.Parameters.AddWithValue("$newValue", clamped.NewPerDay.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$reviewKey", ReviewPerDayKey);
            command.Parameters.AddWithValue("$reviewValue", clamped.ReviewPerDay.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<StudyStats> GetStudyStatsAsync(
        DateTimeOffset now,
        string? bookId = null,
        CancellationToken cancellationToken = default) =>
        Background(() => GetStudyStatsCoreAsync(now, bookId, cancellationToken), cancellationToken);

    private async Task<StudyStats> GetStudyStatsCoreAsync(
        DateTimeOffset now,
        string? bookId,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var remaining = (await BuildQueueAsync(connection, now, settings, bookId, cancellationToken)).Count;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM study_cards WHERE $book IS NULL OR book_id = $book),
                (SELECT COUNT(*) FROM study_cards
                 WHERE due_at <= $now AND ($book IS NULL OR book_id = $book)),
                (SELECT COUNT(*) FROM study_cards
                 WHERE introduced_at IS NULL AND ($book IS NULL OR book_id = $book)),
                (SELECT COUNT(DISTINCT r.word_id) FROM review_log r
                 JOIN study_cards c ON c.word_id = r.word_id
                 WHERE r.reviewed_at >= $today AND ($book IS NULL OR c.book_id = $book));
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$today", Format(StartOfDay(now)));
        command.Parameters.AddWithValue("$book", (object?)bookId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new StudyStats(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            remaining);
    }

    /// <summary>
    /// 队列分三段拼出来：今天已看过又到期的（不占配额）、到期复习（占复习配额）、新词（占新词配额）。
    /// 配额是"张数"上限，用 SQL 表达要绕 row_number，分三次查更直白。
    /// </summary>
    private static async Task<IReadOnlyList<StudyCard>> BuildQueueAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        StudySettings settings,
        string? bookId,
        CancellationToken cancellationToken)
    {
        var today = Format(StartOfDay(now));
        var nowText = Format(now);
        var book = (object?)bookId ?? DBNull.Value;

        var seenToday = await QueryAsync(
            connection,
            $"""
             SELECT {CardColumns} FROM study_cards
             WHERE due_at <= $now
               AND ($book IS NULL OR book_id = $book)
               AND word_id IN (SELECT word_id FROM review_log WHERE reviewed_at >= $today)
             ORDER BY due_at, word_id;
             """,
            cancellationToken,
            ("$now", nowText),
            ("$today", today),
            ("$book", book));

        var introducedToday = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM study_cards WHERE introduced_at >= $today AND ($book IS NULL OR book_id = $book);",
            cancellationToken,
            ("$today", today),
            ("$book", book));

        var reviewedToday = await CountAsync(
            connection,
            """
            SELECT COUNT(DISTINCT r.word_id) FROM review_log r
            JOIN study_cards c ON c.word_id = r.word_id
            WHERE r.reviewed_at >= $today AND ($book IS NULL OR c.book_id = $book)
              AND (c.introduced_at IS NULL OR c.introduced_at < $today);
            """,
            cancellationToken,
            ("$today", today),
            ("$book", book));

        var reviewBudget = Math.Max(0, settings.ReviewPerDay - reviewedToday);
        var newBudget = Math.Max(0, settings.NewPerDay - introducedToday);

        var reviews = reviewBudget == 0
            ? []
            : await QueryAsync(
                connection,
                $"""
                 SELECT {CardColumns} FROM study_cards
                 WHERE due_at <= $now
                   AND introduced_at IS NOT NULL
                   AND ($book IS NULL OR book_id = $book)
                   AND word_id NOT IN (SELECT word_id FROM review_log WHERE reviewed_at >= $today)
                 ORDER BY due_at, word_id
                 LIMIT $limit;
                 """,
                cancellationToken,
                ("$now", nowText),
                ("$today", today),
                ("$book", book),
                ("$limit", reviewBudget));

        var fresh = newBudget == 0
            ? []
            : await QueryAsync(
                connection,
                $"""
                 SELECT {CardColumns} FROM study_cards
                 WHERE introduced_at IS NULL
                   AND ($book IS NULL OR book_id = $book)
                 ORDER BY book_id, word_id
                 LIMIT $limit;
                 """,
                cancellationToken,
                ("$book", book),
                ("$limit", newBudget));

        return [.. seenToday, .. reviews, .. fresh];
    }

    private static async Task<StudySettings> ReadSettingsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta WHERE key IN ($newKey, $reviewKey);";
        command.Parameters.AddWithValue("$newKey", NewPerDayKey);
        command.Parameters.AddWithValue("$reviewKey", ReviewPerDayKey);

        var settings = StudySettings.Default;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!int.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            settings = reader.GetString(0) == NewPerDayKey
                ? settings with { NewPerDay = value }
                : settings with { ReviewPerDay = value };
        }

        return settings.Clamp();
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<StudyCard>> QueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await ReadCardsAsync(command, cancellationToken);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value) => new(value.Date, value.Offset);

    public void Dispose() => _writeLock.Dispose();

    private static async Task<IReadOnlyList<StudyCard>> ReadCardsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<StudyCard>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StudyCard(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetDouble(8),
                Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.IsDBNull(13) ? null : Parse(reader.GetString(13))));
        }

        return result;
    }

    // 到期比较是在 SQL 里对文本做字典序比较，因此必须用固定宽度的 UTC 格式。
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// 把一次操作整个搬到线程池上。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="SqliteDictionaryRepository"/> 一样的道理：Microsoft.Data.Sqlite
    /// 的 *Async 全是同步实现，不这么做整套学习数据的读写都跑在 UI 线程上。
    /// 词典库早就挪走了（换词 93ms → 0.8ms 那次），学习库一直漏着——
    /// 换词书要重建整个队列，是这里最重的一次查询。
    ///
    /// 注意这也让 StudyViewModel 里的 await 变成了真正的挂起点，
    /// 所以那边的 _queueGeneration 保护必须先就位。
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
