using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LingoDict.App.ViewModels;
using LingoDict.App.Views;
using LingoDict.Core.Models;

namespace LingoDict.App.Tests;

/// <summary>
/// 无头渲染真正的 MainWindow。编译期能校验 x:DataType 下的绑定，
/// 但 ReflectionBinding（$parent 路径）和附加属性只有跑起来才知道对不对。
/// </summary>
public sealed class MainWindowBindingTests
{
    [AvaloniaFact]
    public void HistoryBack_NamesWhereItGoesAndStaysHiddenUntilThereIsHistory()
    {
        var (window, viewModel, _, _, _) = Show();
        Pump();

        static IEnumerable<TextBlock> Texts(Window w) => w.GetVisualDescendants().OfType<TextBlock>();

        // 一进来没有历史，回退入口整个不出现——不是灰着，是没有。
        // 原来它是顶栏两个常驻的箭头，没历史时照样满亮，看着能点、点了没反应。
        Assert.Empty(viewModel.BackLabel);
        Assert.DoesNotContain(Texts(window), x => x.Text?.StartsWith("返回", StringComparison.Ordinal) == true);

        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();

        // 写出目的地，这样它和「上一个 / 下一个」不可能被看成一回事：
        // 一个是「我从 cancel 来的」，一个是「这个列表的相邻词」。
        Assert.Equal($"返回 {viewModel.SearchResults[0].Headword}", viewModel.BackLabel);
        Assert.Contains(Texts(window), x => x.Text == viewModel.BackLabel);

        // 两套导航同时在场，各说各的
        Assert.Contains(Texts(window), x => x.Text == "上一个");
        Assert.Contains(Texts(window), x => x.Text == "下一个");
    }

    [AvaloniaFact]
    public void SearchResults_HighlightTheQueryInsideEachRow()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SearchText = "取消";
        Pump();

        var runs = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .SelectMany(x => x.Inlines?.OfType<Run>() ?? [])
            .ToList();

