using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LingoDict.App.ViewModels;
using LingoDict.App.Views;
using LingoDict.Core.Models;
using LingoDict.Core.Services;
using LingoDict.Grammar;

namespace LingoDict.App.Tests;

/// <summary>
/// 开发工具，不是断言型测试：把各界面状态渲染成 PNG 供肉眼比对。
/// 合成鼠标/键盘输入会被系统的 UIPI 拦截，无法驱动真实窗口，
/// 无头 Skia 渲染是这个应用唯一可行的视觉验证途径。
/// 设置 LINGODICT_SHOTS=&lt;输出目录&gt; 才会产出，否则直接跳过。
/// </summary>
public sealed class Shots
{
    [AvaloniaFact]
    public void Render()
    {
        var dir = Environment.GetEnvironmentVariable("LINGODICT_SHOTS");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Shot(dir, "light-search", ThemeVariant.Light, _ => { });
        Shot(dir, "dark-search", ThemeVariant.Dark, _ => { });
        Shot(dir, "light-study", ThemeVariant.Light, Study);
        Shot(dir, "dark-study", ThemeVariant.Dark, Study);
        Shot(dir, "light-books", ThemeVariant.Light, Books);
        Shot(dir, "light-browse", ThemeVariant.Light, BookWalkthrough);
        Shot(dir, "light-bookmark", ThemeVariant.Light, Bookmark);
        Shot(dir, "light-large", ThemeVariant.Light, Large);
        Shot(dir, "light-hover", ThemeVariant.Light, HoverSentence);
        Shot(dir, "light-nav", ThemeVariant.Light, Navigated);
        Shot(dir, "light-favorites", ThemeVariant.Light, HoverFavoriteRow);
        Shot(dir, "light-phrases", ThemeVariant.Light, PhraseHits);
        Shot(dir, "light-placeholder", ThemeVariant.Light, EmptySearch);
        Shot(dir, "light-undo", ThemeVariant.Light, StudyAfterGrading);
        Shot(dir, "light-focus", ThemeVariant.Light, StudyFocus, 1440, 900);
        Shot(dir, "dark-focus", ThemeVariant.Dark, StudyFocus, 1440, 900);
        Shot(dir, "narrow-books", ThemeVariant.Light, Books, 960, 640);
        Shot(dir, "wide-search", ThemeVariant.Light, _ => { }, 1920, 1000);
        Shot(dir, "light-writing", ThemeVariant.Light, Writing);
        Shot(dir, "dark-writing", ThemeVariant.Dark, Writing);
        Shot(dir, "light-accent-green", ThemeVariant.Light, c => Accent(c, "green"));
        Shot(dir, "dark-accent-rose", ThemeVariant.Dark, c => Accent(c, "rose"));
        PhoneticsShot(dir, "light-ipa-chart", ThemeVariant.Light, drill: false);
        PhoneticsShot(dir, "light-ipa-drill", ThemeVariant.Light, drill: true);
        SettingsShot(dir, "light-settings", ThemeVariant.Light);
        SettingsShot(dir, "dark-settings", ThemeVariant.Dark);
        Shot(dir, "light-writing-setup", ThemeVariant.Light, OpenWriting, withModel: false);
        Shot(dir, "dark-writing-setup", ThemeVariant.Dark, OpenWriting, withModel: false);
    }

