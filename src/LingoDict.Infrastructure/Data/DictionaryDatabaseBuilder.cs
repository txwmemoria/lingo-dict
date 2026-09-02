using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LingoDict.Core.Text;
using Microsoft.Data.Sqlite;

namespace LingoDict.Infrastructure.Data;

public sealed record ImportProgress(int CompletedBooks, int TotalBooks, int ImportedRows, string CurrentBook);

/// <summary>
/// <paramref name="RowCount"/> 是读入的 JSON 行数，<paramref name="MembershipCount"/> 是实际落库的
/// book_words 行数。两者之差是同一本词书里归一化后撞车的词头（如 long-time / longtime）。
/// </summary>
public sealed record ImportResult(
    int BookCount,
    int RowCount,
    int MembershipCount,
    int WordCount,
    long DatabaseBytes,
    TimeSpan Duration);

public sealed class DictionaryDatabaseBuilder
{
    /// <summary>
    /// 先写临时文件，全部完成后再原子替换目标。导入要跑一分多钟，
    /// 中途失败（数据损坏、磁盘满、取消）不能把上一份能用的词典毁掉。
    /// </summary>
    public ImportResult Build(
        string sourceRoot,
        string outputPath,
        Action<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var finalPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // 放在同一目录，保证 File.Move 是同卷内的原子重命名。
        var stagingPath = finalPath + ".importing";
        Delete(stagingPath);

        try
        {
            var result = BuildInto(sourceRoot, stagingPath, progress, cancellationToken);
            File.Move(stagingPath, finalPath, overwrite: true);
            return result with { DatabaseBytes = new FileInfo(finalPath).Length };
        }
        catch
        {
            Delete(stagingPath);
            throw;
        }
    }

