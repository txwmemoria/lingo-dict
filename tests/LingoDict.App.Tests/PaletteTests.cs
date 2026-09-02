using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using LingoDict.App.Controls;
using LingoDict.Core.Models;

namespace LingoDict.App.Tests;

/// <summary>
/// 主题色调色板。
/// </summary>
/// <remarks>
/// 整套颜色是从 Semi 自己的色阶量出来的，所以最有力的检查就是：
/// 用默认蓝跑一遍，生成的值必须和 Semi 的原值一模一样。梯子错了这里立刻红。
/// </remarks>
public sealed class PaletteTests
{
    private static readonly double[] Ladder =
        [0.959, 0.896, 0.794, 0.692, 0.590, 0.490, 0.420, 0.351, 0.280, 0.210];

    [Fact]
    public void DefaultBlueLandsExactlyOnSemisPrimary()
    {
        var blue = Palette.Resolve(AppearanceTheme.DefaultAccent);

        // 5 号是主色。这一档必须分毫不差，它是整张梯子的锚点——
        // 对上了就说明色相、饱和度和明度三个数都取对了。
        Assert.Equal(Color.Parse("#FF0064FA"), Palette.FromHsl(blue.Hue, blue.Saturation, Ladder[5]));
    }

    [Theory]
    [InlineData(0, "#FFEAF5FF")]
    [InlineData(2, "#FF98CDFD")]
    [InlineData(7, "#FF004FB3")]
    [InlineData(9, "#FF002C6B")]
    public void OtherStepsStayCloseToSemis(int step, string expected)
    {
        // 其余档位只求接近，不求相同：Semi 的色阶里色相会漂几度、饱和度在
        // 0.96–1.00 之间浮动，是手调出来的。我们固定一组 H/S 套明度梯子，
        // 差几个色阶单位，肉眼分不出，换成别的色相时反而更规整。
        var blue = Palette.Resolve(AppearanceTheme.DefaultAccent);
        var actual = Palette.FromHsl(blue.Hue, blue.Saturation, Ladder[step]);
        var want = Color.Parse(expected);

        Assert.True(
            Math.Abs(actual.R - want.R) <= 16 && Math.Abs(actual.G - want.G) <= 16 && Math.Abs(actual.B - want.B) <= 16,
            $"第 {step} 档算出 {actual}，Semi 是 {want}，差得太远");
    }

    [AvaloniaFact]
    public void ApplyingAnAccentChangesTheSemanticKeysControlsActuallyRead()
    {
        var app = Application.Current!;
        Palette.Apply(app, AppearanceSettings.Default with { Accent = "green" });

        // 这个键才是按钮模板真正读的那个。只改 SemiColorPrimary 是不够的——
        // Semi 的语义键是在主题加载时从色阶烘焙出来的，运行时改色阶传导不过去。
        Assert.True(app.Resources.TryGetResource("ButtonSolidPrimaryBackground", ThemeVariant.Light, out var value));
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
        Assert.True(color.G > color.B && color.G > color.R, $"绿色主题下按钮底色却是 {color}");
    }

    [AvaloniaFact]
    public void DarkTintsKeepTheirOpacity()
    {
        var app = Application.Current!;
        Palette.Apply(app, AppearanceSettings.Default with { Accent = "rose" });

        // 深色主题的「淡底」是主色压透明度做的。丢了透明度，搜索高亮会变成
        // 一整块实心色，前景和背景同色，字直接看不见。
        Assert.True(app.Resources.TryGetResource("SemiColorPrimaryLight", ThemeVariant.Dark, out var value));
        var brush = Assert.IsType<SolidColorBrush>(value);
        Assert.Equal(0.2, brush.Opacity, 3);

        Assert.True(app.Resources.TryGetResource("SemiColorPrimaryLight", ThemeVariant.Light, out var light));
        Assert.Equal(1.0, ((SolidColorBrush)light!).Opacity, 3);
    }

    [AvaloniaTheory]
    [InlineData(AppearanceTheme.System)]
    [InlineData(AppearanceTheme.Light)]
    [InlineData(AppearanceTheme.Dark)]
    public void ThemeModeDrivesTheRequestedVariant(string mode)
    {
        var app = Application.Current!;
        Palette.Apply(app, AppearanceSettings.Default with { ThemeMode = mode });

        var expected = mode switch
        {
            AppearanceTheme.Light => ThemeVariant.Light,
            AppearanceTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        Assert.Equal(expected, app.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void ApplyingTwiceDoesNotPileUpDictionaries()
    {
        var app = Application.Current!;
        var before = app.Resources.MergedDictionaries.Count;

        for (var i = 0; i < 5; i++)
        {
            Palette.Apply(app, AppearanceSettings.Default with { Accent = i % 2 == 0 ? "green" : "orange" });
        }

        // 每次换色都新加一本却不摘旧的，字典会越堆越长——每次查资源都要多走几本
        Assert.True(app.Resources.MergedDictionaries.Count <= before + 1);
    }

    [Fact]
    public void AnUnknownAccentFallsBackInsteadOfThrowing()
    {
        // 旧版本存下来的 id、或者手改坏的用户库，不该让应用起不来
        Assert.Equal(AppearanceTheme.DefaultAccent, Palette.Resolve("chartreuse").Id);
        Assert.Equal(AppearanceTheme.DefaultAccent, Palette.Resolve(null).Id);
    }

    [Fact]
    public void EveryAccentHasADistinctSwatch()
    {
        var swatches = Palette.Accents.Select(x => x.Swatch).ToList();
        Assert.Equal(swatches.Count, swatches.Distinct().Count());
        Assert.All(Palette.Accents, x => Assert.InRange(x.Hue, 0, 360));
        Assert.All(Palette.Accents, x => Assert.InRange(x.Saturation, 0.3, 1.0));
    }
}
