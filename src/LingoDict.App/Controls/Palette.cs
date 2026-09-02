using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using LingoDict.Core.Models;

namespace LingoDict.App.Controls;

/// <summary>
/// 按主题色重算调色板，写回 <see cref="Application.Resources"/>。
/// </summary>
/// <remarks>
/// 做法是量出来的，不是拍的。
///
/// Semi 的所有强调色最终都来自一条 <c>SemiBlue0..9</c> 色阶——按钮底色、
/// 列表选中底、标签页下划线，全是它的别名。把那条色阶重算一遍，整套跟着换；
/// 只改 <c>SemiColorPrimary</c> 是不够的，那些别名是从色阶烘焙出来的，动不了。
///
/// 打印出来的色阶几乎只差**明度**——色相在 207–216 之间漂几度、饱和度在
/// 0.96–1.00 之间浮动，都是手调的痕迹。我们固定一组色相/饱和度套这张明度梯子：
///
/// <code>
///   序号      0      1      2      3      4      5      6      7      8      9
///   浅色   0.959  0.896  0.794  0.692  0.590  0.490  0.420  0.351  0.280  0.210
///   深色   0.229  0.310  0.398  0.486  0.580  0.665  0.749  0.831  0.916  0.969
/// </code>
///
/// 5 号是主色，6/7 是 hover/pressed，2 号是禁用和淡底——浅色越点越深，
/// 深色越点越亮，这也是 Semi 自己的规则。所以一个主题色只要给出色相和饱和度。
/// 默认蓝按 H=216 / S=100% 生成 5 号正是 #0064FA，和 Semi 的原值分毫不差；
/// 其余档位差几个色阶单位，肉眼分不出，换成别的色相时反而更规整。
///
/// 顶栏那条深色品牌带同理：现在的 #16203A 正是 HSL(222, 45%, 16%)，
/// 于是它也跟着主题色的色相走，换成绿色主题时顶栏是深绿而不是突兀的藏蓝。
/// </remarks>
public static class Palette
{
    /// <summary>一个主题色：只需要色相和饱和度，其余由明度梯子生成。</summary>
    /// <param name="Id">存进用户库的标识。</param>
    /// <param name="Label">设置里显示的名字。</param>
    /// <param name="Hue">0–360。</param>
    /// <param name="Saturation">0–1。各色相要单独调：绿和紫在 S=100% 下太刺眼。</param>
    public sealed record Accent(string Id, string Label, double Hue, double Saturation)
    {
        /// <summary>设置里那个小圆点用的颜色，就是浅色主题下的主色（色阶 5 号）。</summary>
        public Color Swatch => FromHsl(Hue, Saturation, 0.490);
    }

    public static IReadOnlyList<Accent> Accents { get; } =
    [
        new(AppearanceTheme.DefaultAccent, "蓝", 216, 1.00),
        new("cyan", "青", 190, 0.80),
        new("green", "绿", 150, 0.60),
        new("orange", "橙", 24, 0.90),
        new("rose", "玫红", 340, 0.80),
        new("violet", "紫", 268, 0.65),
    ];

