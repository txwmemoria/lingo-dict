namespace LingoDict.Core.Models;

/// <summary>复习评分。取值会写进 review_log，不要改动已有成员的数值。</summary>
public enum ReviewGrade
{
    /// <summary>没想起来，当天重排。</summary>
    Again = 0,

    /// <summary>想起来了但很吃力。</summary>
    Hard = 1,

    /// <summary>正常回忆起来。</summary>
    Good = 2,

    /// <summary>毫不费力。</summary>
    Easy = 3,
}

public sealed record StudyCard(
    long WordId,
    long EntryId,
    string Headword,
    string Phonetic,
    string Translation,
    string? BookId,
    int Repetition,
    int IntervalDays,
    double Ease,
    DateTimeOffset DueAt,
    DateTimeOffset? LastReviewedAt,
    int CorrectCount,
    int LapseCount,
    /// <summary>首次复习的时间。为空表示这张卡还没被放出来过，即"新词"。</summary>
    DateTimeOffset? IntroducedAt = null) : IWordReference
{
    public bool IsNew => IntroducedAt is null;
}

/// <summary>
/// 每日投放量。整本词书加进来之后靠这两个上限分批释放，
/// 否则几千张卡会一次性全部到期。
/// </summary>
public sealed record StudySettings(int NewPerDay, int ReviewPerDay)
{
    public static StudySettings Default { get; } = new(20, 200);

    public const int MaxPerDay = 9_999;

    public StudySettings Clamp() => new(
        Math.Clamp(NewPerDay, 0, MaxPerDay),
        Math.Clamp(ReviewPerDay, 0, MaxPerDay));
}

/// <param name="DueCount">全部到期卡片数，不受每日上限约束。</param>
/// <param name="RemainingToday">今天还能学的张数，已扣掉当日配额。</param>
public sealed record StudyStats(
    int TotalCards,
    int DueCount,
    int NewCount,
    int ReviewedToday,
    int RemainingToday);

/// <summary>某一天的复习量，按评分档位拆开。</summary>
public sealed record StudyDay(DateOnly Date, int Again, int Hard, int Good, int Easy)
{
    public int Total => Again + Hard + Good + Easy;

    /// <summary>答对率：除"重来"以外都算记住了。没有复习记录时返回 0。</summary>
    public double Accuracy => Total == 0 ? 0 : (double)(Total - Again) / Total;
}

/// <summary>学习计划里出现过的词书，用于把复习范围收窄到某一本。</summary>
public sealed record StudyBook(string? BookId, string Title, int CardCount)
{
    public const string AllBooksTitle = "全部词书";
}
