using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;

namespace LingoDict.App.Tests;

/// <summary>
/// 学习页顶部的今日进度。
/// </summary>
/// <remarks>
/// 原来那里并排摆着五个数字（今日待学 / 今日已学 / 到期 / 未学 / 共），
/// 权重一样、彼此重复，唯独没回答背单词时唯一真正关心的那个问题：
/// 「今天还剩多少」。这些属性就是把那一句话算出来。
/// </remarks>
public sealed class StudyProgressTests
{
    private static StudyViewModel Create(int cards)
    {
        var repository = new FakeStudyRepository();
        repository.AddCardsAsync(
            [.. Enumerable.Range(1, cards).Select(i =>
                new WordReference(i, i * 10, "w" + i, "'w", "n 词"))],
            null,
            DateTimeOffset.Now.AddMinutes(-1));

        var study = new StudyViewModel(repository);
        study.RefreshAsync();
        Pump();
        return study;
    }

    [AvaloniaFact]
    public void TheProgressLineCountsWhatIsDoneOutOfTodaysTotal()
    {
        var study = Create(3);

        Assert.Equal(0, study.TodayDone);
        Assert.Equal(3, study.TodayTotal);
        Assert.Equal("今天 0 / 3", study.TodayLabel);
        Assert.Equal(0, study.TodayProgress);
        Assert.False(study.IsTodayDone);
    }

    [AvaloniaFact]
    public void GradingACardMovesTheBar()
    {
        var study = Create(4);

        study.GradeCommand.Execute(ReviewGrade.Good);
        Pump();

        Assert.Equal(1, study.TodayDone);
        Assert.Equal("今天 1 / 4", study.TodayLabel);
        Assert.Equal(25, study.TodayProgress);
    }

    [AvaloniaFact]
    public void FinishingTheQueueSaysSoInsteadOfShowingAFullBarWithNoWords()
    {
        var study = Create(2);

        study.GradeCommand.Execute(ReviewGrade.Good);
        Pump();
        study.GradeCommand.Execute(ReviewGrade.Good);
        Pump();

        Assert.True(study.IsTodayDone);
        Assert.Equal("今天学完了 · 2 张", study.TodayLabel);
        Assert.Equal(100, study.TodayProgress);
    }

    [AvaloniaFact]
    public void WithNoCardsTheBarIsFullRatherThanZero()
    {
        // 0/0 算成 0% 的话，一个空词书会显示成「一点没做」，
        // 看着像还欠着什么。
        var study = Create(0);

        Assert.Equal("今天没有要学的", study.TodayLabel);
        Assert.Equal(100, study.TodayProgress);
    }

    [AvaloniaFact]
    public void TheBreakdownStaysAvailableAsATooltip()
    {
        // 到期 / 未学 / 总数没有删掉，只是从五个并列的数字降级成
        // 一行说明——想知道的时候还查得到。
        var study = Create(3);

        Assert.Contains("到期", study.QueueDetail);
        Assert.Contains("未学", study.QueueDetail);
        Assert.Contains("3", study.QueueDetail);
    }

    private static void Pump()
    {
        for (var round = 0; round < 12; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
