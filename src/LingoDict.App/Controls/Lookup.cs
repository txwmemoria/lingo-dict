using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LingoDict.Core.Models;
using LingoDict.Core.Text;

namespace LingoDict.App.Controls;

/// <summary>
/// 让例句、短语里的单词可以就地查询。
///
/// 关键不是双击这个手势，而是动作发生前要有反馈：鼠标移过时高亮光标下的词、
/// 换成手型光标，只高亮词典里真有的词，停留片刻还会弹出释义预览。
/// 大多数时候看一眼就够了，双击才是「我要看完整词条」。
///
/// 单击刻意不做跳转：会和拖动选词打架，误触也太容易。
/// </summary>
public static class Lookup
{
    /// <summary>
    /// 悬停多久才弹预览。太短会在扫视时乱闪，太长又显得迟钝。
    /// 设为零则立即弹出——无头测试里 DispatcherTimer 不会触发，靠这个把延迟旁路掉。
    /// </summary>
    public static readonly AttachedProperty<TimeSpan> PreviewDelayProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, TimeSpan>(
            "PreviewDelay", typeof(Lookup), TimeSpan.FromMilliseconds(350));

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, string?>("Text", typeof(Lookup));

    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, ICommand?>("Command", typeof(Lookup));

    /// <summary>词头索引（键为归一化形式）；为空时既不高亮也不预览。</summary>
    public static readonly AttachedProperty<IReadOnlyDictionary<string, WordPreview>?> IndexProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, IReadOnlyDictionary<string, WordPreview>?>(
            "Index", typeof(Lookup));

    /// <summary>原文里被 &lt;b&gt; 标出的目标词，渲染时加粗，方便在句中一眼找到。</summary>
    public static readonly AttachedProperty<string?> EmphasisProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, string?>("Emphasis", typeof(Lookup));

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, State?>("State", typeof(Lookup));

    static Lookup()
    {
        TextProperty.Changed.AddClassHandler<SelectableTextBlock>((target, _) => Rebuild(target));
        IndexProperty.Changed.AddClassHandler<SelectableTextBlock>((target, _) => Rebuild(target));
        EmphasisProperty.Changed.AddClassHandler<SelectableTextBlock>((target, _) => Rebuild(target));
        CommandProperty.Changed.AddClassHandler<SelectableTextBlock>(OnCommandChanged);
    }

    public static void SetText(SelectableTextBlock target, string? value) => target.SetValue(TextProperty, value);

    public static string? GetText(SelectableTextBlock target) => target.GetValue(TextProperty);

    public static void SetCommand(SelectableTextBlock target, ICommand? value) => target.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(SelectableTextBlock target) => target.GetValue(CommandProperty);

    public static void SetIndex(SelectableTextBlock target, IReadOnlyDictionary<string, WordPreview>? value) =>
        target.SetValue(IndexProperty, value);

    public static IReadOnlyDictionary<string, WordPreview>? GetIndex(SelectableTextBlock target) =>
        target.GetValue(IndexProperty);

    public static void SetPreviewDelay(SelectableTextBlock target, TimeSpan value) =>
        target.SetValue(PreviewDelayProperty, value);

    public static TimeSpan GetPreviewDelay(SelectableTextBlock target) => target.GetValue(PreviewDelayProperty);

    public static void SetEmphasis(SelectableTextBlock target, string? value) =>
        target.SetValue(EmphasisProperty, value);

    public static string? GetEmphasis(SelectableTextBlock target) => target.GetValue(EmphasisProperty);

    private static void OnCommandChanged(SelectableTextBlock target, AvaloniaPropertyChangedEventArgs args)
    {
        target.DoubleTapped -= OnDoubleTapped;
        target.PointerMoved -= OnPointerMoved;
        target.PointerExited -= OnPointerExited;
        target.ContextFlyout = null;

        if (args.NewValue is not ICommand)
        {
            return;
        }

        target.DoubleTapped += OnDoubleTapped;
        target.PointerMoved += OnPointerMoved;
        target.PointerExited += OnPointerExited;
        target.ContextFlyout = BuildFlyout(target);
        ToolTip.SetPlacement(target, PlacementMode.Pointer);
        ToolTip.SetShowDelay(target, 0);
    }

    /// <summary>
    /// 一次把文本切成逐词的 Run，之后 hover 只改其中一个 Run 的样式，
    /// 不必在每次鼠标移动时重建整棵 Inlines。
    /// </summary>
    private static void Rebuild(SelectableTextBlock target)
    {
        var index = GetIndex(target);
        var inlines = target.Inlines;
        if (inlines is null)
        {
            return;
        }

        var state = target.GetValue(StateProperty) ?? new State();
        state.Reset(target);
        inlines.Clear();

        // 目标词按整词比较，避免 "can" 把 "cancel" 里的前三个字母也加粗。
        var emphasis = GetEmphasis(target);
        var offset = 0;
        foreach (var run in SearchText.SplitWords(GetText(target)))
        {
            var inline = new Run(run.Text);
            if (run.IsWord && SearchText.IsInflectionOf(run.Text, emphasis))
            {
                inline.FontWeight = FontWeight.Bold;
            }

            inlines.Add(inline);

            if (run.IsWord && index is not null)
            {
                var word = SearchText.WordAt(run.Text, 0);
                if (word.Length > 0 && index.TryGetValue(WordNormalizer.Normalize(word), out var preview))
                {
                    state.Words.Add(new Entry(offset, offset + run.Text.Length, word, preview, inline));
                }
            }

            offset += run.Text.Length;
        }

        target.SetValue(StateProperty, state);
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (sender is SelectableTextBlock target)
        {
            Highlight(target, Locate(target, args.GetPosition(target)));
        }
    }

