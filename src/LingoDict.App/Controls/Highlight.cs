using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LingoDict.Core.Text;

namespace LingoDict.App.Controls;

/// <summary>
/// 给 TextBlock 加上命中词高亮。同时绑定 <c>Text</c> 和 <c>Query</c>，
/// 控件会把文本拆成 Run 序列写进 Inlines（仍然支持换行和省略号）。
/// </summary>
public static class Highlight
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(Highlight));

    public static readonly AttachedProperty<string?> QueryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Query", typeof(Highlight));

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("Hooked", typeof(Highlight));

    /// <summary>
    /// 画过高亮的控件。换主题色时要把它们全部重画一遍。
    /// </summary>
    /// <remarks>
    /// 存弱引用：这是个静态表，存强引用等于让每一个渲染过高亮的 TextBlock 永远活着。
    /// </remarks>
    private static readonly List<WeakReference<TextBlock>> Rendered = [];

    static Highlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((target, _) => Render(target));
        QueryProperty.Changed.AddClassHandler<TextBlock>((target, _) => Render(target));
        Palette.Changed += (_, _) => RepaintAll();
    }

    private static void RepaintAll()
    {
        for (var i = Rendered.Count - 1; i >= 0; i--)
        {
            if (Rendered[i].TryGetTarget(out var target))
            {
                Render(target);
            }
            else
            {
                Rendered.RemoveAt(i);
            }
        }
    }

    public static void SetText(TextBlock target, string? value) => target.SetValue(TextProperty, value);

    public static string? GetText(TextBlock target) => target.GetValue(TextProperty);

    public static void SetQuery(TextBlock target, string? value) => target.SetValue(QueryProperty, value);

    public static string? GetQuery(TextBlock target) => target.GetValue(QueryProperty);

    private static void Render(TextBlock target)
    {
        var inlines = target.Inlines;
        if (inlines is null)
        {
            return;
        }

        // Run 不参与样式系统，颜色只能在这里取；主题切换时重画一次。
        if (!target.GetValue(HookedProperty))
        {
            target.SetValue(HookedProperty, true);
            target.ActualThemeVariantChanged += (sender, _) => Render((TextBlock)sender!);
            Rendered.Add(new WeakReference<TextBlock>(target));
        }

        var foreground = Resource(target, "SemiColorPrimary");
        var background = Resource(target, "SemiColorPrimaryLight");

        inlines.Clear();
        foreach (var segment in TextHighlighter.Split(GetText(target), GetQuery(target)))
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.FontWeight = FontWeight.SemiBold;
                run.Foreground = foreground;
                run.Background = background;
            }

            inlines.Add(run);
        }
    }

    private static IBrush? Resource(TextBlock target, string key) =>
        target.TryFindResource(key, target.ActualThemeVariant, out var value) ? value as IBrush : null;
}
