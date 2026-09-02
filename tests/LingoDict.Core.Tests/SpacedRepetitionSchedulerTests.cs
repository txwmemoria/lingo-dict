using LingoDict.Core.Models;
using LingoDict.Core.Study;

namespace LingoDict.Core.Tests;

public sealed class SpacedRepetitionSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly WordReference Cancel = new(1, 10, "cancel", "'kænsl", "v 取消");

    [Fact]
    public void CreateCard_StartsDueImmediatelyAndUnreviewed()
    {
        var card = SpacedRepetitionScheduler.CreateCard(Cancel, "CET4_1", Now);

        Assert.Equal(0, card.Repetition);
        Assert.Equal(0, card.IntervalDays);
        Assert.Equal(SpacedRepetitionScheduler.InitialEase, card.Ease);
        Assert.Equal(Now, card.DueAt);
        Assert.Null(card.LastReviewedAt);
        Assert.Equal("CET4_1", card.BookId);
    }

    [Theory]
    [InlineData(ReviewGrade.Good, 1)]
    [InlineData(ReviewGrade.Hard, 1)]
    [InlineData(ReviewGrade.Easy, 3)]
    public void FirstSuccessfulReview_UsesFixedLearningSteps(ReviewGrade grade, int expectedDays)
    {
        var reviewed = SpacedRepetitionScheduler.Apply(NewCard(), grade, Now);

        Assert.Equal(1, reviewed.Repetition);
        Assert.Equal(expectedDays, reviewed.IntervalDays);
        Assert.Equal(Now.AddDays(expectedDays), reviewed.DueAt);
        Assert.Equal(1, reviewed.CorrectCount);
        Assert.Equal(Now, reviewed.LastReviewedAt);
    }

    [Fact]
    public void SecondGoodReview_JumpsToSixDays()
    {
        var card = SpacedRepetitionScheduler.Apply(NewCard(), ReviewGrade.Good, Now);

        var second = SpacedRepetitionScheduler.Apply(card, ReviewGrade.Good, Now.AddDays(1));

        Assert.Equal(2, second.Repetition);
        Assert.Equal(6, second.IntervalDays);
    }

    [Fact]
    public void ThirdGoodReview_MultipliesPreviousIntervalByEase()
    {
        var card = NewCard() with { Repetition = 2, IntervalDays = 6, Ease = 2.5 };

        var reviewed = SpacedRepetitionScheduler.Apply(card, ReviewGrade.Good, Now);

        Assert.Equal(15, reviewed.IntervalDays); // 6 × 2.5
        Assert.Equal(2.5, reviewed.Ease);        // 良好不改变难度因子
    }

    [Fact]
    public void Again_ResetsProgressAndRequeuesSameDay()
    {
        var card = NewCard() with { Repetition = 4, IntervalDays = 30, Ease = 2.5, CorrectCount = 4 };

        var reviewed = SpacedRepetitionScheduler.Apply(card, ReviewGrade.Again, Now);

        Assert.Equal(0, reviewed.Repetition);
        Assert.Equal(0, reviewed.IntervalDays);
        Assert.Equal(Now, reviewed.DueAt);
        Assert.Equal(1, reviewed.LapseCount);
        Assert.Equal(4, reviewed.CorrectCount); // 答错不该抹掉历史正确数
        Assert.Equal(2.3, reviewed.Ease, 3);
    }

    [Fact]
    public void GoodAfterAgain_RestartsFromOneDayWithoutDividingByZero()
    {
        var lapsed = SpacedRepetitionScheduler.Apply(
            NewCard() with { Repetition = 4, IntervalDays = 30 },
            ReviewGrade.Again,
            Now);

        var recovered = SpacedRepetitionScheduler.Apply(lapsed, ReviewGrade.Good, Now);

        Assert.Equal(1, recovered.Repetition);
        Assert.Equal(1, recovered.IntervalDays);
    }

    [Theory]
    [InlineData(ReviewGrade.Again, -0.20)]
    [InlineData(ReviewGrade.Hard, -0.15)]
    [InlineData(ReviewGrade.Good, 0.0)]
    [InlineData(ReviewGrade.Easy, 0.15)]
    public void EaseMovesByGrade(ReviewGrade grade, double delta)
    {
        var reviewed = SpacedRepetitionScheduler.Apply(NewCard() with { Ease = 2.0 }, grade, Now);

        Assert.Equal(2.0 + delta, reviewed.Ease, 3);
    }

    [Fact]
    public void EaseStaysWithinBounds()
    {
        var floor = NewCard() with { Ease = SpacedRepetitionScheduler.MinimumEase };
        var ceiling = NewCard() with { Ease = SpacedRepetitionScheduler.MaximumEase };

        for (var round = 0; round < 10; round++)
        {
            floor = SpacedRepetitionScheduler.Apply(floor, ReviewGrade.Again, Now);
            ceiling = SpacedRepetitionScheduler.Apply(ceiling, ReviewGrade.Easy, Now);
        }

        Assert.Equal(SpacedRepetitionScheduler.MinimumEase, floor.Ease);
        Assert.Equal(SpacedRepetitionScheduler.MaximumEase, ceiling.Ease);
    }

    [Fact]
    public void IntervalIsCappedSoDatesStaySane()
    {
        var card = NewCard() with { Repetition = 20, IntervalDays = 3000, Ease = 3.0 };

        var reviewed = SpacedRepetitionScheduler.Apply(card, ReviewGrade.Easy, Now);

        Assert.Equal(SpacedRepetitionScheduler.MaximumIntervalDays, reviewed.IntervalDays);
    }

    [Fact]
    public void PreviewMatchesWhatApplyWillDo()
    {
        var card = NewCard() with { Repetition = 3, IntervalDays = 12, Ease = 2.4 };

        foreach (var grade in Enum.GetValues<ReviewGrade>())
        {
            Assert.Equal(
                SpacedRepetitionScheduler.Apply(card, grade, Now).IntervalDays,
                SpacedRepetitionScheduler.PreviewIntervalDays(card, grade));
        }
    }

    [Fact]
    public void RepeatedGoodReviews_GrowIntervalsMonotonically()
    {
        var card = NewCard();
        var previous = 0;
        var at = Now;

        for (var round = 0; round < 8; round++)
        {
            card = SpacedRepetitionScheduler.Apply(card, ReviewGrade.Good, at);
            Assert.True(card.IntervalDays > previous, $"第 {round + 1} 轮：{card.IntervalDays} 未大于 {previous}");
            previous = card.IntervalDays;
            at = card.DueAt;
        }
    }

    private static StudyCard NewCard() => SpacedRepetitionScheduler.CreateCard(Cancel, null, Now);
}
