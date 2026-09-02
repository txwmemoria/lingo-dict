using System.Diagnostics;
using LingoDict.Infrastructure.Data;

namespace LingoDict.Core.Tests;

/// <summary>
/// 边打字边搜时的响应性。
/// </summary>
/// <remarks>
/// 起因是「连续快速输入 trending，打到 i 前后会卡一下」。查下来是两件事叠在一起：
///
///   1. 前缀一旦匹配不上任何词头（"trendi"），就落到子串扫描兜底那条路径，
///      而它原来写成 `normalized LIKE ... OR translation LIKE ...`——OR 让优化器
///      放弃索引，先连上 96876 行 entries 再开窗。冷缓存时近 2 秒。
///   2. Microsoft.Data.Sqlite 的 *Async 全是同步实现，所以那 2 秒是花在**调用线程**
///      上的，也就是 UI 线程。查询多慢，输入就卡多久。
///
/// 两条用例分别钉住这两件事。这些用例需要真实的 artifacts/dictionary.db，
/// 没有就跳过——它们量的是真实数据量下的行为，微型夹具测不出来。
/// </remarks>
public sealed class SearchLatencyTests
{
    private static readonly string? Database = Locate("artifacts/dictionary.db");

    private const string Missing = "缺少 artifacts/dictionary.db，跑 LingoDict.Importer 生成后再测";

    /// <summary>
    /// 是否跑在共享 runner 上（GitHub Actions 等都会设 CI）。
    /// </summary>
    /// <remarks>
    /// 计时阈值只在本地成立。共享 runner 的 CPU 和磁盘都是抢来的，
    /// 本地并行构建时就已经见过搜索从 62ms 抖到 296ms。
    ///
    /// 关掉计时并不等于不设防：这两条真正要抓的「兜底 SQL 退回 OR 写法」
    /// 和「book_words(entry_id) 索引没了」，都由 DictionaryImportTests
    /// 直接查 sqlite_master 确定性地钉住。计时是第二道，用来发现那两个
    /// 之外的退化——那种发现本来也只在稳定的机器上才可信。
    /// 用例其余的正确性断言照跑。
    /// </remarks>
    private static readonly bool OnSharedRunner =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    /// <summary>打了一半、还匹配不上任何词头的英文——边打字边搜时最常经过的状态。</summary>
    private const string NoMatchPrefix = "trendi";

    [SkippableFact]
    public async Task ANonMatchingPrefixDoesNotFallOffACliff()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        var watch = Stopwatch.StartNew();
        var hits = await repo.SearchAsync(NoMatchPrefix, 80);
        watch.Stop();

        Assert.Empty(hits);

