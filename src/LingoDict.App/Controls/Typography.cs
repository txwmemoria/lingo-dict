using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using LingoDict.Core.Models;

namespace LingoDict.App.Controls;

/// <summary>
/// 把外观偏好写回 <see cref="Application.Resources"/>。界面里所有字号都是
/// <c>DynamicResource</c>，改这里就会实时生效，不需要重启。
/// </summary>
public static class Typography
{
    /// <summary>缩放 1.0 时的基准阶梯，与 Tokens.axaml 里的默认值一致。</summary>
    private static readonly (string Key, double Size)[] Scale =
    [
        ("FontSizeCaption", 11),
        ("FontSizeSmall", 12),
        ("FontSizeCompact", 13),
        ("FontSizeBody", 14),
        ("FontSizeBodyLarge", 15),
        ("FontSizeSubtitle", 16),
        ("FontSizeTitle", 17),
        ("FontSizeBrand", 21),
        ("FontSizeDisplay", 34),
        ("FontSizeHeadword", 36),

        // 专注模式的卡片铺满窗口，字号得跟着上一档，否则大留白里字显得更小。
        ("FontSizeFocusAnswer", 26),
        ("FontSizeFocusHeadword", 64),
    ];

    /// <summary>
    /// Semi 控件用的是它自己的字号资源，不一起覆盖的话按钮、标签页会停在原尺寸，
    /// 只有正文变大，看起来会很怪。
    /// </summary>
    private static readonly (string Key, double Size)[] SemiScale =
    [
        ("DefaultFontSize", 14),
        ("ButtonDefaultFontSize", 14),
        ("SemiFontSizeHeader5", 16),
        ("SemiFontSizeHeader4", 18),
        ("SemiFontSizeHeader3", 20),
        ("SemiFontSizeHeader2", 24),
        ("SemiFontSizeHeader1", 30),
    ];

    public static readonly IReadOnlyList<double> Presets = [0.85, 1.0, 1.15, 1.3];

    public const string DefaultFontFamily = "Inter, Microsoft YaHei UI, PingFang SC";

    public static void Apply(Application application, AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(settings);

        var clamped = settings.Clamp();
        foreach (var (key, size) in Scale)
        {
            application.Resources[key] = Math.Round(size * clamped.FontScale, 1);
        }

        foreach (var (key, size) in SemiScale)
        {
            application.Resources[key] = Math.Round(size * clamped.FontScale, 1);
        }

        application.Resources["AppFontFamily"] =
            new FontFamily(clamped.FontFamily ?? DefaultFontFamily);
    }
}