        // 命中背景只在高亮片段上设置；字重不能用来判断，词头本身就是 SemiBold。
        Assert.Contains(runs, x => x.Text == "取消" && x.Background is not null);
        Assert.Contains(runs, x => x.Text == "v " && x.Background is null);
    }

    [AvaloniaFact]
    public void SearchResults_WithoutQuery_RenderPlainText()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        var runs = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .SelectMany(x => x.Inlines?.OfType<Run>() ?? [])
            .ToList();

        Assert.NotEmpty(runs);
        Assert.All(runs, x => Assert.Null(x.Background));
        Assert.Contains(runs, x => x.Text == "cancel");
    }

    [AvaloniaFact]
    public void EmptySections_AreHiddenAndPopulatedOnesAreNot()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        // 这个词条有释义和例句，没有短语。
        Assert.True(SectionIsVisible(window, "释义"));
        Assert.True(SectionIsVisible(window, "例句"));
        Assert.False(SectionIsVisible(window, "短语"));
        Assert.True(SectionIsVisible(window, "同近义词"));
    }

    [AvaloniaFact]
    public void ClickingASynonym_NavigatesThroughTheWindowLevelCommand()
    {
        var (window, viewModel, dictionary, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        // 按内容定位，不依赖样式类——链接的外观由主题决定，会随主题变化。
        var link = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(x => Equals(x.Content, "cancellation"));

        // Command 来自 {ReflectionBinding $parent[Window].DataContext...}，解析失败的话这里就是 null。
        Assert.NotNull(link.Command);
        link.Command!.Execute(link.CommandParameter);
        Pump();

        Assert.Equal("cancellation", dictionary.LastFindByHeadword);
    }

    [AvaloniaFact]
    public void FavoriteAndStudyButtons_ReflectCurrentState()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        Assert.NotNull(FindButton(window, "☆ 收藏"));
        Assert.NotNull(FindButton(window, "+ 加入学习"));

        viewModel.ToggleFavoriteCommand.Execute(null);
        viewModel.ToggleStudyCommand.Execute(null);
        Pump();

        Assert.NotNull(FindButton(window, "★ 已收藏"));
        Assert.NotNull(FindButton(window, "✓ 学习中"));
    }

    [AvaloniaFact]
    public void StudyTab_RendersTheDueCardAndItsIntervalLabels()
    {
        var (window, viewModel, _, _, study) = Show();
        study.AddCardsAsync([new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.UtcNow.AddMinutes(-1));
        viewModel.Study.RefreshAsync();
        Pump();

        // TabControl 只实例化选中页的内容，先切到"学习"。
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;
        Pump();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("cancel", texts);
        // 顶上原来并排五个数字，现在只留一句「今天做了几张 / 共几张」，
        // 到期 / 未学 / 总数那几个拆解降级成这一行的 tooltip。
        Assert.Contains("今天 0 / 1", texts);
        Assert.Contains("未学新词 1", viewModel.Study.QueueDetail);

        var reveal = FindButton(window, "显示答案");
        Assert.NotNull(reveal);
        Assert.True(reveal!.IsVisible);

        // 档位名和「下次什么时候」分开两个属性、分两行显示。
        // 原来挤成「良好 · 1 天」，一个间隔号把两个完全不同的东西连在一起。
        Assert.Equal("良好", viewModel.Study.GoodLabel);
        Assert.Equal("1 天后", viewModel.Study.GoodNext);
        Assert.Equal("简单", viewModel.Study.EasyLabel);
        Assert.Equal("3 天后", viewModel.Study.EasyNext);
        Assert.Equal("稍后再考", viewModel.Study.AgainNext);
    }

    [AvaloniaFact]
    public void RevealingAnAnswer_ShowsGradeButtonsAndLoadsTheDetail()
    {
        var (window, viewModel, _, _, study) = Show();
        study.AddCardsAsync([new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.UtcNow.AddMinutes(-1));
        viewModel.Study.RefreshAsync();
        Pump();

        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 4;
        Pump();

        viewModel.Study.RevealCommand.Execute(null);
        Pump();

        Assert.True(viewModel.Study.IsAnswerVisible);
        Assert.False(FindButton(window, "显示答案")!.IsVisible);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();

        // 四个档位必须先有一句话把问题问出来，否则「重来/困难/良好/简单」
        // 看着就是四个没头没尾的词——这正是用户反馈看不懂的地方。
        Assert.Contains(texts, x => x is not null && x.Contains("刚才想起来了吗"));
        Assert.Contains("重来", texts);
        Assert.Contains("稍后再考", texts);

        // 翻面后右侧详情页应当同步显示该词条。
        Assert.Equal("cancel", viewModel.SelectedWord?.Headword);
    }

    [AvaloniaFact]
    public void StudyShortcuts_RevealWithSpaceAndGradeWithNumberKeys()
    {
        var (window, viewModel, _, _, study) = Show();
        study.AddCardsAsync([new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.UtcNow.AddMinutes(-1));
        viewModel.Study.RefreshAsync();
        Pump();
        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 4;
        Pump();

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Pump();
        Assert.True(viewModel.Study.IsAnswerVisible);

        window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.None);
        Pump();
        Assert.Equal(1, study.ReviewCount);
        Assert.False(viewModel.Study.IsAnswerVisible);
    }

    [AvaloniaFact]
    public void StudyShortcuts_DoNotFireWhileTypingInTheSearchBox()
    {
        var (window, viewModel, _, _, study) = Show();
        study.AddCardsAsync([new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.UtcNow.AddMinutes(-1));
        viewModel.Study.RefreshAsync();
        Pump();
        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 4;
        Pump();

        var searchBox = window.GetVisualDescendants().OfType<TextBox>().First();
        searchBox.Focus();
        Pump();

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Pump();

        Assert.False(viewModel.Study.IsAnswerVisible);
    }

    [AvaloniaFact]
    public void BookWordList_VirtualizesInsteadOfRealizingEveryRow()
    {
        var (window, viewModel, dictionary, _, _) = Show();
        dictionary.Results.Clear();
        dictionary.Results.AddRange(Enumerable.Range(1, 20_000)
            .Select(i => new WordSummary(i, i * 10, $"word{i:D5}", "p", $"释义 {i}", 1)));

        viewModel.SelectedBook = viewModel.Books[0];
        Pump();

        Assert.Equal(20_000, viewModel.BookWords.Count);

        // 两万条只应实例化视口范围内的容器，否则词书页会卡死。
        var realized = window.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(realized, 1, 200);
    }

    [AvaloniaFact]
    public void ExampleSentences_AreSplitIntoRunsAndWiredToLookup()
    {
        var (window, viewModel, dictionary, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        var sentence = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Single(x => Controls.Lookup.GetText(x) == "Please cancel the order.");

        // 逐词切成 Run 之后 hover 才能只改其中一个，不必整棵重建。
        var runs = sentence.Inlines!.OfType<Run>().Select(x => x.Text).ToList();
        Assert.Equal(["Please", " ", "cancel", " ", "the", " ", "order", "."], runs);

        // Command 与词头集合都来自 ReflectionBinding，解析失败就是 null。
        Assert.NotNull(Controls.Lookup.GetCommand(sentence));
        Assert.NotNull(Controls.Lookup.GetIndex(sentence));
        Assert.NotNull(sentence.ContextFlyout);
    }

    [AvaloniaFact]
    public void TheTargetWordIsBolded_EvenWhenTheSentenceUsesAnInflectedForm()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        var sentences = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();

        static Run Bold(SelectableTextBlock block) =>
            block.Inlines!.OfType<Run>().Single(x => x.FontWeight == FontWeight.Bold);

        // 导入侧存的强调词是词头 "cancel"，句子里却是 "cancelled"。
        // 两端共用 SearchText.IsInflectionOf，否则这句就一个粗体都没有。
        Assert.Equal(
            "cancel",
            Bold(sentences.Single(x => Controls.Lookup.GetText(x) == "Please cancel the order.")).Text);
        Assert.Equal(
            "cancelled",
            Bold(sentences.Single(x => Controls.Lookup.GetText(x) == "Our flight was cancelled.")).Text);
    }

    [AvaloniaFact]
    public void HoveringAWordHighlightsIt_AndLeavingRestoresItInsteadOfHidingIt()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        var sentence = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Single(x => Controls.Lookup.GetText(x) == "Please cancel the order.");

        var runs = sentence.Inlines!.OfType<Run>().ToList();
        var target = runs.Single(x => x.Text == "cancel");
        var untouched = runs.Single(x => x.Text == "Please");

        var origin = sentence.TranslatePoint(new Point(0, 0), window)!.Value;
        var middle = origin.Y + sentence.Bounds.Height / 2;

        window.MouseMove(new Point(origin.X + 60, middle));
        Pump();
        Assert.NotNull(target.TextDecorations);
        Assert.NotNull(target.Foreground);

        window.MouseMove(new Point(origin.X + 8, middle));
        Pump();

        // 关键：离开后必须回到和普通文字一样的渲染。给 Foreground 赋 null 不是
        // "恢复继承"而是"没有画刷"，那一段会整个消失——这正是之前的 bug。
        Assert.Null(target.TextDecorations);
        Assert.Equal(untouched.Foreground, target.Foreground);
    }

    [AvaloniaFact]
    public void HoveringAWord_EventuallyShowsAPreviewWithTheMeaning()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        var sentence = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Single(x => Controls.Lookup.GetText(x) == "Please cancel the order.");

        // 无头环境下 DispatcherTimer 不触发，把延迟旁路掉来验证其余逻辑。
        Controls.Lookup.SetPreviewDelay(sentence, TimeSpan.Zero);

        var origin = sentence.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseMove(new Point(origin.X + 60, origin.Y + sentence.Bounds.Height / 2));
        Pump();

        Assert.True(ToolTip.GetIsOpen(sentence));
        var texts = (ToolTip.GetTip(sentence) as Control)!
            .GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("cancel", texts);
        Assert.Contains(texts, x => x?.Contains("取消", StringComparison.Ordinal) == true);

        // 移开就收起来，不能挂着不走。
        window.MouseMove(new Point(origin.X + 8, origin.Y + sentence.Bounds.Height / 2));
        Pump();
        Assert.False(ToolTip.GetIsOpen(sentence));
    }

    [AvaloniaFact]
    public void OnlyWordsPresentInTheDictionaryAreOfferedForLookup()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        // 按句子挑，不能用 First()：词头现在也是 SelectableTextBlock（为了能复制），
        // 排在例句前面，而它身上没有挂取词索引。
        var index = Controls.Lookup.GetIndex(
            window.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Single(x => Controls.Lookup.GetText(x) == "Please cancel the order."))!;

        // 假仓储里只有 cancel / cancellation，所以句中的 Please、the、order 都不该被点亮，
        // 否则用户会双击一个查不到的词然后什么都不发生。
        Assert.True(index.ContainsKey("cancel"));
        Assert.False(index.ContainsKey("please"));
        Assert.False(index.ContainsKey("order"));

        // 索引里带着预览要用的信息，悬停时不必回数据库。
        Assert.Equal("'kænsl", index["cancel"].Phonetic);
        Assert.Contains("取消", index["cancel"].Translation, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ListNavigation_WalksTheBookAndShowsPosition()
    {
        var (window, viewModel, dictionary, _, _) = Show();
        dictionary.Results.Clear();
        dictionary.Results.AddRange(Enumerable.Range(1, 5)
            .Select(i => new WordSummary(i, i * 10, $"word{i}", "p", $"释义 {i}", 1)));

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 1;
        viewModel.SelectedBook = viewModel.Books[0];
        Pump();

        Assert.Equal(BrowseScope.Book, viewModel.ActiveScope);
        viewModel.SelectedBookWord = viewModel.BookWords[0];
        Pump();

        Assert.Equal("四级词汇 · 1 / 5", viewModel.BrowseLabel);
        Assert.False(viewModel.CanGoPreviousInList);   // 已经在第一个
        Assert.True(viewModel.CanGoNextInList);

        viewModel.GoNextInListCommand.Execute(null);
        Pump();

        Assert.Equal("四级词汇 · 2 / 5", viewModel.BrowseLabel);
        Assert.Equal("word2", viewModel.SelectedWord?.Headword);
        Assert.True(viewModel.CanGoPreviousInList);

        // Alt+↓ 全局生效，不依赖列表焦点——焦点挪到详情页照样能翻。
        window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "DetailScroller").Focus();
        Pump();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Pump();

        Assert.Equal("四级词汇 · 3 / 5", viewModel.BrowseLabel);
        Assert.Equal("word3", viewModel.SelectedWord?.Headword);
    }

    [AvaloniaFact]
    public void ListNavigation_FollowsTheActiveTabAndIsNotTheSameAsBackForward()
    {
        var (window, viewModel, _, _, _) = Show();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();

        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        Assert.Equal("搜索结果 · 1 / 2", viewModel.BrowseLabel);

        // 顺着列表看到第 2 个，再从例句跳走
        viewModel.GoNextInListCommand.Execute(null);
        Pump();
        Assert.Equal("cancellation", viewModel.SelectedWord?.Headword);

        viewModel.NavigateToWordCommand.Execute("cancel");
        Pump();
        Assert.Equal("cancel", viewModel.SelectedWord?.Headword);

        // 后退回到跳走之前那个词，而不是列表里的上一个——两条轴各管各的。
        viewModel.GoBackCommand.Execute(null);
        Pump();
        Assert.Equal("cancellation", viewModel.SelectedWord?.Headword);

        // 切到收藏页，范围跟着换；空收藏没得翻。
        tabs.SelectedIndex = 2;
        Pump();
        Assert.Equal(BrowseScope.Favorites, viewModel.ActiveScope);
        Assert.False(viewModel.CanBrowseList);

        // 学习页没有列表。
        tabs.SelectedIndex = 4;
        Pump();
        Assert.Equal(BrowseScope.None, viewModel.ActiveScope);
        Assert.Equal(string.Empty, viewModel.BrowseLabel);
    }

    [AvaloniaFact]
    public void SearchPlaceholder_IsReadableOnTheDarkHeader()
    {
        var (window, _, _, _, _) = Show();

        // 窗口里好几个输入框都有 PART_Placeholder，只看顶栏那个。
        var searchBox = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "SearchBox");
        var placeholder = searchBox.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(x => x.Name == "PART_Placeholder");

        // 顶栏永远是深色，占位文字必须来自 SearchPlaceholder 而不是主题默认的近黑色。
        // 选择器写错部件名不会报错，只会静默失效——这条断言就是拿来盯它的。
        // SearchPlaceholder 定义在主题变体字典里，不带变体取不到。
        Assert.True(window.TryFindResource("SearchPlaceholder", window.ActualThemeVariant, out var resource));
        var expected = Assert.IsAssignableFrom<ISolidColorBrush>(resource);
        var actual = Assert.IsAssignableFrom<ISolidColorBrush>(placeholder.Foreground);
        Assert.Equal(expected.Color, actual.Color);

        // 再兜一层：真要是掉回近黑色，上面那条也可能因为资源被改而放过。
        var brightness = (actual.Color.R + actual.Color.G + actual.Color.B) / 3;
        Assert.InRange(brightness, 110, 255);
    }

    [AvaloniaFact]
    public void SearchRows_ShowTypicalCollocationsSoCandidatesCanBeCompared()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SearchText = "取消";
        Pump();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(x => x.Text).ToList();

        // cancel 那一行带搭配，cancellation 没有——没有的不该留出空位。
        Assert.Contains("cancel the order", texts);
        Assert.Contains("cancel out", texts);

        var chipLists = window.GetVisualDescendants().OfType<ItemsControl>()
            .Where(x => x.ItemsSource is IReadOnlyList<string>)
            .ToList();
        Assert.Contains(chipLists, x => x.IsVisible);
        Assert.Contains(chipLists, x => !x.IsVisible);
    }

    [AvaloniaFact]
    public void PhraseHits_ShowAboveTheWordListAndNavigateToTheirWord()
    {
        var (window, viewModel, dictionary, _, _) = Show();

        // 中文母语者查「取消订单」，最该看到的是那条搭配本身。
        viewModel.SearchText = "取消订单";
        Pump();

        var hit = Assert.Single(viewModel.PhraseResults);
        Assert.Equal("cancel an order", hit.Text);

        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(x => x.DataContext is PhraseHit);

        // Command 走 ReflectionBinding，解析失败就是 null。
        Assert.NotNull(button.Command);
        button.Command!.Execute(button.CommandParameter);
        Pump();

        Assert.Equal("cancel", viewModel.SelectedWord?.Headword);
    }

    [AvaloniaFact]
    public void PhraseSection_IsHiddenWhenNothingMatches()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SearchText = "无关的查询";
        Pump();

        Assert.Empty(viewModel.PhraseResults);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<Button>(), x => x.DataContext is PhraseHit);
    }

    [AvaloniaFact]
    public void FocusMode_ShowsTheSameLiveSessionFullScreenAndLeavesOnEscape()
    {
        var (window, viewModel, _, _, study) = Show();
        study.AddCardsAsync([new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.UtcNow.AddMinutes(-1));
        viewModel.Study.RefreshAsync();
        Pump();

        var overlay = window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "StudyFocusOverlay");
        Assert.False(overlay.IsVisible);

        viewModel.Study.ToggleFocusCommand.Execute(null);
        Pump();
        Assert.True(overlay.IsVisible);

        // 全屏里必须是活的会话，不是另抄的一张静态卡。
        var session = overlay.GetVisualDescendants().OfType<StudySession>().Single();
        var reveal = session.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "显示答案"));
        Assert.True(reveal.IsVisible);

        // 词书范围、每日上限这些配置项在专注模式里收起来。
        Assert.All(
            session.GetVisualDescendants().OfType<Control>().Where(x => x.Classes.Contains("session-chrome")),
            x => Assert.False(x.IsVisible));

        // 学习页没被选中也要能用空格/数字键，否则全屏里键盘是死的。
        Assert.NotSame(window.GetVisualDescendants().OfType<TabControl>().First().SelectedItem, null);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Pump();
        Assert.True(viewModel.Study.IsAnswerVisible);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump();

        Assert.False(viewModel.Study.IsFocusMode);
        Assert.False(overlay.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(1280, false)]
    [InlineData(1920, true)]
    public void DetailLayout_SplitsIntoTwoColumnsOnlyWhenThereIsRoom(int width, bool expectTwoColumns)
    {
        var (window, viewModel, _, _, _) = Show(width);
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        Assert.Equal(expectTwoColumns, viewModel.IsTwoColumnDetail);

        // 参考栏在宽屏时移到右列，窄屏时落回左列正文下方——顺序不能乱。
        var aside = window.GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(x => x.Children.Any(c => c is StackPanel s
                && s.Children.OfType<TextBlock>().Any(t => t.Text == "同近义词")));

        Assert.Equal(expectTwoColumns ? 2 : 0, Grid.GetColumn(aside));
        Assert.Equal(expectTwoColumns ? 0 : 1, Grid.GetRow(aside));

        // 分两列之后每栏的行长必须还在可读区间里，否则宽屏反而更难读。
        var body = (Control)aside.Parent!;
        var column = expectTwoColumns ? aside.Bounds.Width : body.Bounds.Width;
        Assert.InRange(column, 400, 720);
    }

    private static bool SectionIsVisible(Window window, string title)
    {
        var label = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(x => x.Classes.Contains("section-title") && x.Text == title);

        return label is not null && label.GetVisualAncestors().OfType<Control>().Prepend(label).All(x => x.IsVisible);
    }

    private static Button? FindButton(Window window, string content) => window.GetVisualDescendants()
        .OfType<Button>()
        .FirstOrDefault(x => Equals(x.Content, content));

    [AvaloniaFact]
    public void Headword_CanBeSelectedAndCopied()
    {
        var (window, viewModel, _, _, _) = Show();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        // 词头必须是 SelectableTextBlock 而不是 TextBlock——查完词最常见的
        // 下一步就是把它复制走，普通 TextBlock 连选都选不中。
        var headword = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .FirstOrDefault(x => x.Text == viewModel.SelectedWord!.Headword);

        Assert.NotNull(headword);
        headword.SelectAll();
        Assert.Equal(viewModel.SelectedWord!.Headword, headword.SelectedText);
    }

    private static void Pump()
    {
        for (var round = 0; round < 12; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, MainViewModel ViewModel, FakeDictionaryRepository Dictionary,
        FakeUserDataRepository UserData, FakeStudyRepository Study) Show(int width = 1280, int height = 820)
    {
        var dictionary = new FakeDictionaryRepository();
        var userData = new FakeUserDataRepository();
        var study = new FakeStudyRepository();
        var viewModel = new MainViewModel(dictionary, userData, study);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };

        window.Show();
        viewModel.InitializeAsync();
        Pump();
        return (window, viewModel, dictionary, userData, study);
    }
}
