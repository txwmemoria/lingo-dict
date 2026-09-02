using LingoDict.Core.Models;
using LingoDict.Core.Services;
using LingoDict.Core.Text;
using Microsoft.Data.Sqlite;

namespace LingoDict.Infrastructure.Data;

public sealed class SqliteDictionaryRepository(string databasePath) : IDictionaryRepository
{
    /// <summary>与 <see cref="ReadSummary"/> 的列顺序一一对应。</summary>
    /// <summary>每个搜索结果展示几条搭配。三条够看出区别，再多列表就太挤。</summary>
    private const int CollocationsPerWord = 3;

    private const string SummaryColumns = """
        w.id, e.id, w.headword,
        CASE WHEN e.us_phone <> '' THEN e.us_phone ELSE e.uk_phone END,
        e.translation,
        (SELECT COUNT(*) FROM book_words bw WHERE bw.word_id=w.id)
        """;

    private const string SupportedSchemaVersion = "7";

    private readonly string _connectionString = CreateConnectionString(databasePath);

    public Task<DictionaryStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        Background(() => GetStatsCoreAsync(cancellationToken), cancellationToken);

    private async Task<DictionaryStats> GetStatsCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM words),
                (SELECT COUNT(*) FROM books),
                (SELECT COUNT(*) FROM book_words);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DictionaryStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public Task<IReadOnlyList<WordSummary>> SearchAsync(
        string query,
        int limit = 80,
        CancellationToken cancellationToken = default) =>
        Background(() => SearchCoreAsync(query, limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<WordSummary>> SearchCoreAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var trimmed = query.Trim();
        var clamped = Math.Clamp(limit, 1, 500);
        await using var connection = Open();

        if (trimmed.Length == 0)
        {
            // 首屏浏览不带搭配：那时用户还没有要比较的对象，白花一次查询。
            return await BrowseAsync(connection, clamped, cancellationToken).ConfigureAwait(false);
        }

        var normalized = WordNormalizer.Normalize(trimmed);

        // 输入的可能是个变化形式（cancelled / mice / better）。库里的词头基本都是
        // 原形，不查这张表的话这类输入要么一无所获，要么只在别的词的例句里
        // 蹭到几个不相干的命中。命中原形就把它顶到最前面——用户要的就是它。
        var lemma = await ResolveFormAsync(connection, normalized, cancellationToken).ConfigureAwait(false);

        var match = SearchText.BuildMatchExpression(trimmed);
        if (match is not null)
        {
            var hits = await MatchAsync(connection, match, normalized, clamped, cancellationToken).ConfigureAwait(false);
            if (hits.Count > 0 || lemma is not null)
            {
                return await AttachCollocationsAsync(
                    connection, Prepend(lemma, hits, clamped), CollocationsPerWord, cancellationToken).ConfigureAwait(false);
            }
        }

        // FTS 要求词元相邻，"取消 订单" 这类跨字段的输入会落空；此时退回子串扫描兜底。
        var scanned = await ScanAsync(connection, trimmed, normalized, clamped, cancellationToken).ConfigureAwait(false);
        return await AttachCollocationsAsync(
            connection, Prepend(lemma, scanned, clamped), CollocationsPerWord, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把词形回退命中的原形放到结果最前面，并去掉后面重复的那条。
    /// </summary>
    private static IReadOnlyList<WordSummary> Prepend(WordSummary? lemma, IReadOnlyList<WordSummary> rest, int limit)
    {
        if (lemma is null)
        {
            return rest;
        }

        var result = new List<WordSummary>(rest.Count + 1) { lemma };
        result.AddRange(rest.Where(x => x.Id != lemma.Id));
        return result.Count > limit ? result[..limit] : result;
    }

    /// <summary>
    /// 输入是某个词头的变化形式吗？是就返回那个词头。
    /// </summary>
    /// <remarks>
    /// word_forms 的主键就是 form，这是一次主键查找，代价可以忽略，
    /// 所以每次搜索都问一遍，不必等别的路子先落空。
    /// </remarks>
    private static async Task<WordSummary?> ResolveFormAsync(
        SqliteConnection connection,
        string normalized,
        CancellationToken cancellationToken)
    {
        if (normalized.Length == 0)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM word_forms f
            JOIN words w ON w.id=f.word_id
            JOIN entries e ON e.id=w.canonical_entry_id
            WHERE f.form=$form;
            """;
        command.Parameters.AddWithValue("$form", normalized);
        var found = await ReadSummariesAsync(command, cancellationToken).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    private static async Task<IReadOnlyList<WordSummary>> BrowseAsync(
        SqliteConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM words w
            JOIN entries e ON e.id=w.canonical_entry_id
            -- 词频优先，未知（0）垫底。原来纯按长度排，短的生僻词
            -- （apt、wry）会挤在 the、have 前面。
            ORDER BY
                CASE WHEN w.frequency = 0 THEN 1 ELSE 0 END, w.frequency,
                length(w.headword), w.headword COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadSummariesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<WordSummary>> MatchAsync(
        SqliteConnection connection,
        string match,
        string normalized,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // 先用 bm25 取一批候选（词头列权重 8 倍），内层 LIMIT 保证像"的"这种高频字
        // 也不会退化成全索引扫描。索引按 entry 建，同一个词可能有多条命中，
        // 用 ROW_NUMBER 只保留分数最好的那条——它就是要展示和打开的释义。
        command.CommandText = $"""
            WITH hits AS (
                SELECT rowid AS entry_id, bm25(words_fts, 8.0, 1.0) AS score
                FROM words_fts
                WHERE words_fts MATCH $match
                ORDER BY score
                LIMIT $scan
            ),
            ranked AS (
                SELECT h.entry_id, h.score, x.word_id,
                       ROW_NUMBER() OVER (PARTITION BY x.word_id ORDER BY h.score, h.entry_id) AS rn
                FROM hits h
                JOIN entries x ON x.id = h.entry_id
            )
            SELECT {SummaryColumns}
            FROM ranked r
            JOIN words w ON w.id=r.word_id
            JOIN entries e ON e.id=r.entry_id
            WHERE r.rn = 1
            ORDER BY
                CASE
                    WHEN w.normalized=$exact THEN 0
                    WHEN w.normalized LIKE $prefix THEN 1
                    ELSE 2
                END,
                r.score,
                CASE WHEN w.frequency = 0 THEN 1 ELSE 0 END, w.frequency,
                length(w.headword), w.headword COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$exact", normalized);
        command.Parameters.AddWithValue("$prefix", normalized + "%");

        // 每个词可能贡献多条命中，候选量要比目标条数放宽更多。
        command.Parameters.AddWithValue("$scan", Math.Min(limit * 20, 4_000));
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadSummariesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<WordSummary>> ScanAsync(
        SqliteConnection connection,
        string trimmed,
        string normalized,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // 同样按 entry 匹配，优先返回释义真正命中的那一条，没有则回落到 canonical。
        //
        // 先把命中的 word_id 收成一个小集合，再去开窗排序——不要写回
        // `WHERE v.normalized LIKE ... OR x.translation LIKE ...`。
        // 那种写法里 OR 让优化器放弃 words.normalized 上的索引，于是先把
        // 96876 行 entries 全连上、再对连接结果开窗。输入一个匹配不上的前缀
        // （打到一半的英文，比如 trendi）要 326ms，冷缓存时近 2 秒——
        // 而这条路径恰恰是「边打字边搜」时最常走到的。
        // 拆成 UNION 之后前缀那一支能走索引，只剩释义子串那一支必须扫。
        command.CommandText = $"""
            WITH hit AS (
                SELECT id AS word_id FROM words WHERE normalized LIKE $prefix ESCAPE '\'
                UNION
                SELECT word_id FROM entries WHERE translation LIKE $contains ESCAPE '\'
            ),
            ranked AS (
                SELECT x.id AS entry_id, x.word_id,
                       ROW_NUMBER() OVER (
                           PARTITION BY x.word_id
                           ORDER BY CASE WHEN x.translation LIKE $contains ESCAPE '\' THEN 0 ELSE 1 END, x.id) AS rn
                FROM entries x
                WHERE x.word_id IN (SELECT word_id FROM hit)
            )
            SELECT {SummaryColumns}
            FROM ranked r
            JOIN words w ON w.id=r.word_id
            JOIN entries e ON e.id=r.entry_id
            WHERE r.rn = 1
            ORDER BY
                CASE
                    WHEN w.normalized=$exact THEN 0
                    WHEN w.normalized LIKE $prefix ESCAPE '\' THEN 1
                    ELSE 2
                END,
                length(w.headword), w.headword COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$exact", normalized);
        command.Parameters.AddWithValue("$prefix", EscapeLike(normalized) + "%");
        command.Parameters.AddWithValue("$contains", "%" + EscapeLike(trimmed) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadSummariesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<IReadOnlyList<WordSummary>> ReadSummariesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<WordSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadSummary(reader));
        }

        return result;
    }

    /// <summary>
    /// 给一批词各取几条最有代表性的搭配。rank 在导入期算好（见 DictionaryDatabaseBuilder），
    /// 这里只按它取前几条——虚词搭配（see as、look in）排在最后，不会占掉名额。
    /// </summary>
    private static async Task<IReadOnlyList<WordSummary>> AttachCollocationsAsync(
        SqliteConnection connection,
        IReadOnlyList<WordSummary> summaries,
        int perWord,
        CancellationToken cancellationToken)
    {
        if (summaries.Count == 0)
        {
            return summaries;
        }

        var ids = string.Join(',', summaries.Select(x => x.Id));
        await using var command = connection.CreateCommand();

        // ids 由上一步查询产出的整数拼成，不来自用户输入。
        command.CommandText = $"""
            SELECT word_id, text FROM (
                SELECT word_id, text,
                       ROW_NUMBER() OVER (PARTITION BY word_id ORDER BY rank, length(text), text) AS rn
                FROM phrases
                WHERE word_id IN ({ids})
            )
            WHERE rn <= $per;
            """;
        command.Parameters.AddWithValue("$per", perWord);

        var byWord = new Dictionary<long, List<string>>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!byWord.TryGetValue(reader.GetInt64(0), out var list))
                {
                    byWord[reader.GetInt64(0)] = list = [];
                }

                list.Add(reader.GetString(1));
            }
        }

        return [.. summaries.Select(x =>
            byWord.TryGetValue(x.Id, out var list) ? x with { Collocations = list } : x)];
    }

    public Task<IReadOnlyList<PhraseHit>> SearchPhrasesAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default) =>
        Background(() => SearchPhrasesCoreAsync(query, limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<PhraseHit>> SearchPhrasesCoreAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var match = SearchText.BuildMatchExpression(query);
        if (match is null)
        {
            return [];
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // 短语列权重高于译文：搜 "pay attention" 应当优先命中短语本身，
        // 而不是某条译文里恰好含这两个字的其它短语。
        command.CommandText = """
            SELECT p.word_id, w.headword, p.text, p.translation
            FROM phrases_fts f
            JOIN phrases p ON p.id = f.rowid
            JOIN words w ON w.id = p.word_id
            WHERE phrases_fts MATCH $match
            ORDER BY bm25(phrases_fts, 4.0, 1.0), length(p.text)
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<PhraseHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PhraseHit(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return results;
    }

    public Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default) =>
        Background(() => GetBooksCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<BookSummary>> GetBooksCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.id, b.title, b.tags, COUNT(bw.word_id)
            FROM books b
            LEFT JOIN book_words bw ON bw.book_id=b.id
            GROUP BY b.id, b.title, b.tags
            ORDER BY b.title COLLATE NOCASE;
            """;
        var result = new List<BookSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new BookSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return result;
    }

    public Task<IReadOnlyList<WordSummary>> GetBookWordsAsync(
        string bookId,
        int limit = 500,
        CancellationToken cancellationToken = default) =>
        Background(() => GetBookWordsCoreAsync(bookId, limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<WordSummary>> GetBookWordsCoreAsync(
        string bookId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM book_words m
            JOIN words w ON w.id=m.word_id
            JOIN entries e ON e.id=m.entry_id
            WHERE m.book_id=$book
            ORDER BY m.rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$book", bookId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20_000));
        return await ReadSummariesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public Task<WordDetail?> GetWordAsync(
        long wordId,
        long? entryId = null,
        CancellationToken cancellationToken = default) =>
        Background(() => GetWordCoreAsync(wordId, entryId, cancellationToken), cancellationToken);

    private async Task<WordDetail?> GetWordCoreAsync(
        long wordId,
        long? entryId,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, e.id, w.headword, e.uk_phone, e.us_phone, e.content
            FROM words w
            JOIN entries e ON e.id=COALESCE($entry, w.canonical_entry_id)
            WHERE w.id=$word AND e.word_id=w.id;
            """;
        command.Parameters.AddWithValue("$word", wordId);
        command.Parameters.AddWithValue("$entry", entryId is null ? DBNull.Value : entryId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var id = reader.GetInt64(0);
        var selectedEntryId = reader.GetInt64(1);
        var headword = reader.GetString(2);
        var ukPhone = reader.GetString(3);
        var usPhone = reader.GetString(4);
        var stored = (byte[])reader[5];
        await reader.CloseAsync().ConfigureAwait(false);

        // 整个读路径只有这里需要解压，而且一次只有一行。
        var content = ContentCodec.Decode(stored);

        await using var sourceCommand = connection.CreateCommand();
        sourceCommand.CommandText = """
            SELECT DISTINCT b.title
            FROM book_words bw
            JOIN books b ON b.id=bw.book_id
            WHERE bw.word_id=$word
            ORDER BY b.title COLLATE NOCASE;
            """;
        sourceCommand.Parameters.AddWithValue("$word", wordId);
        var sourceBooks = new List<string>();
        await using var sourceReader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        while (await sourceReader.ReadAsync(cancellationToken))
        {
            sourceBooks.Add(sourceReader.GetString(0));
        }

        return new WordDetail(
            id,
            selectedEntryId,
            headword,
            ukPhone,
            usPhone,
            content.Translations,
            content.Sentences,
            content.ExamSentences,
            content.Phrases,
            content.Synonyms,
            content.RelatedWords,
            sourceBooks);
    }

    public Task<WordSummary?> FindByHeadwordAsync(
        string headword,
        CancellationToken cancellationToken = default) =>
        Background(() => FindByHeadwordCoreAsync(headword, cancellationToken), cancellationToken);

    private async Task<WordSummary?> FindByHeadwordCoreAsync(
        string headword,
        CancellationToken cancellationToken)
    {
        var normalized = WordNormalizer.Normalize(headword);
        if (normalized.Length == 0)
        {
            return null;
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM words w
            JOIN entries e ON e.id=w.canonical_entry_id
            WHERE w.normalized=$normalized
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$normalized", normalized);
        return (await ReadSummariesAsync(command, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    public Task<IReadOnlyDictionary<string, WordPreview>> GetWordIndexAsync(
        CancellationToken cancellationToken = default) =>
        Background(() => GetWordIndexCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyDictionary<string, WordPreview>> GetWordIndexCoreAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.normalized, w.headword,
                   CASE WHEN e.us_phone <> '' THEN e.us_phone ELSE e.uk_phone END,
                   e.translation
            FROM words w
            JOIN entries e ON e.id = w.canonical_entry_id;
            """;

        var result = new Dictionary<string, WordPreview>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // 只留首行释义：整份索引常驻内存，长释义会把占用推高好几倍。
            var translation = reader.GetString(3);
            var newline = translation.IndexOf('\n', StringComparison.Ordinal);
            result[reader.GetString(0)] = new WordPreview(
                reader.GetString(1),
                reader.GetString(2),
                newline < 0 ? translation : translation[..newline]);
        }

        return result;
    }

    public Task<IReadOnlyList<AlternateEntry>> GetAlternateEntriesAsync(
        long wordId,
        long excludeEntryId,
        CancellationToken cancellationToken = default) =>
        Background(() => GetAlternateEntriesCoreAsync(wordId, excludeEntryId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<AlternateEntry>> GetAlternateEntriesCoreAsync(
        long wordId,
        long excludeEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // 顺带把每条释义出自哪几本词书拼出来——这正是它们彼此不同的原因。
        //
        // 最后那列是**正在显示的那条**释义，每行都一样。取它是为了把各词书之间
        // 重复的英文尾巴去掉：实测 64% 的多释义词头各条英文完全一致，而英文占
        // 这些行 82% 的字符，等于同一句话在这一段里抄好几遍——何况完整的英文
        // 上面「释义」里已经有了。一次标量子查询，代价可以忽略。
        command.CommandText = """
            SELECT e.id, e.translation,
                   COALESCE((SELECT group_concat(b.title, '、')
                             FROM book_words bw
                             JOIN books b ON b.id = bw.book_id
                             WHERE bw.entry_id = e.id), ''),
                   COALESCE((SELECT translation FROM entries WHERE id = $exclude), '')
            FROM entries e
            WHERE e.word_id = $word AND e.id <> $exclude
            ORDER BY e.id;
            """;
        command.Parameters.AddWithValue("$word", wordId);
        command.Parameters.AddWithValue("$exclude", excludeEntryId);

        var result = new List<AlternateEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AlternateEntry(
                reader.GetInt64(0),
                TranslationText.WithoutRepeatedTail(reader.GetString(1), reader.GetString(3)),
                reader.GetString(2)));
        }

        return result;
    }

    private static WordSummary ReadSummary(SqliteDataReader reader)
    {
        var translation = reader.GetString(4);
        var firstLine = translation.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return new WordSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            firstLine,
            reader.GetInt32(5));
    }

    /// <summary>
    /// 把一次查询整个搬到线程池上。
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite 的那些 *Async 方法全是同步实现——SQLite 是本地文件，
    /// 没有真正的异步 I/O。所以 `await repo.SearchAsync(...)` 会把整条查询跑在
    /// 调用线程上，返回的 Task 已经是 completed。在应用里那就是 UI 线程：
    /// 边打字边搜时，每次查询有多少毫秒，输入就卡多少毫秒。
    ///
    /// 之前只把 <c>connection.Open()</c> 包进 Task.Run，靠后面一路
    /// ConfigureAwait(false) 留在线程池上。那样不成立：如果线程池那边在调用线程
    /// 走到 await 之前就开完了连接，await 看到 IsCompleted 就地同步继续——
    /// ConfigureAwait(false) 的语义是「不要求切回原上下文」，不是「强制切走」。
    /// 于是整条查询又落回 UI 线程，只是概率不高。
    ///
    /// 现在整个方法体在 Task.Run 里面：Task.Run 从不在调用线程上执行委托，
    /// 所以「不占用调用线程」从概率变成了保证。
    /// </remarks>
    private static Task<T> Background<T>(Func<Task<T>> work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);

    /// <summary>
    /// 开连接。同步的——调用方已经在 <see cref="Background{T}"/> 的后台任务里了。
    /// </summary>
    private SqliteConnection Open()
    {
        LastQueryThreadId = Environment.CurrentManagedThreadId;
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// 最后一次查询跑在哪个线程上。测试用：它必须不等于调用方的线程。
    /// </summary>
    internal int LastQueryThreadId { get; private set; }

    private static string CreateConnectionString(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("词典数据库不存在，请先运行 LingoDict.Importer。", fullPath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        VerifySchemaVersion(connectionString, fullPath);
        return connectionString;
    }

    /// <summary>
    /// 启动时就把过期的库拦下来。否则用户拿着旧的 dictionary.db 只会看到
    /// "no such column: e.content" 这种没法自己解决的报错。
    /// </summary>
    private static void VerifySchemaVersion(string connectionString, string path)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='schema_version';";

        var version = command.ExecuteScalar() as string;
        if (version != SupportedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"词典数据库版本不匹配（需要 {SupportedSchemaVersion}，实际 {version ?? "未知"}）：{path}。" +
                "请重新运行 LingoDict.Importer 生成。");
        }
    }
}
