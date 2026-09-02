using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LingoDict.App.Controls;
using LingoDict.Core.Models;

namespace LingoDict.App.ViewModels;

public sealed record FontScaleOption(string Label, double Scale)
{
    public static IReadOnlyList<FontScaleOption> All { get; } =
    [
        new("小", 0.85),
        new("标准", 1.0),
        new("大", 1.15),
        new("特大", 1.3),
    ];

    public static FontScaleOption Nearest(double scale) =>
        All.OrderBy(x => System.Math.Abs(x.Scale - scale)).First();
}

/// <summary>明暗模式的三个选项。「跟随系统」是默认。</summary>
public sealed record ThemeModeOption(string Label, string Id)
{
    public static IReadOnlyList<ThemeModeOption> All { get; } =
    [
        new("跟随系统", AppearanceTheme.System),
        new("浅色", AppearanceTheme.Light),
        new("深色", AppearanceTheme.Dark),
    ];

    public static ThemeModeOption Resolve(string? id)
    {
        var normalized = AppearanceTheme.NormalizeMode(id);
        return All.First(x => x.Id == normalized);
    }
}

/// <summary>
/// 设置页上的一个色点。
/// </summary>
/// <remarks>
/// 单独包一层只为了 <see cref="IsSelected"/>：勾选标记要绑「这个色点是不是当前选中的」，
/// 而 ConverterParameter 在 Avalonia 里不能是绑定，拿不到当前选中的 id。
/// 与其在 XAML 里堆 MultiBinding，不如让 ViewModel 直接说清楚。
/// </remarks>
public sealed partial class AccentChoice : ObservableObject
{
    public AccentChoice(Palette.Accent accent) => Accent = accent;

    public Palette.Accent Accent { get; }

    public string Id => Accent.Id;

    public string Label => Accent.Label;

    public Color Swatch => Accent.Swatch;

    [ObservableProperty]
    private bool _isSelected;
}

public static class FontFamilyOption
{
    public const string Default = "默认";

    /// <summary>
    /// 固定几个常见字体而不是枚举系统字体：列表短、跨平台可预期，
    /// 缺失的字体 Avalonia 会自动回退，不会渲染成空白。
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Default,
        "Microsoft YaHei UI",
        "SimSun",
        "SimHei",
        "KaiTi",
        "PingFang SC",
        "Segoe UI",
    ];

    public static string? ToFamily(string? label) =>
        string.IsNullOrWhiteSpace(label) || label == Default ? null : label;

    public static string ToLabel(string? family) =>
        string.IsNullOrWhiteSpace(family) ? Default : family;
}

public static class SpeechVoiceOption
{
    public const string System = "系统默认";

    public static string? ToVoice(string? label) =>
        string.IsNullOrWhiteSpace(label) || label == System ? null : label;

    /// <summary>存下来的嗓音如果在这台机器上不存在，退回系统默认而不是显示一个选不中的名字。</summary>
    public static string ToLabel(string? voice, IReadOnlyList<string> available) =>
        !string.IsNullOrWhiteSpace(voice) && available.Contains(voice) ? voice : System;
}