    private static void Large((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        Controls.Typography.Apply(Avalonia.Application.Current!, new AppearanceSettings(1.3, null));
        Pump();
    }

    private static void Study((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Study.AddCardsAsync(
            [
                new WordReference(1, 10, "cancel", "'kænsl", "v 取消，撤销"),
                new WordReference(2, 20, "abruptly", "ə'brʌptli", "adv 突然地"),
            ],
            "CET4_1",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        c.Vm.Study.RefreshAsync();
        Pump();
        c.Window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 4;
        Pump();
        c.Vm.Study.RevealCommand.Execute(null);
        Pump();
    }

    /// <summary>
    /// 点进两个词，让「后退」可用而「前进」不可用——顶栏那两个箭头
    /// 必须一眼能看出哪个是死的。原来图标写死了 Foreground，
    /// 把按钮的禁用态盖掉，两个永远一样亮。
    /// </summary>
    private static void Navigated((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Vm.SelectedSearchResult = c.Vm.SearchResults[0];
        Pump();
        c.Vm.SelectedSearchResult = c.Vm.SearchResults[1];
        Pump();
    }

    /// <summary>把鼠标停在例句的第二个词上，用来检查 hover 高亮的实际渲染。</summary>
    private static void HoverSentence((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Vm.SelectedSearchResult = c.Vm.SearchResults[0];
        Pump();

        var sentence = c.Window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(x => Controls.Lookup.GetText(x)?.StartsWith("Please", StringComparison.Ordinal) == true);

        var origin = sentence.TranslatePoint(new Point(0, 0), c.Window) ?? new Point(0, 0);
        var y = origin.Y + sentence.Bounds.Height / 2;

        // 先停在 "cancel" 上，再移开——移开时的清理路径才是出问题的地方。
        c.Window.MouseMove(new Point(origin.X + 60, y));
        Pump();
        c.Window.MouseMove(new Point(origin.X + 8, y));
        Pump();
    }

    /// <summary>收藏两个词，鼠标停在第一行上，检查行内删除按钮是否浮出来。</summary>
    private static void HoverFavoriteRow((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Vm.SelectedSearchResult = c.Vm.SearchResults[0];
        Pump();
        c.Vm.ToggleFavoriteCommand.Execute(null);
        c.Vm.SelectedSearchResult = c.Vm.SearchResults[1];
        Pump();
        c.Vm.ToggleFavoriteCommand.Execute(null);
        Pump();

        c.Window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        Pump();

        var row = c.Window.GetVisualDescendants().OfType<ListBoxItem>().First();
        var origin = row.TranslatePoint(new Point(0, 0), c.Window) ?? new Point(0, 0);
        c.Window.MouseMove(new Point(origin.X + row.Bounds.Width / 2, origin.Y + row.Bounds.Height / 2));
        Pump();
    }

    /// <summary>清空搜索框，检查顶栏上的占位文字是否可读。</summary>
    private static void EmptySearch((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Vm.SearchText = string.Empty;
        Pump();
    }

    /// <summary>短语命中：中文搜搭配时最该看到的那一块。</summary>
    private static void PhraseHits((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Vm.SearchText = "取消";
        Pump();
    }

    /// <summary>专注模式：卡片铺满窗口。</summary>
    private static void StudyFocus((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        Study(c);
        c.Vm.Study.ToggleFocusCommand.Execute(null);
        Pump();
    }

    /// <summary>评一张之后停在学习页，检查撤销入口。</summary>
    private static void StudyAfterGrading((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        Study(c);
        c.Vm.Study.GradeCommand.Execute(ReviewGrade.Good);
        Pump();
    }

    /// <summary>词书里逐个看词：检查详情页顶部的位置条。</summary>
    private static void BookWalkthrough((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        Books(c);
        c.Vm.SelectedBookWord = c.Vm.BookWords[0];
        Pump();
        c.Vm.GoNextInListCommand.Execute(null);
        Pump();
    }

    /// <summary>换个主题色，看强调色和顶栏那条带是不是一起跟着走。</summary>
    private static void Accent((Window Window, MainViewModel Vm, FakeStudyRepository Study) c, string id)
    {
        c.Vm.PickAccentCommand.Execute(id);
        Pump();
        c.Vm.SelectedSearchResult = c.Vm.SearchResults[0];
        Pump();
    }

    /// <summary>只切到写作页——没装模型时那一屏就是这个样子。</summary>
    private static void OpenWriting((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 5;
        Pump();
    }

    /// <summary>写作检查：贴一段典型的中式英语，看建议是怎么摆出来的。</summary>
    private static void Writing((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        OpenWriting(c);
        c.Vm.Writing.LoadSampleCommand.Execute(null);

        // 模型加载 + 500ms 防抖 + 逐句推理，转久一点
        for (var i = 0; i < 8; i++)
        {
            Pump();
        }
    }

    /// <summary>词书里读到一半又去查了个别的词——书签留在原处，露出「回到书签」。</summary>
    private static void Bookmark((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        Books(c);                       // 书签落在 cancel（第一个词）
        Pump();
        c.Vm.BookFilter = "cancellation";  // 过滤掉书签那个词
        Pump();
        c.Vm.SelectedBookWord = c.Vm.BookWords[0];  // 查开另一个词——书签不跟着走
        Pump();
    }

    private static void Books((Window Window, MainViewModel Vm, FakeStudyRepository Study) c)
    {
        c.Window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        c.Vm.SelectedBook = c.Vm.Books[0];
        Pump();
    }

    /// <summary>音标窗口：音标表和听音辨词两页。</summary>
    private static void PhoneticsShot(string dir, string name, ThemeVariant variant, bool drill)
    {
        Controls.Appearance.Apply(Avalonia.Application.Current!, AppearanceSettings.Default);

        // 无头环境下没有 TTS，传 null——这里要看的是排版，不是声音
        var window = new PhoneticsWindow
        {
            DataContext = new PhoneticsViewModel(null, () => null),
            RequestedThemeVariant = variant,
            Width = 760,
            Height = 720,
        };
        window.Show();
        Pump();
        if (drill)
        {
            window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
            Pump();
            ((PhoneticsViewModel)window.DataContext!).ChooseCommand.Execute(
                ((PhoneticsViewModel)window.DataContext!).Pair.Left);
            Pump();
        }

#pragma warning disable CS0618 // 新的 BitmapEncoderOptions 重载在 12.1 尚未公开
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        window.Close();
    }

    /// <summary>设置窗口单独截：主题下拉、主题色色点、字号、字体都在这一屏。</summary>
    private static void SettingsShot(string dir, string name, ThemeVariant variant)
    {
        var app = Avalonia.Application.Current!;
        Controls.Appearance.Apply(app, AppearanceSettings.Default);

        var vm = new MainViewModel(new FakeDictionaryRepository(), new FakeUserDataRepository(), new FakeStudyRepository());
        vm.AppearanceChanged += (_, settings) => Controls.Appearance.Apply(app, settings);
        vm.InitializeAsync();
        Pump();
        vm.PickAccentCommand.Execute("violet");
        Pump();

        var window = new SettingsWindow { DataContext = vm, RequestedThemeVariant = variant };
        window.Show();
        Pump();
#pragma warning disable CS0618 // 新的 BitmapEncoderOptions 重载在 12.1 尚未公开
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        window.Close();
    }

    private static void Shot(
        string dir,
        string name,
        ThemeVariant variant,
        Action<(Window, MainViewModel, FakeStudyRepository)> arrange,
        int width = 1280,
        int height = 820,
        bool withModel = true)
    {
        // 写的是 Application.Resources（全局），上一张若改过字号或主题色会漏到下一张。
        var app = Avalonia.Application.Current!;
        Controls.Appearance.Apply(app, AppearanceSettings.Default);

        var study = new FakeStudyRepository();
        var vm = new MainViewModel(
            new FakeDictionaryRepository(), new FakeUserDataRepository(), study,
            grammar: withModel ? RealChecker() : new GectorGrammarChecker("(未安装)"),
            grammarModel: new GrammarModelInstaller());
        // 和 App.axaml.cs 走同一个入口，否则截出来的图和真实应用不是一回事
        vm.AppearanceChanged += (_, settings) => Controls.Appearance.Apply(app, settings);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, RequestedThemeVariant = variant };
        window.Show();
        vm.InitializeAsync();
        Pump();
        vm.SearchText = "取消";
        Pump();
        arrange((window, vm, study));
#pragma warning disable CS0618 // 新的 BitmapEncoderOptions 重载在 12.1 尚未公开
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        window.Close();
    }

    /// <summary>
    /// 写作页用真模型出图。装不上时（artifacts/ 不入库）退回缺席状态，
    /// 那张图照样有意义——它就是用户没装模型时看到的样子。
    /// </summary>
    private static IGrammarChecker RealChecker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "artifacts", "grammar", "gector-int8.onnx");
            if (File.Exists(candidate))
            {
                return new GectorGrammarChecker(candidate);
            }

            directory = directory.Parent;
        }

        return new GectorGrammarChecker("(未安装)");
    }

    private static void Pump()
    {
        for (var i = 0; i < 12; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