    public static Accent Resolve(string? id)
    {
        foreach (var accent in Accents)
        {
            if (string.Equals(accent.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return accent;
            }
        }

        return Accents[0];
    }

    /// <summary>
    /// 应用明暗模式和主题色。字号那一套在 <see cref="Typography"/>，两者互不相干。
    /// </summary>
    public static void Apply(Application application, AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(settings);

        application.RequestedThemeVariant = AppearanceTheme.NormalizeMode(settings.ThemeMode) switch
        {
            AppearanceTheme.Light => ThemeVariant.Light,
            AppearanceTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var accent = Resolve(settings.Accent);

        // 换成一整本带 ThemeDictionaries 的字典，而不是往 Resources 里塞平铺的值：
        // 这样明暗切换时由 Avalonia 自己挑对应的一套，不用监听 ActualThemeVariantChanged
        // 再手动重写一遍——那种写法总会在某个路径上漏掉。
        var replacement = new ResourceDictionary();
        replacement.ThemeDictionaries[ThemeVariant.Light] = Build(accent, dark: false);
        replacement.ThemeDictionaries[ThemeVariant.Dark] = Build(accent, dark: true);

        var merged = application.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceDictionary existing && existing.ContainsKey(Marker))
            {
                merged.RemoveAt(i);
            }
        }

        replacement[Marker] = true;

        // 追加在最后：Avalonia 按倒序查合并字典，最后加的优先，
        // 才盖得住 Tokens.axaml 里那套默认的品牌色。
        merged.Add(replacement);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 调色板换过了。
    /// </summary>
    /// <remarks>
    /// 给那些「取一次颜色就画死」的地方用。搜索高亮画的是 Run，Run 不参与样式系统，
    /// 颜色是取一次就写进去的——它本来只在明暗切换时重画，换主题色时不会动，
    /// 于是整页都变了绿，只有高亮那几个字还是蓝的。
    /// </remarks>
    public static event EventHandler? Changed;

    /// <summary>用来认出上一次塞进去的那本字典，好在换色时先摘掉。</summary>
    private const string Marker = "LingoDictPaletteMarker";

    /// <summary>Semi 色阶 0–9 的明度。浅色由淡到深，深色反过来。</summary>
    private static readonly double[] LightLadder =
        [0.959, 0.896, 0.794, 0.692, 0.590, 0.490, 0.420, 0.351, 0.280, 0.210];

    private static readonly double[] DarkLadder =
        [0.229, 0.310, 0.398, 0.486, 0.580, 0.665, 0.749, 0.831, 0.916, 0.969];

    private static ResourceDictionary Build(Accent accent, bool dark)
    {
        var ladder = dark ? DarkLadder : LightLadder;
        Color Step(int index) => FromHsl(accent.Hue, accent.Saturation, ladder[index]);

        var dictionary = new ResourceDictionary();

        // 整条色阶。Semi 内部的语义键（ButtonSolidPrimaryBackground、
        // ListBoxItemSelectedBackground…）都是它的别名，换掉这里就全跟着换，
        // 不必去枚举那一长串名字。
        for (var i = 0; i <= 9; i++)
        {
            Set(dictionary, $"SemiBlue{i}", Step(i));
        }

        // Semi 的控件模板并不读 SemiBlue*，而是读一层语义键（ButtonSolidPrimaryBackground、
        // ListBoxItemSelectedBackground…），而那层键是在主题加载时从色阶**烘焙**出来的：
        // 运行时改色阶不会传导过去。所以必须逐个覆盖语义键本身。
        //
        // 这张表是量出来的，不是抄文档的：把 Semi 程序集里的资源键名全抽出来，
        // 建一个真实的主窗口让控件主题按需加载，再逐个查它当前解析成什么颜色，
        // 凡是落在蓝色色阶上的就记下它对应第几档。生成方法见 git 历史里的 ProbeKeys。
        foreach (var (key, lightStep, darkStep, lightOpacity, darkOpacity) in SemanticKeys)
        {
            var brush = new SolidColorBrush(Step(dark ? darkStep : lightStep))
            {
                // 深色主题下那些「淡底」是拿主色压透明度做的（0.2 / 0.3 / 0.4）。
                // 不带上透明度的话，深色下选中的列表行会变成一整条高饱和的色块。
                Opacity = dark ? darkOpacity : lightOpacity,
            };
            dictionary[key] = brush;
        }

        // 我们自己的界面直接引用的那几个键，也一并给上。
        Set(dictionary, "SemiColorPrimary", Step(5));
        Set(dictionary, "SemiColorPrimaryPointerover", Step(6));
        Set(dictionary, "SemiColorPrimaryActive", Step(7));
        Set(dictionary, "SemiColorPrimaryDisabled", Step(2));
        Set(dictionary, "SemiColorLink", Step(5));
        Set(dictionary, "SemiColorLinkActive", Step(7));
        Set(dictionary, "SemiColorLinkVisited", Step(5));
        Set(dictionary, "SemiColorFocusBorder", Step(5));

        // 浅色下的「淡底」是色阶最上面那几档；深色下 Semi 换了个做法——
        // 用主色本身压透明度。少了透明度，深色主题里搜索高亮就是一整块实心色，
        // 字直接看不见了（前景和背景成了同一个颜色）。
        Set(dictionary, "SemiColorPrimaryLight", Step(dark ? 5 : 0), dark ? 0.2 : 1);
        Set(dictionary, "SemiColorPrimaryLightPointerover", Step(dark ? 5 : 1), dark ? 0.3 : 1);
        Set(dictionary, "SemiColorPrimaryLightActive", Step(dark ? 5 : 2), dark ? 0.4 : 1);

        // 顶栏是固定的深色带，明暗两套都深，只是深浅不同。
        // 饱和度压得比主色低很多，否则一条高饱和的色带会把整个界面压住。
        Color Brand(double s, double l) => FromHsl(accent.Hue, s, l);

        if (dark)
        {
            Set(dictionary, "BrandInk", Brand(0.39, 0.09));
            Set(dictionary, "BrandTitle", Color.Parse("#F2F5FA"));
            Set(dictionary, "BrandSubtitle", Brand(0.23, 0.60));
            Set(dictionary, "SearchField", Brand(0.32, 0.15));
            Set(dictionary, "SearchFieldBorder", Brand(0.29, 0.24));
            Set(dictionary, "SearchPlaceholder", Brand(0.15, 0.54));
        }
        else
        {
            Set(dictionary, "BrandInk", Brand(0.45, 0.16));
            Set(dictionary, "BrandTitle", Colors.White);
            Set(dictionary, "BrandSubtitle", Brand(0.33, 0.72));
            Set(dictionary, "SearchField", Brand(0.35, 0.22));
            Set(dictionary, "SearchFieldBorder", Brand(0.29, 0.32));
            Set(dictionary, "SearchPlaceholder", Brand(0.25, 0.61));
        }

        return dictionary;
    }

    /// <summary>
    /// Semi 里跟着主题色走的语义键：键名、浅色档位、深色档位、浅色透明度、深色透明度。
    /// </summary>
    /// <remarks>
    /// 刻意排除了 <c>LabelTag*Blue*</c> 和 <c>NotificationCard*Information*</c>——
    /// 那两组的「蓝」是语义（蓝色标签、信息提示），不是强调色，跟着主题色变才是错的。
    /// </remarks>
    private static readonly (string Key, int Light, int Dark, double LightOpacity, double DarkOpacity)[] SemanticKeys =
        Parse("""
        AdornerLayerBorderBrush 2 5 1 0.4
        ButtonDefaultPrimaryForeground 5 5 1 1
        ButtonInputInnerPointeroverForeground 6 6 1 1
        ButtonInputInnerPressedForeground 7 7 1 1
        ButtonSolidPrimaryBackground 5 5 1 1
        ButtonSolidPrimaryBorderBrush 5 5 1 1
        ButtonSolidPrimaryPointeroverBackground 6 6 1 1
        ButtonSolidPrimaryPointeroverBorderBrush 6 6 1 1
        ButtonSolidPrimaryPressedBackground 7 7 1 1
        ButtonSolidPrimaryPressedBorderBrush 7 7 1 1
        CalendarDatePickerBorderedPointeroverBorderBrush 6 6 1 1
        CalendarDatePickerFocusBorderBrush 5 5 1 1
        CalendarItemCalendarButtonSelectedBackground 5 5 1 1
        CalendarItemCalendarDayButtonSelectedBackground 5 5 1 1
        CalendarItemCalendarDayButtonTodayForeground 5 5 1 1
        CheckBoxCardCheckedBackground 0 5 1 0.2
        CheckBoxCardCheckedBorderBrush 5 5 1 1
        CheckBoxCardCheckedDisabledBorderBrush 2 2 1 1
        CheckBoxCardCheckedPointeroverBorderBrush 6 6 1 1
        CheckBoxCardCheckedPressedBorderBrush 7 7 1 1
        CheckBoxCheckedDefaultBackground 5 5 1 1
        CheckBoxCheckedDefaultBorderBrush 5 5 1 1
        CheckBoxCheckedDisabledBackground 2 2 1 1
        CheckBoxCheckedDisabledBorderBrush 2 2 1 1
        CheckBoxCheckedPointeroverBackground 6 6 1 1
        CheckBoxCheckedPointeroverBorderBrush 6 6 1 1
        CheckBoxCheckedPressedBackground 7 7 1 1
        CheckBoxCheckedPressedBorderBrush 7 7 1 1
        CheckBoxPointeroverBorderBrush 5 5 1 1
        CheckBoxPressedBorderBrush 7 7 1 1
        ComboBoxItemSelectedBackground 0 5 1 0.2
        ComboBoxItemSelectedPointeroverBackground 1 5 1 0.3
        ComboBoxSelectorBorderedFocusBorderBrush 6 6 1 1
        ComboBoxSelectorBorderedPointeroverBorderBrush 6 6 1 1
        ComboBoxSelectorBorderedPressedBorderBrush 7 7 1 1
        ComboBoxSelectorFocusBorderBrush 5 5 1 1
        ComboBoxSelectorPressedBorderBrush 5 5 1 1
        CommandBarToggleButtonCheckedBackground 0 5 1 0.2
        CommandBarToggleButtonCheckedDisabledForeground 2 2 1 1
        CommandBarToggleButtonCheckedForeground 5 5 1 1
        CommandBarToggleButtonCheckedPointeroverBackground 1 5 1 0.3
        CommandBarToggleButtonCheckedPressedBackground 2 5 1 0.4
        GridSplitterPreviewBackground 2 5 1 0.4
        HyperlinkButtonForeground 5 5 1 1
        HyperlinkButtonPointeroverForeground 6 6 1 1
        HyperlinkButtonPressedForeground 7 7 1 1
        HyperlinkButtonVisitedForeground 5 5 1 1
        ListBoxItemSelectedBackground 0 5 1 0.2
        ListBoxItemSelectedPointeroverBackground 1 5 1 0.3
        ProgressBarIndicatorBrush 5 5 1 1
        ProgressBarPrimaryForeground 5 5 1 1
        RadioButtonButtonCheckedForeground 5 5 1 1
        RadioButtonCardCheckDefaultBorderBrush 5 5 1 1
        RadioButtonCardCheckPointeroverBorderBrush 6 6 1 1
        RadioButtonCardCheckPressedBorderBrush 7 7 1 1
        RadioButtonCardCheckedBackground 0 5 1 0.2
        RadioButtonCheckIconDefaultBackground 5 5 1 1
        RadioButtonCheckIconDefaultBorderBrush 5 5 1 1
        RadioButtonCheckIconDisabledBackground 2 2 1 1
        RadioButtonCheckIconDisabledBorderBrush 2 2 1 1
        RadioButtonCheckIconPointeroverBackground 6 6 1 1
        RadioButtonCheckIconPointeroverBorderBrush 6 6 1 1
        RadioButtonCheckIconPressedBackground 7 7 1 1
        RadioButtonCheckIconPressedBorderBrush 7 7 1 1
        RadioButtonUncheckIconPointeroverBorderBrush 6 6 1 1
        RadioButtonUncheckIconPressedBorderBrush 7 7 1 1
        RefreshVisualizerIconForeground 5 5 1 1
        SliderThumbBorderBrush 5 5 1 1
        SliderThumbDisabledBorderBrush 1 1 1 1
        SliderThumbPointeroverBorderBrush 6 6 1 1
        SliderThumbPressedBorderBrush 7 7 1 1
        SliderTrackForeground 5 5 1 1
        TabItemButtonHeaderSelectedBackground 0 5 1 0.2
        TabItemButtonHeaderSelectedForeground 5 5 1 1
        TabItemIconSelectedForeground 5 5 1 1
        TabItemLineHeaderSelectedBackground 0 5 1 0.2
        TabItemLinePipeSelectedBackground 5 5 1 1
        TableViewRowBackgroundSelected 0 5 1 0.2
        TableViewRowBackgroundSelectedPointerover 1 5 1 0.3
        TableViewRowBackgroundSelectedPressed 2 5 1 0.4
        TextBlockMarkBackground 0 5 1 0.2
        TextBoxBorderedPointeroverBorderBrush 6 6 1 1
        TextBoxFocusBorderBrush 5 5 1 1
        ToggleButtonPrimaryIndeterminateBorderBrush 5 5 1 1
        TreeViewItemSelectedBackground 0 5 1 0.2
        """);

    private static (string, int, int, double, double)[] Parse(string table)
    {
        var rows = new List<(string, int, int, double, double)>();
        using var reader = new StringReader(table);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5)
            {
                rows.Add((
                    parts[0],
                    int.Parse(parts[1], CultureInfo.InvariantCulture),
                    int.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture),
                    double.Parse(parts[4], CultureInfo.InvariantCulture)));
            }
        }

        return [.. rows];
    }

    /// <remarks>
    /// 这些键存的是 Brush 而不是 Color——名字带 Color 是 Semi 的历史包袱。
    /// 放 Color 进去不会报错，只会在用到的地方抛类型转换异常。
    /// </remarks>
    private static void Set(IDictionary<object, object?> dictionary, string key, Color color, double opacity = 1) =>
        dictionary[key] = new SolidColorBrush(color) { Opacity = opacity };

    /// <param name="hue">0–360。</param>
    /// <param name="saturation">0–1。</param>
    /// <param name="lightness">0–1。</param>
    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var sector = (((hue % 360) + 360) % 360) / 60;
        var second = chroma * (1 - Math.Abs((sector % 2) - 1));
        var match = lightness - (chroma / 2);

        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };

        return Color.FromRgb(Channel(r + match), Channel(g + match), Channel(b + match));
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
