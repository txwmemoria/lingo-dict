using System;
using LingoDict.Core.Models;

namespace LingoDict.App.ViewModels;

/// <summary>
/// 复习历史柱状图的一根柱子。高度在 ViewModel 里算好，界面就不需要转换器，
/// 也不必知道最大值是多少。
/// </summary>
public sealed record StudyDayBar(StudyDay Day, double Height)
{
    public const double MaxHeight = 56;

    public string Tooltip => Day.Total == 0
        ? $"{Day.Date:MM-dd}　未复习"
        : $"{Day.Date:MM-dd}　{Day.Total} 张　答对 {Day.Accuracy:P0}";

    public static StudyDayBar From(StudyDay day, int peak) => new(
        day,
        peak <= 0 ? 0 : Math.Max(day.Total == 0 ? 0 : 2, MaxHeight * day.Total / peak));
}
