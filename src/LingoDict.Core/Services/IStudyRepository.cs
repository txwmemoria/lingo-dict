using LingoDict.Core.Models;

namespace LingoDict.Core.Services;

/// <summary>
/// 间隔重复的卡片存储。与收藏/历史/笔记共用同一个用户库文件。
/// </summary>
public interface IStudyRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> IsInStudyAsync(long wordId, CancellationToken cancellationToken = default);

    Task<StudyCard?> GetCardAsync(long wordId, CancellationToken cancellationToken = default);

    /// <summary>批量加入学习计划。已在计划里的词会跳过，不会重置其进度。</summary>
    /// <returns>本次真正新增的卡片数。</returns>
    Task<int> AddCardsAsync(
        IEnumerable<IWordReference> words,
        string? bookId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveCardAsync(long wordId, CancellationToken cancellationToken = default);

    /// <summary>取出全部到期卡片（不受每日上限约束），最早到期的排前面。</summary>
    Task<IReadOnlyList<StudyCard>> GetDueCardsAsync(
        DateTimeOffset now,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 组装今天该学的队列：先到期复习、再放新词，两者分别受 <see cref="StudySettings"/> 约束。
    /// 今天已经看过的卡片如果又到期（评了"重来"），一定会回到队列且不再占用配额。
    /// <paramref name="bookId"/> 非空时只取该词书加入的卡片。
    /// </summary>
    Task<IReadOnlyList<StudyCard>> BuildDailyQueueAsync(
        DateTimeOffset now,
        string? bookId = null,
        CancellationToken cancellationToken = default);

    /// <summary>学习计划里出现过的词书，按卡片数倒序。</summary>
    Task<IReadOnlyList<StudyBook>> GetStudyBooksAsync(CancellationToken cancellationToken = default);

    /// <summary>最近 <paramref name="days"/> 天的每日复习量，缺的日子补 0。</summary>
    Task<IReadOnlyList<StudyDay>> GetReviewHistoryAsync(
        DateTimeOffset now,
        int days = 30,
        CancellationToken cancellationToken = default);

    Task<StudySettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(StudySettings settings, CancellationToken cancellationToken = default);

    /// <summary>按评分推进一张卡片并记入复习日志，返回更新后的卡片。</summary>
    Task<StudyCard> ReviewAsync(
        StudyCard card,
        ReviewGrade grade,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销一次评分：把卡片写回 <paramref name="previous"/> 的状态，并删掉该词最新的一条复习记录。
    /// 传入的必须是 <see cref="ReviewAsync"/> 调用之前的那张卡片。
    /// </summary>
    Task UndoReviewAsync(StudyCard previous, CancellationToken cancellationToken = default);

    Task<StudyStats> GetStudyStatsAsync(
        DateTimeOffset now,
        string? bookId = null,
        CancellationToken cancellationToken = default);
}
