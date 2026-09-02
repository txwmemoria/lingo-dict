using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LingoDict.Core.Text;
using LingoDict.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LingoDict.Core.Tests;

/// <summary>
/// 端到端：用一份微型词书源构建数据库，再通过只读仓储查询它。
/// 覆盖归一化去重计数、canonical entry 选择和 FTS5 检索。
/// </summary>
public sealed class DictionaryImportTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingodict-tests", Guid.NewGuid().ToString("N"));
    private ImportResult _result = null!;
    private SqliteDictionaryRepository _repository = null!;

    public Task InitializeAsync()
    {
        var source = Path.Combine(_root, "source");
        var books = Directory.CreateDirectory(Path.Combine(source, "book")).FullName;

        WriteArchive(Path.Combine(books, "TEST_1.zip"), "TEST_1.json",
        [
            Word("TEST_1", 1, "cancel", "取消", sentence: "Please cancel the order.",
                // out 是虚词，the order 带名词——排序应当把后者放前面。
                phrases: ["cancel out", "cancel the order", "shared phrase"]),
            Word("TEST_1", 2, "abruptly", "突然地"),
            // 名词表是从释义的词性前缀反推的，这一条让 order 进表。
            Word("TEST_1", 6, "order", "订单", pos: "n"),
            Word("TEST_1", 3, "long-time", "长期的"),
            // 与上一行归一化后相同，应被合并掉。
            Word("TEST_1", 4, "longtime", "长期的"),
            // 也挂着 "cancel out"：词头不在短语里，归属应当留给 cancel。
            Word("TEST_1", 5, "can", "能够", phrases: ["cancel out", "can do"]),
        ]);

        WriteArchive(Path.Combine(books, "TEST_2.zip"), "TEST_2.json",
        [
            Word("TEST_2", 1, "cancel", "撤销"),
        ]);

        WriteArchive(Path.Combine(books, "TEST_3.zip"), "TEST_3.json",
        [
            // 与 TEST_1 的 cancel 释义完全相同，只多了界面从不读取的字段。
            Word("TEST_3", 1, "cancel", "取消", sentence: "Please cancel the order.",
                phrases: ["cancel out", "cancel the order", "shared phrase"], noisy: true),
        ]);

        File.WriteAllText(
            Path.Combine(source, "bookLists.txt"),
            """
            {"data":{"normalBooksInfo":[
              {"id":"TEST_1","title":"测试词书一","wordNum":6,"tags":[{"tagName":"四级"},{"tagName":"某出版方"}]},
              {"id":"TEST_2","title":"测试词书二","wordNum":1,"tags":[]},
              {"id":"TEST_3","title":"测试词书三","wordNum":1,"tags":[]}
            ]}}
            """,
            Encoding.UTF8);

        var output = Path.Combine(_root, "dictionary.db");
        _result = new DictionaryDatabaseBuilder().Build(source, output);
        _repository = new SqliteDictionaryRepository(output);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void Build_StoresContentAsCompressedBlobWithoutImportOnlyColumns()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "dictionary.db")};Mode=ReadOnly");
        connection.Open();

        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT name, type FROM pragma_table_info('entries') ORDER BY cid;";
        var schema = new List<(string Name, string Type)>();
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                schema.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.Contains(("content", "BLOB"), schema);

        // 这两列只在导入期有用，不该出现在发布的库里。
        Assert.DoesNotContain(schema, x => x.Name is "content_json" or "content_hash" or "richness_score");

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        Assert.Equal("6", version.ExecuteScalar());
    }

    [Fact]
    public void TheIndexesQueriesDependOnAreAllPresent()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "dictionary.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                indexes.Add(reader.GetString(0));
            }
        }

        // 少哪一条都不会报错，只会让某个界面慢下来——而慢多少和数据量有关，
        // 微型夹具上根本量不出来。所以这里只查「在不在」。
        //
        // ix_book_words_entry_id 是代价最大的一条：详情页要按 entry_id 反查
        // 「这条释义出自哪几本词书」，没有它 SQLite 会改成对 81 本书逐本查，
        // 每换一个词重来一遍。真实库上实测 302ms → 0.33ms。
        string[] required =
        [
            "ix_words_headword",
            "ix_words_browse",
            "ix_entries_word_id",
            "ix_book_words_word_id",
            "ix_book_words_book_rank",
            "ix_book_words_entry_id",
            "ix_phrases_word",
        ];

        var missing = required.Where(x => !indexes.Contains(x)).ToArray();
        Assert.True(missing.Length == 0, $"缺索引：{string.Join(", ", missing)}");
    }

    [Fact]
    public void OpeningAnOutdatedDatabase_FailsWithAnActionableMessage()
    {
        var stale = Path.Combine(_root, "stale.db");
        using (var connection = new SqliteConnection($"Data Source={stale}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);" +
                                 "INSERT INTO meta VALUES('schema_version','1');";
            create.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var error = Assert.Throws<InvalidOperationException>(() => new SqliteDictionaryRepository(stale));

        Assert.Contains("版本不匹配", error.Message, StringComparison.Ordinal);
        Assert.Contains("LingoDict.Importer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DeduplicatesEntriesThatDifferOnlyInUnusedFields()
    {
        // TEST_1 和 TEST_3 的 cancel 只有 sSpeech 这种界面从不读的字段不同，应合并成一条。
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "dictionary.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM entries e
            JOIN words w ON w.id = e.word_id
            WHERE w.normalized = 'cancel';
            """;

        // 取消在 TEST_1/TEST_3（同一份释义）和 TEST_2（不同释义）里出现，共 2 条。
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public void Build_SeparatesRowsReadFromMembershipsWritten()
    {
        Assert.Equal(3, _result.BookCount);
        Assert.Equal(8, _result.RowCount);          // 读入 8 行 JSON
        Assert.Equal(7, _result.MembershipCount);   // long-time / longtime 合并成一行
        Assert.Equal(5, _result.WordCount);
    }

    [Fact]
    public async Task GetStatsAsync_MatchesWhatWasWritten()
    {
        var stats = await _repository.GetStatsAsync();

        Assert.Equal(_result.WordCount, stats.WordCount);
        Assert.Equal(_result.BookCount, stats.BookCount);
        Assert.Equal(_result.MembershipCount, stats.MembershipCount);
    }

    [Fact]
    public async Task Build_PicksRichestEntryAsCanonical()
    {
        // cancel 在两本词书里有不同释义，带例句的那条更"厚"，应当胜出。
        var cancel = Assert.Single(await _repository.SearchAsync("cancel"), x => x.Headword == "cancel");

        Assert.Contains("取消", cancel.Translation, StringComparison.Ordinal);
        Assert.Equal(3, cancel.BookCount);
    }

    [Fact]
    public async Task SearchAsync_RanksExactHeadwordAboveOtherPrefixMatches()
    {
        var results = await _repository.SearchAsync("can");

        Assert.Equal("can", results[0].Headword);
        Assert.Contains(results, x => x.Headword == "cancel");
    }

    [Fact]
    public async Task SearchAsync_FindsPartialChineseInsideTranslation()
    {
        // "突然" 是 "突然地" 的前两个字，逐字切分后才能命中。
        var results = await _repository.SearchAsync("突然");

        Assert.Equal("abruptly", Assert.Single(results).Headword);
    }

    [Fact]
    public async Task SearchAsync_ReturnsTheEntryThatActuallyMatched()
    {
        // "撤销" 只出现在 TEST_2 那条非首选释义里。搜到它却显示"取消"、
        // 打开也看不到"撤销"，用户就无从知道为什么这条会出现在结果里。
        var hit = Assert.Single(await _repository.SearchAsync("撤销"));

        Assert.Equal("cancel", hit.Headword);
        Assert.Contains("撤销", hit.Translation, StringComparison.Ordinal);

        var detail = await _repository.GetWordAsync(hit.Id, hit.EntryId);
        Assert.Contains("v 撤销", detail!.Translations);
    }

    [Fact]
    public async Task GetAlternateEntriesAsync_SurfacesTheOtherBooksReading()
    {
        var hit = Assert.Single(await _repository.SearchAsync("撤销"));

        var others = await _repository.GetAlternateEntriesAsync(hit.Id, hit.EntryId);

        var canonical = Assert.Single(others);
        Assert.Contains("取消", canonical.Translation, StringComparison.Ordinal);

        // 顺带说明这条释义出自哪几本词书——那正是它们彼此不同的原因。
        Assert.Contains("测试词书一", canonical.Books, StringComparison.Ordinal);
        Assert.Contains("测试词书三", canonical.Books, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAlternateEntriesAsync_IsEmptyForWordsWithASingleReading()
    {
        var hit = Assert.Single(await _repository.SearchAsync("突然"));

        Assert.Empty(await _repository.GetAlternateEntriesAsync(hit.Id, hit.EntryId));
    }

    [Fact]
    public async Task SearchAsync_StillReturnsTheCanonicalEntryWhenItIsTheMatch()
    {
        var hit = Assert.Single(await _repository.SearchAsync("取消"), x => x.Headword == "cancel");

        Assert.Contains("取消", hit.Translation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ReturnsOneRowPerWordEvenWhenSeveralEntriesMatch()
    {
        // cancel 的两条释义都含"v"开头的词性标记，但结果里只应出现一次。
        var results = await _repository.SearchAsync("cancel");

        Assert.Single(results, x => x.Headword == "cancel");
    }

    [Fact]
    public void Build_LeavesTheOldDatabaseIntactWhenImportFails()
    {
        var output = Path.Combine(_root, "guarded.db");
        var good = Path.Combine(_root, "source");
        new DictionaryDatabaseBuilder().Build(good, output);
        var originalSize = new FileInfo(output).Length;
        Assert.True(originalSize > 0);

        // 空词书目录会让导入抛错，此时旧库必须原封不动，且不留下临时文件。
        var broken = Directory.CreateDirectory(Path.Combine(_root, "broken", "book")).FullName;
        Assert.ThrowsAny<Exception>(() =>
            new DictionaryDatabaseBuilder().Build(Path.Combine(_root, "broken"), output));

        Assert.Equal(originalSize, new FileInfo(output).Length);
        Assert.False(File.Exists(output + ".importing"));
        Assert.NotNull(broken);
    }

    [Fact]
    public async Task SearchAsync_ReturnsNothingForUnknownTerms()
    {
        Assert.Empty(await _repository.SearchAsync("zzzzz"));
        Assert.Empty(await _repository.SearchAsync("完全不相干的词"));
    }

    [Fact]
    public async Task SearchAsync_TreatsFtsOperatorsAsPlainText()
    {
        // 未转义时 '"' 或 NEAR( 会让 FTS5 抛语法错误。
        Assert.Empty(await _repository.SearchAsync("\" OR headword:"));
        Assert.Empty(await _repository.SearchAsync("NEAR(zzz yyy)"));
        Assert.Empty(await _repository.SearchAsync("100%"));
    }

    [Fact]
    public async Task SearchAsync_WithEmptyQueryBrowsesShortestFirst()
    {
        var results = await _repository.SearchAsync(string.Empty);

        Assert.Equal(5, results.Count);
        Assert.Equal("can", results[0].Headword);
    }

    [Fact]
    public async Task GetBookWordsAsync_ReturnsDeduplicatedWordsInRankOrder()
    {
        var words = await _repository.GetBookWordsAsync("TEST_1");

        Assert.Equal(["cancel", "abruptly", "long-time", "can", "order"], words.Select(x => x.Headword));
    }

    [Fact]
    public async Task GetBooksAsync_UsesTitlesFromBookLists()
    {
        var books = await _repository.GetBooksAsync();

        Assert.Equal(["测试词书一", "测试词书三", "测试词书二"], books.Select(x => x.Title));
        Assert.Equal(5, books.Single(x => x.Id == "TEST_1").WordCount);

        // 标签只留说明内容的分类。源数据里混着提供方的名字，那是「谁做的」
        // 而不是「这本书是什么」，白名单认不出来的一律丢掉——
        // 用黑名单的话每个品牌名都得写进源码，而且新出现一个就漏一个。
        Assert.Equal("四级", books.Single(x => x.Id == "TEST_1").Tags);
    }

    [Fact]
    public async Task GetWordAsync_ReturnsDetailWithEverySourceBook()
    {
        var summary = Assert.Single(await _repository.SearchAsync("cancel"), x => x.Headword == "cancel");

        var detail = await _repository.GetWordAsync(summary.Id, summary.EntryId);

        Assert.NotNull(detail);
        Assert.Equal("cancel", detail!.Headword);
        Assert.Equal(["v 取消"], detail.Translations);
                var sentence = Assert.Single(detail.Sentences);
        Assert.Equal("Please cancel the order.", sentence.Text);
        Assert.Equal("cancel", sentence.Emphasis);
        Assert.Equal(["测试词书一", "测试词书三", "测试词书二"], detail.SourceBooks);
    }

    [Fact]
    public async Task GetWordAsync_ReturnsNullForUnknownWord()
    {
        Assert.Null(await _repository.GetWordAsync(999_999));
    }

    private static void WriteArchive(string archivePath, string entryName, string[] lines)
    {
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = archive.CreateEntry(entryName).Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(false));
        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }
    }

    private static string Word(
        string bookId,
        int rank,
        string headword,
        string translation,
        string pos = "v",
        string? sentence = null,
        string[]? phrases = null,
        bool noisy = false)
    {
        var content = new Dictionary<string, object>
        {
            ["ukphone"] = $"uk-{headword}",
            ["usphone"] = $"us-{headword}",
            ["trans"] = new[] { new { tranCn = translation, pos } },
        };

        if (phrases is not null)
        {
            content["phrase"] = new
            {
                phrases = phrases.Select(x => new { pContent = x, pCn = $"{x} 的中文" }).ToArray(),
            };
        }

        if (sentence is not null)
        {
            content["sentence"] = new
            {
                sentences = new[]
                {
                    new
                    {
                        sContent = sentence,
                        sContent_eng = sentence.Replace("cancel", "<b>cancel</b>", StringComparison.Ordinal),
                        sCn = "例句译文。",
                    },
                },
            };
        }

        if (noisy)
        {
            // 抽取时会被丢掉的字段，不应影响去重。
            content["ukspeech"] = $"{headword}&type=1";
            content["usspeech"] = $"{headword}&type=2";
            content["star"] = 3;
            content["speech"] = headword;
        }

        return JsonSerializer.Serialize(new
        {
            wordRank = rank,
            headWord = headword,
            bookId,
            content = new { word = new { wordHead = headword, content } },
        });
    }
}
