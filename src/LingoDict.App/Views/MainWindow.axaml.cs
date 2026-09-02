using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;
using GridLength = Avalonia.Controls.GridLength;

namespace LingoDict.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 走隧道阶段：搜索框自己会吞掉 Esc 和空格，冒泡阶段就收不到了。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 鼠标侧键前进/后退。同样走隧道，免得被列表或文本框先吃掉。
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

        // 列宽是 GridLength，ViewModel 里存的是 double，两边手动搭一下——
        // 只在加载时读一次、拖完写一次，不必做双向绑定。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.LeftPaneWidth))
                    {
                        ContentGrid.ColumnDefinitions[0].Width = new GridLength(model.LeftPaneWidth);
                    }
                    else if (e.PropertyName == nameof(MainViewModel.SelectedWord))
                    {
                        // 换词条必须回到顶部，否则从长词条跳过去会落在半截内容上。
                        DetailScroller.ScrollToHome();
                    }
                };
            }
        };

        // 断点判定放在视图侧：Avalonia 没有 media query，
        // 只有这里知道详情区实际有多宽（窗口宽度减去左栏和拖动条之后）。
        DetailScroller.SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.IsTwoColumnDetail = e.NewSize.Width >= MainViewModel.TwoColumnThreshold;
            }
        };

        // 「上一个/下一个」跟随当前标签页：只有视图知道用户在看哪一页。
        MainTabs.SelectionChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.ActiveScope = MainTabs.SelectedIndex switch
                {
                    0 => BrowseScope.Search,
                    1 => BrowseScope.Book,
                    2 => BrowseScope.Favorites,
                    3 => BrowseScope.History,
                    _ => BrowseScope.None,
                };

                // 切到写作页就开始读模型：129 MB 的会话要一两秒，
                // 等用户敲完字再加载，看起来就是卡住了。
                if (ReferenceEquals(MainTabs.SelectedItem, WritingTab))
                {
                    _ = model.Writing.WarmUpAsync();
                }
            }
        };

        PaneSplitter.DragCompleted += (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.LeftPaneWidth = ContentGrid.ColumnDefinitions[0].ActualWidth;
            }
        };
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed && model.GoBackCommand.CanExecute(null))
        {
            model.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (properties.IsXButton2Pressed && model.GoForwardCommand.CanExecute(null))
        {
            model.GoForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.F or Key.L)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // 专注模式先接管 Esc：这时候用户看不到搜索框，按 Esc 想的是退出。
        if (e.Key == Key.Escape && DataContext is MainViewModel { Study.IsFocusMode: true } focused)
        {
            focused.Study.ToggleFocusCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SearchBox.IsFocused)
        {
            SearchBox.Clear();
            e.Handled = true;
            return;
        }

        // 打完字直接用方向键挑结果，手不用离开键盘——这是最高频的流程。
        if (SearchBox.IsFocused && e.Key is Key.Down or Key.Up && MoveSearchSelection(e.Key == Key.Down ? 1 : -1))
        {
            e.Handled = true;
            return;
        }

        // 复习快捷键只在学习页（或专注模式）生效，且不能抢走文本框里的按键。
        if (e.KeyModifiers != KeyModifiers.None ||
            FocusManager?.GetFocusedElement() is TextBox ||
            DataContext is not MainViewModel { Study: var study } ||
            study.CurrentCard is null ||
            (!study.IsFocusMode && !ReferenceEquals(MainTabs.SelectedItem, StudyTab)))
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            if (!study.IsAnswerVisible)
            {
                study.RevealCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (study.IsAnswerVisible && TryMapGrade(e.Key, out var grade))
        {
            study.GradeCommand.Execute(grade);
            e.Handled = true;
        }
    }

    /// <summary>在搜索结果里上下移动选中项，并把它滚进视野。</summary>
    private bool MoveSearchSelection(int delta)
    {
        if (DataContext is not MainViewModel model || model.SearchResults.Count == 0)
        {
            return false;
        }

        var current = model.SelectedSearchResult is null
            ? -1
            : model.SearchResults.IndexOf(model.SelectedSearchResult);
        var next = Math.Clamp(current + delta, 0, model.SearchResults.Count - 1);
        if (next == current)
        {
            return true;
        }

        model.SelectedSearchResult = model.SearchResults[next];
        SearchList.ScrollIntoView(next);
        return true;
    }

    private static bool TryMapGrade(Key key, out ReviewGrade grade)
    {
        grade = key switch
        {
            Key.D1 or Key.NumPad1 => ReviewGrade.Again,
            Key.D2 or Key.NumPad2 => ReviewGrade.Hard,
            Key.D3 or Key.NumPad3 => ReviewGrade.Good,
            Key.D4 or Key.NumPad4 => ReviewGrade.Easy,
            _ => (ReviewGrade)(-1),
        };

        return Enum.IsDefined(grade);
    }
}
