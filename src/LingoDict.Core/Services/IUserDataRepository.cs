using LingoDict.Core.Models;

namespace LingoDict.Core.Services;

/// <summary>
/// 可写的用户数据：收藏、查询历史、词条笔记。与只读的内容库分开存放，
/// 因此重新生成 dictionary.db 不会影响用户数据。
/// </summary>
public interface IUserDataRepository
{
    /// <summary>建表并升级架构。其余方法调用前必须先调用一次。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<UserDataStats> GetStatsAsync(CancellationToken cancellationToken = default);

    Task<bool> IsFavoriteAsync(long wordId, CancellationToken cancellationToken = default);

    /// <summary>切换收藏状态，返回切换后的新状态。</summary>
    Task<bool> ToggleFavoriteAsync(IWordReference word, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(
        int limit = 500,
        CancellationToken cancellationToken = default);

    /// <summary>删掉一条收藏。返回是否真的删了（不在收藏里就是 <see langword="false"/>）。</summary>
    Task<bool> RemoveFavoriteAsync(long wordId, CancellationToken cancellationToken = default);

    /// <summary>记录一次查看。同一个词只保留一行，累加次数并刷新时间。</summary>
    Task RecordViewAsync(IWordReference word, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);

    /// <summary>删掉一条历史。返回是否真的删了。</summary>
    Task<bool> RemoveHistoryAsync(long wordId, CancellationToken cancellationToken = default);

    Task ClearHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>这本词书上次看到哪个词；没看过返回 <c>null</c>。</summary>
    Task<BookProgress?> GetBookProgressAsync(string bookId, CancellationToken cancellationToken = default);

    /// <summary>记下这本词书看到哪儿了。一本书只留一行。</summary>
    Task SaveBookProgressAsync(string bookId, IWordReference word, CancellationToken cancellationToken = default);

    Task<AppearanceSettings> GetAppearanceAsync(CancellationToken cancellationToken = default);

    Task SaveAppearanceAsync(AppearanceSettings settings, CancellationToken cancellationToken = default);

    Task<string> GetNoteAsync(long wordId, CancellationToken cancellationToken = default);

    /// <summary>保存笔记。内容为空白时删除该笔记。</summary>
    Task SaveNoteAsync(long wordId, string text, CancellationToken cancellationToken = default);
}
