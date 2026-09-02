using LingoDict.Core.Models;

namespace LingoDict.Core.Services;

public interface IDictionaryRepository
{
    Task<DictionaryStats> GetStatsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WordSummary>> SearchAsync(
        string query,
        int limit = 80,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜短语。中文母语者常见的错误是逐词翻译（大雨→big rain），
    /// 词义对应救不了，得让搭配本身可检索。
    /// </summary>
    Task<IReadOnlyList<PhraseHit>> SearchPhrasesAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WordSummary>> GetBookWordsAsync(
        string bookId,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<WordDetail?> GetWordAsync(
        long wordId,
        long? entryId = null,
        CancellationToken cancellationToken = default);

    /// <summary>按词头精确查找（归一化后比较），用于同义词、同根词之间的跳转。</summary>
    Task<WordSummary?> FindByHeadwordAsync(
        string headword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 全部词头（键为归一化形式）及其极简信息。界面用它判断例句里的词能不能查到，
    /// 并就地做悬停预览——两万多条一次读进内存，就不必每次回数据库。
    /// </summary>
    Task<IReadOnlyDictionary<string, WordPreview>> GetWordIndexAsync(
        CancellationToken cancellationToken = default);

    /// <summary>取同一词头下除 <paramref name="excludeEntryId"/> 之外的其他释义。</summary>
    Task<IReadOnlyList<AlternateEntry>> GetAlternateEntriesAsync(
        long wordId,
        long excludeEntryId,
        CancellationToken cancellationToken = default);
}