    private static void Delete(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Delete(path + suffix);
            }
        }
    }

    private static ImportResult BuildInto(
        string sourceRoot,
        string outputPath,
        Action<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var bookDirectory = Path.Combine(sourceRoot, "book");

        // 词书 ZIP 不入版本库，新克隆的仓库必然走到这两条分支之一（git 不跟踪空目录，
        // 所以先撞上的通常是"目录不存在"），把办法直接写在报错里。
        const string Hint = "请先运行 python data/kajweb-dict/download.py 下载源数据"
            + "（见 README「获取词书源数据」）。";

        if (!Directory.Exists(bookDirectory))
        {
            throw new DirectoryNotFoundException($"词书目录不存在：{bookDirectory}。{Hint}");
        }

        var archives = Directory.GetFiles(bookDirectory, "*.zip").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (archives.Length == 0)
        {
            throw new InvalidOperationException($"词书目录中没有 ZIP 文件：{bookDirectory}。{Hint}");
        }

        var metadata = LoadBookMetadata(Path.Combine(sourceRoot, "bookLists.txt"));

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,

            // 关掉池化，否则文件句柄可能在 Close 之后仍被持有，导致随后的 Move 失败。
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        ConfigureForImport(connection);
        CreateSchema(connection);

        using var transaction = connection.BeginTransaction();
        using var commands = new ImportCommands(connection, transaction);
        var wordIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var entryIds = new Dictionary<(long WordId, string Hash), long>();
        var insertedBooks = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = 0;
        var membershipCount = 0;

        for (var archiveIndex = 0; archiveIndex < archives.Length; archiveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = archives[archiveIndex];
            progress?.Invoke(new ImportProgress(archiveIndex, archives.Length, rowCount, Path.GetFileName(archivePath)));

            using var archive = ZipFile.OpenRead(archivePath);
            var jsonEntry = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"压缩包中没有 JSON 文件：{archivePath}");
            using var stream = jsonEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var headword = root.GetProperty("headWord").GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(headword))
                {
                    continue;
                }

                var bookId = root.GetProperty("bookId").GetString() ?? Path.GetFileNameWithoutExtension(archivePath);
                var rank = root.TryGetProperty("wordRank", out var rankElement) && rankElement.TryGetInt32(out var parsedRank)
                    ? parsedRank
                    : rowCount + 1;

                if (insertedBooks.Add(bookId))
                {
                    metadata.TryGetValue(bookId, out var book);
                    commands.InsertBook(
                        bookId,
                        book?.Title ?? bookId,
                        book?.Tags ?? string.Empty,
                        book?.WordCount ?? 0,
                        Path.GetFileName(archivePath));
                }

                var normalized = WordNormalizer.Normalize(headword);
                if (!wordIds.TryGetValue(normalized, out var wordId))
                {
                    wordId = commands.InsertWord(headword, normalized);
                    wordIds.Add(normalized, wordId);
                }

                var content = root.GetProperty("content").GetProperty("word").GetProperty("content");
                var contentJson = content.GetRawText();
                var extracted = ContentExtractor.Extract(content, headword);
                var payload = ContentCodec.Serialize(extracted);

                // 按"抽取后的正文 + 音标"去重，而不是原始 JSON。原始 JSON 里
                // sSpeech/star 之类的噪声字段会让同一份释义在不同词书里算作不同条目。
                var contentHash = Convert.ToHexString(SHA256.HashData(
                    [.. payload, .. Encoding.UTF8.GetBytes(extracted.UkPhone), .. Encoding.UTF8.GetBytes(extracted.UsPhone)]));

                if (!entryIds.TryGetValue((wordId, contentHash), out var entryId))
                {
                    entryId = commands.InsertEntry(
                        wordId,
                        extracted.UkPhone,
                        extracted.UsPhone,
                        extracted.Translation,
                        ContentCodec.Compress(payload),
                        CalculateRichness(contentJson, extracted));
                    entryIds.Add((wordId, contentHash), entryId);
                }

                if (commands.InsertBookWord(bookId, wordId, entryId, rank))
                {
                    membershipCount++;
                }

                rowCount++;
            }

            progress?.Invoke(new ImportProgress(archiveIndex + 1, archives.Length, rowCount, Path.GetFileName(archivePath)));
        }

        commands.SelectCanonicalEntries();
        transaction.Commit();
        CreateSearchIndex(connection);
        CreatePhraseIndex(connection);
        DropImportOnlyColumns(connection);
        FinalizeDatabase(connection, insertedBooks.Count, membershipCount, wordIds.Count);
        connection.Close();

        return new ImportResult(
            insertedBooks.Count,
            rowCount,
            membershipCount,
            wordIds.Count,
            new FileInfo(outputPath).Length,
            DateTimeOffset.UtcNow - startedAt);
    }

    private static int CalculateRichness(string json, ContentExtractor.ExtractedContent content) =>
        json.Length +
        content.Translations.Count * 200 +
        content.Sentences.Count * 100 +
        content.Phrases.Count * 80 +
        content.Synonyms.Count * 60 +
        content.RelatedWords.Count * 40;

    private static Dictionary<string, BookMetadata> LoadBookMetadata(string path)
    {
        var result = new Dictionary<string, BookMetadata>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("normalBooksInfo", out var books))
        {
            return result;
        }

        foreach (var item in books.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var tags = item.TryGetProperty("tags", out var tagItems)
                ? string.Join(" · ", tagItems.EnumerateArray()
                    .Select(x => x.TryGetProperty("tagName", out var tag) ? tag.GetString() : null)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && KeptTags.Contains(x!)))
                : string.Empty;
            result[id] = new BookMetadata(
                item.TryGetProperty("title", out var title) ? title.GetString() ?? id : id,
                tags,
                item.TryGetProperty("wordNum", out var wordCount) ? wordCount.GetInt32() : 0);
        }

        return result;
    }

    private static void ConfigureForImport(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE books(
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                tags TEXT NOT NULL,
                declared_word_count INTEGER NOT NULL,
                archive_name TEXT NOT NULL
            );
            CREATE TABLE words(
                id INTEGER PRIMARY KEY,
                headword TEXT NOT NULL,
                normalized TEXT NOT NULL UNIQUE,
                canonical_entry_id INTEGER
            );
            CREATE TABLE entries(
                id INTEGER PRIMARY KEY,
                word_id INTEGER NOT NULL REFERENCES words(id),
                uk_phone TEXT NOT NULL,
                us_phone TEXT NOT NULL,
                translation TEXT NOT NULL,
                content BLOB NOT NULL,
                richness_score INTEGER NOT NULL
            );
            CREATE TABLE phrases(
                id INTEGER PRIMARY KEY,
                word_id INTEGER NOT NULL REFERENCES words(id),
                text TEXT NOT NULL,
                translation TEXT NOT NULL,
                rank INTEGER NOT NULL
            );
            CREATE TABLE book_words(
                book_id TEXT NOT NULL REFERENCES books(id),
                word_id INTEGER NOT NULL REFERENCES words(id),
                entry_id INTEGER NOT NULL REFERENCES entries(id),
                rank INTEGER NOT NULL,
                PRIMARY KEY(book_id, word_id)
            );
            CREATE INDEX ix_words_headword ON words(headword COLLATE NOCASE);

            -- 首屏的浏览列表按「短词优先」排序。没有这条表达式索引就要对两万三千行
            -- 全表扫描加排序（实测 91ms），有了它是 0.3ms，代价 0.4 MiB。
            CREATE INDEX ix_words_browse ON words(length(headword), headword COLLATE NOCASE);
            CREATE INDEX ix_entries_word_id ON entries(word_id);
            CREATE INDEX ix_book_words_word_id ON book_words(word_id);
            CREATE INDEX ix_book_words_book_rank ON book_words(book_id, rank);

            -- 详情页要标出「这条释义出自哪几本词书」，也就是按 entry_id 反查 book_words。
            -- 没有这条索引时 SQLite 只能把连接反过来做：对 81 本书各查一次，
            -- 每换一个词就重复一遍——实测释义多的词要 302ms，有了它是 0.33ms，代价 1.7 MiB。
            -- 换词是这个应用里最高频的动作（词书里上一个/下一个、点搜索结果），
            -- 所以这一条的收益比上面几条加起来都大。
            CREATE INDEX ix_book_words_entry_id ON book_words(entry_id);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 建立 FTS5 索引，一行对应一个 entry（rowid 即 entry id），这样搜索能定位到
    /// 究竟是哪一条释义命中的，详情页直接打开它——否则搜"撤销"却只看到"取消"。
    /// 词头只写在 canonical 那一行，避免同一个词的词头被重复索引多次。
    /// 写入前按 <see cref="SearchText.Segment"/> 逐字切分汉字。
    /// </summary>
    private static void CreateSearchIndex(SqliteConnection connection)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE VIRTUAL TABLE words_fts USING fts5(
                    headword,
                    translation,
                    content=''
                );
                """;
            create.ExecuteNonQuery();
        }

        var rows = new List<(long Id, string Headword, string Translation)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT e.id,
                       CASE WHEN e.id = w.canonical_entry_id THEN w.headword ELSE '' END,
                       e.translation
                FROM entries e
                JOIN words w ON w.id = e.word_id;
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        using (var transaction = connection.BeginTransaction())
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO words_fts(rowid, headword, translation) VALUES($id,$headword,$translation);";
                var id = insert.Parameters.Add("$id", SqliteType.Integer);
                var headword = insert.Parameters.Add("$headword", SqliteType.Text);
                var translation = insert.Parameters.Add("$translation", SqliteType.Text);
                foreach (var row in rows)
                {
                    id.Value = row.Id;
                    headword.Value = row.Headword;
                    translation.Value = SearchText.Segment(row.Translation);
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        using var optimize = connection.CreateCommand();
        optimize.CommandText = "INSERT INTO words_fts(words_fts) VALUES('optimize');";
        optimize.ExecuteNonQuery();
    }

    /// <summary>
    /// 短语单独建一份可检索的表。
    ///
    /// 这批数据本来就在 entries.content 里（详情页的「短语」一节），但 words_fts
    /// 只索引了词头和释义，所以搜「大雨」只能命中 downpour，够不到 rain 词条下
    /// 那条 heavy rain=大雨——而后者恰恰是中文母语者最需要的：不给搭配，
    /// 查了 heavy=重的、rain=雨，照样会拼出 big rain。
    ///
    /// 同一条短语会挂在多个词条下（实测 54.8 万条出现、去重后 5.3 万条），
    /// 归属取词头出现在短语里的那个，"heavy rain" 才会归给 rain 而不是 heavy。
    ///
    /// rank 决定"典型搭配"里先展示谁。这一列很关键：按长度排的话，搜「看」时
    /// see/look/watch/read 会分别给出 see as / look in / watch for / read on——
    /// 全是虚词搭配，区分度为零。带实词的（spend money、read the newspaper、
    /// talk business）才说明这个词跟什么搭。
    /// </summary>
    private static void CreatePhraseIndex(SqliteConnection connection)
    {
        var nouns = ReadNounLexicon(connection);
        var seen = new Dictionary<(string Text, string Translation), (long WordId, bool Owned, string Head)>();

        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT e.word_id, w.normalized, e.content FROM entries e JOIN words w ON w.id = e.word_id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var wordId = reader.GetInt64(0);
                var normalized = reader.GetString(1);
                var decoded = ContentCodec.Decode((byte[])reader[2]);

                foreach (var phrase in decoded.Phrases)
                {
                    if (phrase.Primary.Length == 0 || phrase.Secondary.Length == 0)
                    {
                        continue;
                    }

                    var key = (phrase.Primary, phrase.Secondary);
                    var owned = SearchText.SplitWords(phrase.Primary)
                        .Any(x => x.IsWord && WordNormalizer.Normalize(x.Text) == normalized);

                    // 已有归属且已经是"词头在短语里"的，就不再替换
                    if (!seen.TryGetValue(key, out var existing) || (owned && !existing.Owned))
                    {
                        seen[key] = (wordId, owned, normalized);
                    }
                }
            }
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE VIRTUAL TABLE phrases_fts USING fts5(
                    text,
                    translation,
                    content=''
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO phrases(id, word_id, text, translation, rank)
                        VALUES($id,$word,$text,$cn,$rank);
                    INSERT INTO phrases_fts(rowid, text, translation) VALUES($id,$text,$segmented);
                    """;
                var id = insert.Parameters.Add("$id", SqliteType.Integer);
                var word = insert.Parameters.Add("$word", SqliteType.Integer);
                var text = insert.Parameters.Add("$text", SqliteType.Text);
                var cn = insert.Parameters.Add("$cn", SqliteType.Text);
                var rank = insert.Parameters.Add("$rank", SqliteType.Integer);
                var segmented = insert.Parameters.Add("$segmented", SqliteType.Text);

                var next = 1L;
                foreach (var ((phrase, translation), owner) in seen)
                {
                    id.Value = next++;
                    word.Value = owner.WordId;
                    text.Value = phrase;
                    cn.Value = translation;
                    rank.Value = RankPhrase(phrase, owner.Head, nouns);
                    segmented.Value = SearchText.Segment(translation);
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX ix_phrases_word ON phrases(word_id, rank, length(text));";
            index.ExecuteNonQuery();
        }

        using var optimize = connection.CreateCommand();
        optimize.CommandText = "INSERT INTO phrases_fts(phrases_fts) VALUES('optimize');";
        optimize.ExecuteNonQuery();
    }

    /// <summary>
    /// 从释义里反推名词表——释义行本来就以词性开头（"n 取消"）。
    /// 只为给搭配排序用，不需要多准。
    /// </summary>
    private static HashSet<string> ReadNounLexicon(SqliteConnection connection)
    {
        const char NewLine = (char)10;

        var nouns = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT w.normalized, e.translation FROM entries e JOIN words w ON w.id = e.word_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            foreach (var line in reader.GetString(1).Split(NewLine))
            {
                if (line.StartsWith("n ", StringComparison.Ordinal))
                {
                    nouns.Add(reader.GetString(0));
                    break;
                }
            }
        }

        return nouns;
    }

    /// <summary>介词和小品词——只由它们构成的搭配（see as、look in）对辨析没有帮助。</summary>
    private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
    {
        "in", "on", "at", "of", "up", "out", "off", "for", "as", "to", "down", "over",
        "about", "with", "from", "by", "away", "back", "into", "onto", "through",
        "around", "along", "across", "after", "before", "against", "upon", "the", "a", "an",
    };

    /// <summary>越小越先展示：0 = 带名词，1 = 带其它实词，2 = 只有虚词。</summary>
    private static int RankPhrase(string phrase, string headNormalized, HashSet<string> nouns)
    {
        var others = SearchText.SplitWords(phrase)
            .Where(x => x.IsWord)
            .Select(x => WordNormalizer.Normalize(x.Text))
            .Where(x => x.Length > 0 && x != headNormalized)
            .ToList();

        if (others.Count == 0 || others.All(Particles.Contains))
        {
            return 2;
        }

        return others.Any(nouns.Contains) ? 0 : 1;
    }

    /// <summary>
    /// richness_score 只用来挑 canonical entry，选完就没用了。查询路径一次都不碰它，
    /// 但它在 11 万行上要占几百 KB，连同行溢出一起随 VACUUM 回收。
    /// </summary>
    private static void DropImportOnlyColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE entries DROP COLUMN richness_score;";
        command.ExecuteNonQuery();
    }

    private static void FinalizeDatabase(SqliteConnection connection, int books, int memberships, int words)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO meta(key, value) VALUES
                    ('schema_version', '6'),
                    ('generated_at_utc', $generated),
                    ('book_count', $books),
                    ('membership_count', $rows),
                    ('word_count', $words);
                """;
            command.Parameters.AddWithValue("$generated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$books", books.ToString());
            command.Parameters.AddWithValue("$rows", memberships.ToString());
            command.Parameters.AddWithValue("$words", words.ToString());
            command.ExecuteNonQuery();
        }

        using var optimize = connection.CreateCommand();
        optimize.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=NORMAL; ANALYZE; VACUUM;";
        optimize.ExecuteNonQuery();
    }

    /// <summary>
    /// 允许进库的词书标签：考试等级和教材版本。
    /// </summary>
    /// <remarks>
    /// 源数据的 tags 里混着两类东西——一类说明这本书**是什么内容**
    /// （四级、考研、IELTS、人教版），一类只是**谁做的**。
    /// 后者对使用者没有意义，而且这份词书数据只是本地自用的输入，
    /// 没有理由把提供方的品牌带进我们生成的数据库。
    ///
    /// 用白名单而不是黑名单：黑名单要把每个品牌名写进源码（正是不想要的），
    /// 而且以后源数据多一个品牌就漏一个。白名单只认得内容分类，其余一律丢。
    /// </remarks>
    private static readonly HashSet<string> KeptTags = new(StringComparer.Ordinal)
    {
        "四级", "六级", "专四", "专八", "考研", "中考", "高考",
        "IELTS", "TOEFL", "GRE", "GMAT", "SAT", "BEC",
        "人教版", "北师版", "外研社版",
    };

    private sealed record BookMetadata(string Title, string Tags, int WordCount);

    private sealed class ImportCommands : IDisposable
    {
        private readonly SqliteCommand _insertBook;
        private readonly SqliteCommand _insertWord;
        private readonly SqliteCommand _insertEntry;
        private readonly SqliteCommand _insertBookWord;
        private readonly SqliteCommand _selectCanonical;

        public ImportCommands(SqliteConnection connection, SqliteTransaction transaction)
        {
            _insertBook = Create(connection, transaction, "INSERT OR IGNORE INTO books(id,title,tags,declared_word_count,archive_name) VALUES($id,$title,$tags,$count,$archive);");
            _insertWord = Create(connection, transaction, "INSERT INTO words(headword,normalized) VALUES($headword,$normalized) RETURNING id;");
            _insertEntry = Create(connection, transaction, "INSERT INTO entries(word_id,uk_phone,us_phone,translation,content,richness_score) VALUES($word,$uk,$us,$translation,$content,$score) RETURNING id;");
            // OR IGNORE：同一本词书里归一化后相同的词头保留首次出现的排序，并让调用方能数出实际落库行数。
            _insertBookWord = Create(connection, transaction, "INSERT OR IGNORE INTO book_words(book_id,word_id,entry_id,rank) VALUES($book,$word,$entry,$rank);");
            _selectCanonical = Create(connection, transaction, "UPDATE words SET canonical_entry_id=(SELECT id FROM entries WHERE entries.word_id=words.id ORDER BY richness_score DESC, id LIMIT 1);");
        }

        public void InsertBook(string id, string title, string tags, int count, string archive)
        {
            Set(_insertBook, ("$id", id), ("$title", title), ("$tags", tags), ("$count", count), ("$archive", archive));
            _insertBook.ExecuteNonQuery();
        }

        public long InsertWord(string headword, string normalized)
        {
            Set(_insertWord, ("$headword", headword), ("$normalized", normalized));
            return (long)_insertWord.ExecuteScalar()!;
        }

        public long InsertEntry(long wordId, string uk, string us, string translation, byte[] content, int score)
        {
            Set(_insertEntry, ("$word", wordId), ("$uk", uk), ("$us", us), ("$translation", translation), ("$content", content), ("$score", score));
            return (long)_insertEntry.ExecuteScalar()!;
        }

        /// <returns>真表示新插入了一行；假表示该词书里已有同一个归一化词头。</returns>
        public bool InsertBookWord(string bookId, long wordId, long entryId, int rank)
        {
            Set(_insertBookWord, ("$book", bookId), ("$word", wordId), ("$entry", entryId), ("$rank", rank));
            return _insertBookWord.ExecuteNonQuery() > 0;
        }

        public void SelectCanonicalEntries() => _selectCanonical.ExecuteNonQuery();

        public void Dispose()
        {
            _insertBook.Dispose();
            _insertWord.Dispose();
            _insertEntry.Dispose();
            _insertBookWord.Dispose();
            _selectCanonical.Dispose();
        }

        private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction transaction, string sql) =>
            new(sql, connection, transaction);

        private static void Set(SqliteCommand command, params (string Name, object Value)[] values)
        {
            command.Parameters.Clear();
            foreach (var (name, value) in values)
            {
                command.Parameters.AddWithValue(name, value);
            }
        }
    }
}