        // 这台机器上：修之前热缓存 326ms、冷缓存 1864ms，修之后 62ms。
        // 阈值取 250ms——比修好的值宽 4 倍，又能逮住退回 OR 写法（热缓存就 326ms）。
        // 健康值和悬崖只差 5 倍，共享 runner 的噪声盖得住，所以那里不量。
        if (!OnSharedRunner)
        {
            Assert.True(
                watch.ElapsedMilliseconds < 250,
                $"匹配不上的前缀查了 {watch.ElapsedMilliseconds}ms；子串兜底那条 SQL 八成又退回 OR 写法了");
        }
    }

    [SkippableFact]
    public void QueriesDoNotRunOnTheCallersThread()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);
        repo.SearchAsync("t", 80).GetAwaiter().GetResult();   // 预热，别把首次连接算进来

        var watch = Stopwatch.StartNew();
        var task = repo.SearchAsync(NoMatchPrefix, 80);
        var blocked = watch.Elapsed.TotalMilliseconds;

        // Microsoft.Data.Sqlite 没有真正的异步 I/O，一不小心整条查询就跑在调用线程上，
        // 返回的还是个已完成的 Task。在应用里那就是 UI 线程被占住，打字直接卡。
        Assert.False(task.IsCompleted, "查询在调用线程上同步跑完了——UI 线程会被占住");
        Assert.True(blocked < 20, $"调用方被占住了 {blocked:0}ms");

        task.GetAwaiter().GetResult();

        // 上面两条都是概率性的：只要 Task.Run 那边抢先跑完，await 就会就地
        // 同步继续，查询照样落在调用线程上，而这里恰好没抓到。
        // 确定的那条在 EveryQueryLeavesTheCallersThread 里。
    }

    [SkippableFact]
    public void EveryQueryLeavesTheCallersThreadNotJustSearch()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        // 必须从自己的线程发起：测试方法跑在线程池线程上，await 一让出去，
        // 那个线程就可能被 Task.Run 挑去跑查询，线程号相等但代码是对的。
        DedicatedThread.Run(() =>
        {
            var caller = Environment.CurrentManagedThreadId;

            void Check(Func<Task> call, string what)
            {
                call().GetAwaiter().GetResult();
                Assert.True(caller != repo.LastQueryThreadId, $"{what} 跑在调用线程上");
            }

            // 挨个过一遍公开入口：漏掉哪个包装，哪个就会在 UI 线程上跑 SQL。
            Check(() => repo.GetStatsAsync(), "GetStatsAsync");
            Check(() => repo.SearchAsync("can", 20), "SearchAsync");
            Check(() => repo.SearchPhrasesAsync("call", 8), "SearchPhrasesAsync");
            Check(() => repo.GetBooksAsync(), "GetBooksAsync");
            Check(() => repo.GetWordIndexAsync(), "GetWordIndexAsync");

            var books = repo.GetBooksAsync().GetAwaiter().GetResult();
            if (books.Count > 0)
            {
                Check(() => repo.GetBookWordsAsync(books[0].Id, 20), "GetBookWordsAsync");
            }

            var word = repo.FindByHeadwordAsync("cancel").GetAwaiter().GetResult();
            Assert.True(caller != repo.LastQueryThreadId, "FindByHeadwordAsync 跑在调用线程上");
            if (word is not null)
            {
                Check(() => repo.GetWordAsync(word.Id), "GetWordAsync");
                Check(() => repo.GetAlternateEntriesAsync(word.Id, word.EntryId), "GetAlternateEntriesAsync");
            }
        });
    }

    [SkippableFact]
    public async Task LookingUpAWordsOtherSensesIsCheap()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        // 换词是这个应用里最高频的动作，而每换一次都要问一遍
        // 「这个词的其它释义分别出自哪几本词书」。挑释义最多的那个词来量。
        var busiest = (await repo.SearchAsync("a", 80)).OrderByDescending(x => x.BookCount).First();
        await repo.GetAlternateEntriesAsync(busiest.Id, busiest.EntryId);   // 预热

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            await repo.GetAlternateEntriesAsync(busiest.Id, busiest.EntryId);
        }

        var each = watch.Elapsed.TotalMilliseconds / 10;

        // 没有 book_words(entry_id) 索引时，SQLite 会改成对 81 本书逐本查，
        // 真实库上实测每次 302ms；有索引是 0.11ms。阈值 20ms 留足机器差异。
        if (!OnSharedRunner)
        {
            Assert.True(each < 20, $"取其它释义花了 {each:0.0}ms/次；book_words(entry_id) 索引八成没了");
        }
    }

    [SkippableFact]
    public async Task RealDatabase_AnInflectedFormFindsItsHeadword()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        // 这些以前一无所获：库里的词头是原形，输入变化形式什么也匹配不上。
        foreach (var (typed, expected) in new[] { ("cancelled", "cancel"), ("ran", "run"), ("studying", "study") })
        {
            var hits = await repo.SearchAsync(typed, 20);
            Skip.If(hits.Count == 0, $"{typed} 无结果——这台机器上的词典库可能没接词频数据");
            Assert.Equal(expected, hits[0].Headword);
        }
    }

    [SkippableFact]
    public async Task RealDatabase_BrowsingLeadsWithCommonWordsNotShortOnes()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        var browse = await repo.SearchAsync(string.Empty, 12);

        // 原来纯按长度排，首屏就是 I, a, n, BA, BC, CD, CV, DJ, DP, Dr, HQ, ID
        // ——一整屏两字母缩写，对任何人都没有用。
        //
        // 不能用「短词有几个」来判：of / a / to / it / I 本身就是最常用的词，
        // 它们出现在首屏是对的。要钉的是那批生僻缩写不再冒头。
        Assert.Equal("the", browse[0].Headword);
        Assert.DoesNotContain(browse, x => x.Headword is "BA" or "BC" or "CD" or "CV" or "DJ" or "DP" or "HQ");
    }

    [SkippableFact]
    public async Task RealDatabase_FrequencyDoesNotOutrankRelevance()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        // 词频排在 bm25 之后，不能反过来。off 的词频（205）远高于 cancel（3915），
        // 但搜「取消」时该给 cancel——把词频提到相关度前面就会退化成这样。
        var hits = await repo.SearchAsync("取消", 10);
        var cancel = hits.Select(x => x.Headword).ToList().IndexOf("cancel");
        var off = hits.Select(x => x.Headword).ToList().IndexOf("off");

        Assert.True(cancel >= 0, "搜「取消」没有 cancel：" + string.Join(", ", hits.Select(x => x.Headword)));
        Assert.True(off < 0 || cancel < off, "off 排到了 cancel 前面：" + string.Join(", ", hits.Select(x => x.Headword)));
    }

    [SkippableFact]
    public async Task RealDatabase_AlternateEntriesDropTheRepeatedEnglish()
    {
        Skip.If(Database is null, Missing);
        var repo = new SqliteDictionaryRepository(Database!);

        var word = await repo.FindByHeadwordAsync("abolish");
        Skip.If(word is null, "这份词典库里没有 abolish");

        var detail = await repo.GetWordAsync(word!.Id, word.EntryId);
        var alternates = await repo.GetAlternateEntriesAsync(word.Id, word.EntryId);
        Skip.If(alternates.Count < 2, "abolish 在这份库里只有一条释义");

        // abolish 是截图里那个例子：五本词书给的英文一模一样，
        // 中文写法各不相同（废除 / 彻底废除，废止 / 废止, 废除 / …）。
        // 完整的英文就在上面「释义」里，这一段重复它没有意义。
        var shared = detail!.Translations[0];
        foreach (var alternate in alternates)
        {
            Assert.DoesNotContain("to officially end a law", alternate.Translation, StringComparison.Ordinal);
        }

        Assert.Contains("to officially end a law", shared, StringComparison.Ordinal);

        // 中文那部分必须原样留着——它才是这一段存在的理由
        Assert.Contains(alternates, x => x.Translation.Contains('废', StringComparison.Ordinal));
        Assert.All(alternates, x => Assert.NotEmpty(x.Translation.Trim()));
    }

    private static string? Locate(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relative.Split('/')]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