    private static void OnPointerExited(object? sender, PointerEventArgs args)
    {
        if (sender is SelectableTextBlock target)
        {
            Highlight(target, null);
        }
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is not SelectableTextBlock target || GetCommand(target) is not { } command)
        {
            return;
        }

        // 只有高亮过的词才可跳——所见即所得，不会跳到一个查不到的词上。
        if (Locate(target, args.GetPosition(target)) is { } entry && command.CanExecute(entry.Word))
        {
            ToolTip.SetIsOpen(target, false);
            command.Execute(entry.Word);
        }
    }

    private static Entry? Locate(SelectableTextBlock target, Point position)
    {
        if (target.GetValue(StateProperty) is not { } state || target.TextLayout is null)
        {
            return null;
        }

        var index = target.TextLayout.HitTestPoint(position).TextPosition;
        foreach (var entry in state.Words)
        {
            if (index >= entry.Start && index < entry.End)
            {
                return entry;
            }
        }

        return null;
    }

    private static void Highlight(SelectableTextBlock target, Entry? entry)
    {
        if (target.GetValue(StateProperty) is not { } state || ReferenceEquals(state.Hovered, entry))
        {
            return;
        }

        if (state.Hovered is not null)
        {
            // 必须 ClearValue 而不是赋 null：显式的 null 前景色不是"恢复继承"，
            // 而是"没有画刷"，那一段文字会整个消失。
            state.Hovered.Inline.ClearValue(TextElement.ForegroundProperty);
            state.Hovered.Inline.ClearValue(Inline.TextDecorationsProperty);
        }

        state.Hovered = entry;
        state.HidePreview(target);

        if (entry is not null)
        {
            entry.Inline.TextDecorations = TextDecorations.Underline;
            if (target.TryFindResource("SemiColorPrimary", target.ActualThemeVariant, out var accent) &&
                accent is IBrush brush)
            {
                entry.Inline.Foreground = brush;
            }

            state.SchedulePreview(target);
        }

        target.Cursor = new Cursor(entry is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
    }

    internal static Control BuildPreview(WordPreview preview)
    {
        var panel = new StackPanel { Spacing = 2, MaxWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = preview.Headword,
            FontWeight = FontWeight.SemiBold,
        });

        if (preview.Phonetic.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = preview.Phonetic, Opacity = 0.7, FontSize = 12 });
        }

        panel.Children.Add(new TextBlock
        {
            Text = preview.Translation,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "双击查看完整词条",
            Opacity = 0.55,
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        });

        return panel;
    }

    private static MenuFlyout BuildFlyout(SelectableTextBlock target)
    {
        var lookup = new MenuItem { Header = "查询选中内容" };
        lookup.Click += (_, _) =>
        {
            var selection = target.SelectedText?.Trim();
            if (!string.IsNullOrEmpty(selection) && GetCommand(target) is { } command && command.CanExecute(selection))
            {
                command.Execute(selection);
            }
        };

        // 只放查询一项。复制交给 SelectableTextBlock 自带的 Ctrl+C——
        // Avalonia 12 把 SetTextAsync 从 IClipboard 上挪走了，为此自造一个复制项不值当。
        var flyout = new MenuFlyout { ItemsSource = new[] { lookup } };
        flyout.Opening += (_, _) =>
        {
            var hasSelection = !string.IsNullOrWhiteSpace(target.SelectedText);
            lookup.IsEnabled = hasSelection;
            lookup.Header = hasSelection ? $"查询「{Shorten(target.SelectedText!)}」" : "查询选中内容";
        };

        return flyout;
    }

    private static string Shorten(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 24 ? trimmed : trimmed[..24] + "…";
    }

    private sealed record Entry(int Start, int End, string Word, WordPreview Preview, Run Inline);

    private sealed class State
    {
        private DispatcherTimer? _timer;

        public List<Entry> Words { get; } = [];

        public Entry? Hovered { get; set; }

        public void SchedulePreview(SelectableTextBlock target)
        {
            var delay = GetPreviewDelay(target);
            if (delay <= TimeSpan.Zero)
            {
                Show(target);
                return;
            }

            _timer ??= new DispatcherTimer();
            _timer.Stop();
            _timer.Interval = delay;
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();

            void OnTick(object? sender, EventArgs args)
            {
                _timer!.Stop();
                Show(target);
            }
        }

        private void Show(SelectableTextBlock target)
        {
            if (Hovered is { } entry)
            {
                ToolTip.SetTip(target, BuildPreview(entry.Preview));
                ToolTip.SetIsOpen(target, true);
            }
        }

        public void HidePreview(SelectableTextBlock target)
        {
            _timer?.Stop();
            ToolTip.SetIsOpen(target, false);
        }

        public void Reset(SelectableTextBlock target)
        {
            HidePreview(target);
            Words.Clear();
            Hovered = null;
        }
    }
}
