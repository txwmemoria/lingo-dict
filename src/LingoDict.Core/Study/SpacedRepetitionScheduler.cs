using LingoDict.Core.Models;

namespace LingoDict.Core.Study;

/// <summary>
/// SM-2 的四档变体（重来/困难/良好/简单）。纯函数，时间由调用方传入，因此完全可测。
/// </summary>
public static class SpacedRepetitionScheduler
{
    public const double InitialEase = 2.5;
    public const double MinimumEase = 1.3;
    public const double MaximumEase = 3.0;

    /// <summary>上限约十年，避免 ease 连乘后溢出成荒谬的日期。</summary>
    public const int MaximumIntervalDays = 3650;

    public static StudyCard CreateCard(IWordReference word, string? bookId, DateTimeOffset now) => new(
        word.WordId,
        word.EntryId,
        word.Headword,
        word.Phonetic,
        word.Translation,
        bookId,
        Repetition: 0,
        IntervalDays: 0,
        Ease: InitialEase,
        DueAt: now,
        LastReviewedAt: null,
        CorrectCount: 0,
        LapseCount: 0,
        IntroducedAt: null);

    public static StudyCard Apply(StudyCard card, ReviewGrade grade, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(card);
        var ease = Math.Clamp(card.Ease + EaseDelta(grade), MinimumEase, MaximumEase);

        // 第一次复习即视为"放出来了"，此后不再占用每日新词配额。
        var introducedAt = card.IntroducedAt ?? now;

        if (grade == ReviewGrade.Again)
        {
            // 连续答对次数清零，当天重新排队；ease 的惩罚保留下来。
            return card with
            {
                Repetition = 0,
                IntervalDays = 0,
                Ease = ease,
                DueAt = now,
                LastReviewedAt = now,
                LapseCount = card.LapseCount + 1,
                IntroducedAt = introducedAt,
            };
        }

        var repetition = card.Repetition + 1;
        var interval = NextInterval(card, grade, repetition, ease);
        return card with
        {
            Repetition = repetition,
            IntervalDays = interval,
            Ease = ease,
            DueAt = now.AddDays(interval),
            LastReviewedAt = now,
            CorrectCount = card.CorrectCount + 1,
            IntroducedAt = introducedAt,
        };
    }

    /// <summary>按钮上"良好 5 天"里的那个天数。重来固定返回 0（当天再来）。</summary>
    public static int PreviewIntervalDays(StudyCard card, ReviewGrade grade)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (grade == ReviewGrade.Again)
        {
            return 0;
        }

        var ease = Math.Clamp(card.Ease + EaseDelta(grade), MinimumEase, MaximumEase);
        return NextInterval(card, grade, card.Repetition + 1, ease);
    }

    private static int NextInterval(StudyCard card, ReviewGrade grade, int repetition, double ease)
    {
        // 上一轮是"重来"时 IntervalDays 为 0，兜底成 1 天再往上乘。
        var previous = Math.Max(card.IntervalDays, 1);
        var days = grade switch
        {
            ReviewGrade.Hard => previous * 1.2,
            ReviewGrade.Good => repetition switch
            {
                1 => 1d,
                2 => 6d,
                _ => previous * ease,
            },
            ReviewGrade.Easy => repetition switch
            {
                1 => 3d,
                2 => 10d,
                _ => previous * ease * 1.3,
            },
            _ => 0d,
        };

        return Math.Clamp((int)Math.Round(days, MidpointRounding.AwayFromZero), 1, MaximumIntervalDays);
    }

    private static double EaseDelta(ReviewGrade grade) => grade switch
    {
        ReviewGrade.Again => -0.20,
        ReviewGrade.Hard => -0.15,
        ReviewGrade.Easy => 0.15,
        _ => 0.0,
    };
}
