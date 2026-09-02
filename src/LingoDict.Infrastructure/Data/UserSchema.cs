using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LingoDict.Infrastructure.Data;

/// <summary>
/// 用户库的建表脚本。收藏/历史/笔记和学习卡片共用一个文件，
/// 两个仓储都调用这里，谁先 Initialize 都能得到完整架构。
/// </summary>
internal static class UserSchema
{
    private const string Script = """
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS favorites(
            word_id INTEGER PRIMARY KEY,
            entry_id INTEGER NOT NULL,
            headword TEXT NOT NULL,
            phonetic TEXT NOT NULL,
            translation TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS history(
            word_id INTEGER PRIMARY KEY,
            entry_id INTEGER NOT NULL,
            headword TEXT NOT NULL,
            phonetic TEXT NOT NULL,
            translation TEXT NOT NULL,
            viewed_at TEXT NOT NULL,
            view_count INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS notes(
            word_id INTEGER PRIMARY KEY,
            text TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS study_cards(
            word_id INTEGER PRIMARY KEY,
            entry_id INTEGER NOT NULL,
            headword TEXT NOT NULL,
            phonetic TEXT NOT NULL,
            translation TEXT NOT NULL,
            book_id TEXT,
            repetition INTEGER NOT NULL,
            interval_days INTEGER NOT NULL,
            ease REAL NOT NULL,
            due_at TEXT NOT NULL,
            last_reviewed_at TEXT,
            correct_count INTEGER NOT NULL,
            lapse_count INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS review_log(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            word_id INTEGER NOT NULL,
            grade INTEGER NOT NULL,
            interval_days INTEGER NOT NULL,
            reviewed_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS book_progress(
            book_id TEXT PRIMARY KEY,
            word_id INTEGER NOT NULL,
            headword TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_favorites_created ON favorites(created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_history_viewed ON history(viewed_at DESC);
        CREATE INDEX IF NOT EXISTS ix_study_due ON study_cards(due_at);
        CREATE INDEX IF NOT EXISTS ix_review_log_time ON review_log(reviewed_at DESC);
        """;

    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = Script;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // v2 的库里没有这一列，CREATE TABLE IF NOT EXISTS 不会补，只能显式加。
        await AddColumnIfMissingAsync(connection, "study_cards", "introduced_at", "TEXT", cancellationToken);

        await using var version = connection.CreateCommand();
        version.CommandText = "INSERT INTO meta(key, value) VALUES('schema_version', '4') " +
                              "ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        await version.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
            probe.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
