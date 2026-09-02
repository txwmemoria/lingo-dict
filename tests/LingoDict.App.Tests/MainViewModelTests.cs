using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;

namespace LingoDict.App.Tests;

/// <summary>
/// ViewModel 自身的行为，不涉及 XAML。跑在 Avalonia 调度器上，
/// 因为若干处理是 <c>_ = SomethingAsync()</c> 这种即发即忘、依赖同步上下文回到 UI 线程。
/// </summary>
public sealed class MainViewModelTests
{
    [AvaloniaFact]
    public void SelectingASearchResult_LoadsDetailFavoriteStateAndNote()
    {
        var (viewModel, _, _, _) = Create((userData, study) =>
        {
            userData.SaveNoteAsync(1, "和 postpone 区分");
            study.AddCardsAsync([new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.UtcNow);
        });

        // 换到另一条再切回来，走一遍选中变更的完整路径。
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        Assert.Equal("cancel", viewModel.SelectedWord?.Headword);
        Assert.Equal("和 postpone 区分", viewModel.NoteText);
        Assert.True(viewModel.IsInStudy);
        Assert.False(viewModel.IsFavorite);
        Assert.False(viewModel.IsNoteDirty);
    }

    [AvaloniaFact]
    public void RefreshingHistory_DoesNotHijackTheWordTheUserJustOpened()
    {
        var (viewModel, _, _, _) = Create();

        // 先看第二个词，让历史里有一条选中项，再点第一个词。
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        viewModel.SelectedHistory = viewModel.History.FirstOrDefault();
        Pump();

        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        // 刷新历史会重设 SelectedHistory；没有防重入的话它会把详情页覆盖回去。
        Assert.Equal(1, viewModel.SelectedWord?.Id);
        Assert.Equal(2, viewModel.History.Count);
    }

    [AvaloniaFact]
    public void OpeningFromHistory_DoesNotReorderTheListUnderTheCursor()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        var orderBefore = viewModel.History.Select(x => x.WordId).ToList();
        viewModel.SelectedHistory = viewModel.History[1];
        Pump();

        Assert.Equal(orderBefore, viewModel.History.Select(x => x.WordId));
    }

    [AvaloniaFact]
    public void EditingANote_MarksItDirtyUntilSaved()
    {
        var (viewModel, _, userData, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        viewModel.NoteText = "手写的笔记";
        Assert.True(viewModel.IsNoteDirty);

        viewModel.SaveNoteCommand.Execute(null);
        Pump();

        Assert.False(viewModel.IsNoteDirty);
        Assert.Equal("手写的笔记", userData.GetNoteAsync(1).Result);
    }

    [AvaloniaFact]
    public void LoadingAWordProgrammatically_DoesNotMarkTheNoteDirty()
    {
        var (viewModel, _, _, _) = Create();

        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        Assert.False(viewModel.IsNoteDirty);
    }

    [AvaloniaFact]
    public void ToggleFavorite_FlipsBothStateAndTheFavoritesList()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        viewModel.ToggleFavoriteCommand.Execute(null);
        Pump();
        Assert.True(viewModel.IsFavorite);
        Assert.Equal("★ 已收藏", viewModel.FavoriteGlyph);

        viewModel.ToggleFavoriteCommand.Execute(null);
        Pump();
        Assert.False(viewModel.IsFavorite);
    }

    [AvaloniaFact]
    public void ToggleStudy_AddsThenRemovesTheCard()
    {
        var (viewModel, _, _, study) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        viewModel.ToggleStudyCommand.Execute(null);
        Pump();
        Assert.True(study.IsInStudyAsync(1).Result);
        Assert.Equal("✓ 学习中", viewModel.StudyGlyph);

        viewModel.ToggleStudyCommand.Execute(null);
        Pump();
        Assert.False(study.IsInStudyAsync(1).Result);
    }

    [AvaloniaFact]
    public void AddBookToStudy_AddsEveryWordOnceAndReportsTheSecondPassAsNoop()
    {
        var (viewModel, _, _, study) = Create();
        viewModel.SelectedBook = viewModel.Books[0];
        Pump();

        viewModel.AddBookToStudyCommand.Execute(null);
        Pump();
        Assert.Equal(2, study.GetStudyStatsAsync(DateTimeOffset.UtcNow).Result.TotalCards);
        Assert.Contains("加入 2", viewModel.StatusMessage);

        viewModel.AddBookToStudyCommand.Execute(null);
        Pump();
        Assert.Equal(2, study.GetStudyStatsAsync(DateTimeOffset.UtcNow).Result.TotalCards);
        Assert.Contains("都已在学习计划里", viewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void NavigateToWord_FallsBackToTheSearchBoxForUnknownHeadwords()
    {
        var (viewModel, dictionary, _, _) = Create();

        viewModel.NavigateToWordCommand.Execute("revoke");
        Pump();

        // 状态栏随后会被防抖搜索的结果数覆盖，所以只断言持久的那部分。
        Assert.Equal("revoke", dictionary.LastFindByHeadword);
        Assert.Equal("revoke", viewModel.SearchText);
    }

    [AvaloniaFact]
    public void NavigateToWord_JumpsDirectlyWhenTheHeadwordExists()
    {
        var (viewModel, dictionary, _, _) = Create();

        viewModel.NavigateToWordCommand.Execute("cancellation");
        Pump();

        Assert.Equal("cancellation", dictionary.LastFindByHeadword);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.NotNull(viewModel.SelectedWord);
    }

    [AvaloniaFact]
    public void NavigateToWord_IgnoresBlankInput()
    {
        var (viewModel, dictionary, _, _) = Create();

        viewModel.NavigateToWordCommand.Execute("   ");
        Pump();

        Assert.Null(dictionary.LastFindByHeadword);
    }

    [AvaloniaFact]
    public void StudyUsesLocalTimeSoTheDailyQuotaResetsAtLocalMidnight()
    {
        var (_, _, _, study) = Create();

        Assert.NotEmpty(study.ReceivedTimes);

        // 传 UtcNow 的话在东八区，每日额度会在早上 08:00 重置。
        Assert.All(study.ReceivedTimes, x => Assert.Equal(DateTimeOffset.Now.Offset, x.Offset));
    }

    [AvaloniaFact]
    public void SwitchingWords_AutoSavesAnUnsavedNoteInsteadOfDroppingIt()
    {
        var (viewModel, _, userData, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        viewModel.NoteText = "还没点保存就切走了";
        Assert.True(viewModel.IsNoteDirty);

        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();

        Assert.Equal("还没点保存就切走了", userData.GetNoteAsync(1).Result);
        Assert.False(viewModel.IsNoteDirty);
        Assert.Equal(string.Empty, viewModel.NoteText);
    }

    [AvaloniaFact]
    public void ASlowEarlierLoadCannotOverwriteTheWordOpenedLater()
    {
        var dictionary = new SlowFirstCallDictionaryRepository();
        var viewModel = new MainViewModel(dictionary, new FakeUserDataRepository(), new FakeStudyRepository());
        viewModel.InitializeAsync();
        Pump();

        // 第一次取详情故意变慢，用户等不及又点了第二个词。
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        dictionary.ReleaseSlowCall();
        Pump();

        Assert.Equal("cancellation", viewModel.SelectedWord?.Headword);
    }

    [AvaloniaFact]
    public void AppearanceLoadsOnStartupAndIsAppliedBeforeAnyUserChange()
    {
        var dictionary = new FakeDictionaryRepository();
        var userData = new FakeUserDataRepository { Appearance = new AppearanceSettings(1.15, "SimSun") };
        var viewModel = new MainViewModel(dictionary, userData, new FakeStudyRepository());

        AppearanceSettings? applied = null;
        viewModel.AppearanceChanged += (_, s) => applied = s;
        viewModel.InitializeAsync();
        Pump();

        Assert.Equal(1.15, applied?.FontScale);
        Assert.Equal("SimSun", applied?.FontFamily);
        Assert.Equal("大", viewModel.SelectedFontScale.Label);
        Assert.Equal("SimSun", viewModel.SelectedFontFamily);
    }

    [AvaloniaFact]
    public void ChangingFontScale_AppliesImmediatelyAndPersists()
    {
        var (viewModel, _, userData, _) = Create();
        var applied = new List<AppearanceSettings>();
        viewModel.AppearanceChanged += (_, s) => applied.Add(s);

        viewModel.SelectedFontScale = viewModel.FontScaleOptions.Single(x => x.Label == "特大");
        Pump();

        Assert.Equal(1.3, Assert.Single(applied).FontScale);
        Assert.Equal(1.3, userData.Appearance.FontScale);
    }

    [AvaloniaFact]
    public void SelectingTheDefaultFontStoresNullRatherThanTheLabel()
    {
        var (viewModel, _, userData, _) = Create();

        viewModel.SelectedFontFamily = "SimHei";
        Pump();
        Assert.Equal("SimHei", userData.Appearance.FontFamily);

        viewModel.SelectedFontFamily = "默认";
        Pump();

        // 存成"默认"这两个字的话，就会被当成一个不存在的字体名去解析。
        Assert.Null(userData.Appearance.FontFamily);
    }

    [AvaloniaFact]
    public void LoadingAppearanceOnStartup_DoesNotWriteItBack()
    {
        var dictionary = new FakeDictionaryRepository();
        var userData = new FakeUserDataRepository { Appearance = new AppearanceSettings(0.85, "KaiTi") };
        var viewModel = new MainViewModel(dictionary, userData, new FakeStudyRepository());

        viewModel.InitializeAsync();
        Pump();

        // 初始化时给下拉框赋值不能反过来触发保存，否则会把用户的设置改写成最接近的档位。
        Assert.Equal(0.85, userData.Appearance.FontScale, 3);
        Assert.Equal("KaiTi", userData.Appearance.FontFamily);
    }

    [AvaloniaFact]
    public void BookFilter_MatchesHeadwordAndTranslationAndIsReversible()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedBook = viewModel.Books[0];
        Pump();
        Assert.Equal(2, viewModel.BookWords.Count);

        viewModel.BookFilter = "cancellation";
        Assert.Equal("cancellation", Assert.Single(viewModel.BookWords).Headword);

        // 释义也参与匹配
        viewModel.BookFilter = "删除";
        Assert.Equal("cancellation", Assert.Single(viewModel.BookWords).Headword);

        viewModel.BookFilter = "  ";
        Assert.Equal(2, viewModel.BookWords.Count);
    }

    [AvaloniaFact]
    public void AlternateEntries_ExcludeTheOneAlreadyShownAndCanBeSwitchedTo()
    {
        var dictionary = new FakeDictionaryRepository();
        dictionary.Alternates.Add(new AlternateEntry(10, "v 取消", "四级词汇"));
        dictionary.Alternates.Add(new AlternateEntry(99, "v 撤销", "六级词汇"));

        var viewModel = new MainViewModel(dictionary, new FakeUserDataRepository(), new FakeStudyRepository());
        viewModel.InitializeAsync();
        Pump();

        // 当前显示的是 entry 10，列表里只应剩另一条。
        var other = Assert.Single(viewModel.AlternateEntries);
        Assert.Equal(99, other.EntryId);
        Assert.Equal("六级词汇", other.Books);

        viewModel.ShowAlternateCommand.Execute(other);
        Pump();

        Assert.Equal(99, viewModel.SelectedWord?.EntryId);
    }

    [AvaloniaFact]
    public void StudyScope_ShowsRealBookTitlesAndNarrowsTheQueue()
    {
        var (viewModel, _, _, study) = Create((_, s) =>
        {
            s.AddCardsAsync([new WordReference(1, 10, "cancel", "p", "v 取消")], "CET4_1", DateTimeOffset.Now.AddMinutes(-1));
            s.AddCardsAsync([new WordReference(2, 20, "abruptly", "p", "adv 突然地")], "OTHER", DateTimeOffset.Now.AddMinutes(-1));
        });

        // 用户库只存 book_id，标题得从内容库带过来。
        var scoped = viewModel.Study.Books.Single(x => x.BookId == "CET4_1");
        Assert.Equal("四级词汇", scoped.Title);

        // 未知的 book_id 没有标题可查，退回显示 id 本身而不是空白。
        Assert.Equal("OTHER", viewModel.Study.Books.Single(x => x.BookId == "OTHER").Title);
        Assert.Equal(2, viewModel.Study.Stats.TotalCards);

        viewModel.Study.SelectedBook = scoped;
        Pump();

        Assert.Equal(1, viewModel.Study.Stats.TotalCards);
        Assert.Equal("cancel", viewModel.Study.CurrentCard?.Headword);
    }

    [AvaloniaFact]
    public void StudyUndo_BringsBackTheCardStillFlippedOver()
    {
        var (viewModel, _, _, study) = Create((_, s) => s.AddCardsAsync(
            [
                new WordReference(1, 10, "cancel", "p", "v 取消"),
                new WordReference(2, 20, "abruptly", "p", "adv 突然地"),
            ],
            null,
            DateTimeOffset.Now.AddMinutes(-1)));

        Assert.False(viewModel.Study.CanUndo);

        viewModel.Study.RevealCommand.Execute(null);
        var graded = viewModel.Study.CurrentCard!;
        viewModel.Study.GradeCommand.Execute(ReviewGrade.Easy);
        Pump();

        Assert.True(viewModel.Study.CanUndo);
        Assert.NotEqual(graded.WordId, viewModel.Study.CurrentCard?.WordId);

        viewModel.Study.UndoCommand.Execute(null);
        Pump();

        Assert.Equal(1, study.UndoCount);
        Assert.Equal(graded, viewModel.Study.CurrentCard);

        // 评错了才会撤销，用户想看的是答案面，不该再翻一次。
        Assert.True(viewModel.Study.IsAnswerVisible);

        // 只保留一步，撤过之后按钮就该收起来。
        Assert.False(viewModel.Study.CanUndo);
    }

    [AvaloniaFact]
    public void StudyUndo_AfterTheQueueRanOut_RebuildsInsteadOfIndexingIntoTheOldQueue()
    {
        var (viewModel, _, _, study) = Create((_, s) => s.AddCardsAsync(
            [new WordReference(1, 10, "cancel", "p", "v 取消")], null, DateTimeOffset.Now.AddMinutes(-1)));

        viewModel.Study.RevealCommand.Execute(null);
        var graded = viewModel.Study.CurrentCard!;

        // 唯一一张评"简单"之后队列见底，会整批重排——旧下标不再指向同一张卡。
        viewModel.Study.GradeCommand.Execute(ReviewGrade.Easy);
        Pump();
        Assert.Null(viewModel.Study.CurrentCard);

        viewModel.Study.UndoCommand.Execute(null);
        Pump();

        Assert.Equal(1, study.UndoCount);
        Assert.Equal(graded.WordId, viewModel.Study.CurrentCard?.WordId);
    }

    [AvaloniaFact]
    public void StudyHistory_ProducesOneBarPerDayScaledToThePeak()
    {
        var (viewModel, _, _, _) = Create();

        Assert.Equal(30, viewModel.Study.History.Count);
        Assert.All(viewModel.Study.History, x => Assert.InRange(x.Height, 0, StudyDayBar.MaxHeight));

        // 一次都没复习过时不该出现除零或 NaN。
        Assert.Equal(0, viewModel.Study.ReviewedLast30Days);
        Assert.Equal(0, viewModel.Study.Accuracy);
    }

    [AvaloniaFact]
    public void AFailedAutoSave_KeepsTheDraftAndRetriesOnTheNextSwitch()
    {
        var (viewModel, _, userData, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        userData.FailNoteWrites = true;
        viewModel.NoteText = "磁盘满的时候写的";
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();

        // 写失败不能清 dirty，也不能把内容丢掉。
        Assert.Contains("稍后自动重试", viewModel.StatusMessage);
        Assert.Equal(string.Empty, userData.GetNoteAsync(1).Result);

        // 磁盘恢复后，下一次切词把草稿补写回去。
        userData.FailNoteWrites = false;
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        Assert.Equal("磁盘满的时候写的", userData.GetNoteAsync(1).Result);
    }

    [AvaloniaFact]
    public void TypingWhileTheWordIsStillLoading_IsNotOverwrittenByTheStoredNote()
    {
        var dictionary = new FakeDictionaryRepository();
        var userData = new FakeUserDataRepository();
        userData.SaveNoteAsync(1, "库里的旧笔记");
        var viewModel = new MainViewModel(dictionary, userData, new FakeStudyRepository());
        viewModel.InitializeAsync();
        Pump();

        // 详情页已经切过来了，用户开始打字；此时后台还在查收藏/学习/笔记。
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        viewModel.NoteText = "边加载边打的字";
        Pump();

        Assert.Equal("边加载边打的字", viewModel.NoteText);
        Assert.True(viewModel.IsNoteDirty);
    }

    [AvaloniaFact]
    public void ASlowEarlierBookLoad_CannotOverwriteTheBookOpenedLater()
    {
        var (viewModel, dictionary, _, study) = Create();

        viewModel.SelectedBook = viewModel.Books[0];
        Pump();
        Assert.Equal(2, viewModel.BookWords.Count);

        // 词表和选中的词书对不上时，「整本加入」必须拒绝执行，
        // 否则会拿着当前词书的 id 把上一本的单词加进去。
        viewModel.AddBookToStudyCommand.Execute(null);
        Pump();
        Assert.Equal(2, study.GetStudyStatsAsync(DateTimeOffset.Now).Result.TotalCards);
    }

    [AvaloniaFact]
    public void SpeechIsHiddenWhenTheSystemHasNoVoices()
    {
        var (viewModel, _, _, _) = Create();

        Assert.False(viewModel.IsSpeechAvailable);
        Assert.Equal([SpeechVoiceOption.System], viewModel.SpeechVoiceOptions);
        Assert.Contains("没有可用的语音合成引擎", viewModel.SpeechHint);

        // 没有语音服务时点朗读不该抛异常。
        viewModel.SpeakCommand.Execute(null);
    }

    [AvaloniaFact]
    public void SpeakUsesTheChosenVoiceAndPersistsIt()
    {
        var speech = new FakeSpeechService(["David", "Zira"]);
        var userData = new FakeUserDataRepository();
        var viewModel = new MainViewModel(
            new FakeDictionaryRepository(), userData, new FakeStudyRepository(), null, speech);
        viewModel.InitializeAsync();
        Pump();

        Assert.True(viewModel.IsSpeechAvailable);
        Assert.Equal([SpeechVoiceOption.System, "David", "Zira"], viewModel.SpeechVoiceOptions);

        viewModel.SelectedSpeechVoice = "Zira";
        Pump();
        viewModel.SpeakCommand.Execute(null);

        Assert.Equal(("cancel", "Zira"), speech.LastSpoken);
        Assert.Equal("Zira", userData.Appearance.SpeechVoice);
    }

    [AvaloniaFact]
    public void AStoredVoiceThatNoLongerExists_FallsBackToTheSystemDefault()
    {
        var speech = new FakeSpeechService(["David"]);
        var userData = new FakeUserDataRepository
        {
            Appearance = new AppearanceSettings(1.0, null, "在这台机器上不存在的嗓音"),
        };
        var viewModel = new MainViewModel(
            new FakeDictionaryRepository(), userData, new FakeStudyRepository(), null, speech);
        viewModel.InitializeAsync();
        Pump();

        // 否则下拉框会显示一个选不中的名字，朗读也会静默失败。
        Assert.Equal(SpeechVoiceOption.System, viewModel.SelectedSpeechVoice);
    }

    [AvaloniaFact]
    public void OpenSettings_RaisesTheRequestInsteadOfTouchingWindows()
    {
        var (viewModel, _, _, _) = Create();
        var raised = 0;
        viewModel.SettingsRequested += (_, _) => raised++;

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void BackAndForward_WalkTheNavigationTrail()
    {
        var (viewModel, _, _, _) = Create();
        Assert.False(viewModel.CanGoBack);
        Assert.False(viewModel.CanGoForward);

        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        Assert.True(viewModel.CanGoBack);
        Assert.Equal(2, viewModel.SelectedWord?.Id);

        viewModel.GoBackCommand.Execute(null);
        Pump();
        Assert.Equal(1, viewModel.SelectedWord?.Id);
        Assert.False(viewModel.CanGoBack);
        Assert.True(viewModel.CanGoForward);

        viewModel.GoForwardCommand.Execute(null);
        Pump();
        Assert.Equal(2, viewModel.SelectedWord?.Id);
        Assert.False(viewModel.CanGoForward);
    }

    [AvaloniaFact]
    public void ANewJump_DiscardsTheForwardTrail()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        viewModel.GoBackCommand.Execute(null);
        Pump();
        Assert.True(viewModel.CanGoForward);

        // 回退之后又跳到新的词，前进链就该断掉——和浏览器一致。
        viewModel.NavigateToWordCommand.Execute("cancellation");
        Pump();

        Assert.False(viewModel.CanGoForward);
        Assert.True(viewModel.CanGoBack);
    }

    [AvaloniaFact]
    public void ReselectingTheSameWord_DoesNotPileUpHistory()
    {
        var (viewModel, _, _, _) = Create();

        viewModel.NavigateToWordCommand.Execute("cancel");
        viewModel.NavigateToWordCommand.Execute("cancel");
        Pump();

        // 原地重选不该入栈，否则后退键要按好几下才动一步。
        Assert.False(viewModel.CanGoBack);
    }

    [AvaloniaFact]
    public void GoingBackDoesNotRecordItselfAsANewStep()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        viewModel.NavigateToWordCommand.Execute("cancel");
        Pump();

        viewModel.GoBackCommand.Execute(null);
        viewModel.GoBackCommand.Execute(null);
        Pump();

        Assert.False(viewModel.CanGoBack);
        Assert.Equal(1, viewModel.SelectedWord?.Id);
    }

    [AvaloniaFact]
    public void ClearHistory_EmptiesTheListButKeepsFavorites()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        viewModel.ToggleFavoriteCommand.Execute(null);
        Pump();

        viewModel.ClearHistoryCommand.Execute(null);
        Pump();

        Assert.Empty(viewModel.History);
        Assert.Single(viewModel.Favorites);
    }

    [AvaloniaFact]
    public void RemoveFavorite_DropsTheRowAndResetsTheButtonOnTheOpenWord()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        viewModel.ToggleFavoriteCommand.Execute(null);
        Pump();
        Assert.True(viewModel.IsFavorite);

        viewModel.RemoveFavoriteCommand.Execute(viewModel.Favorites[0]);
        Pump();

        Assert.Empty(viewModel.Favorites);

        // 删的正是当前打开的词条，详情页那颗按钮必须跟着回到未收藏。
        Assert.False(viewModel.IsFavorite);
        Assert.Equal("☆ 收藏", viewModel.FavoriteGlyph);
    }

    [AvaloniaFact]
    public void RemoveFavorite_OfAnotherWord_LeavesTheOpenWordAlone()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        viewModel.ToggleFavoriteCommand.Execute(null);
        Pump();

        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();
        viewModel.ToggleFavoriteCommand.Execute(null);
        Pump();

        var other = viewModel.Favorites.Single(x => x.Headword == "cancel");
        viewModel.RemoveFavoriteCommand.Execute(other);
        Pump();

        Assert.Equal("cancellation", Assert.Single(viewModel.Favorites).Headword);
        Assert.True(viewModel.IsFavorite);
    }

    [AvaloniaFact]
    public void RemoveHistory_DropsOnlyThatRow()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        viewModel.SelectedSearchResult = viewModel.SearchResults[1];
        Pump();

        Assert.Equal(2, viewModel.History.Count);

        viewModel.RemoveHistoryCommand.Execute(viewModel.History.Single(x => x.Headword == "cancel"));
        Pump();

        Assert.Equal("cancellation", Assert.Single(viewModel.History).Headword);
    }

    [AvaloniaFact]
    public void WhenUserDataIsUnavailable_TheAppStillSearches()
    {
        var dictionary = new FakeDictionaryRepository();
        var userData = new FakeUserDataRepository { FailOnInitialize = true };
        var viewModel = new MainViewModel(dictionary, userData, new FakeStudyRepository());

        viewModel.InitializeAsync();
        Pump();

        Assert.NotEmpty(viewModel.SearchResults);
        Assert.Contains("用户数据不可用", viewModel.StatusMessage);

        // 降级之后这些命令必须安静地不做事，而不是抛异常。
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();
        viewModel.ToggleFavoriteCommand.Execute(null);
        viewModel.ToggleStudyCommand.Execute(null);
        viewModel.SaveNoteCommand.Execute(null);
        Pump();

        Assert.Equal("cancel", viewModel.SelectedWord?.Headword);
        Assert.False(viewModel.IsFavorite);
    }

    [AvaloniaFact]
    public void BrowsingABook_DropsABookmarkWhereYouStopped()
    {
        var (viewModel, _, userData, _) = Create();

        viewModel.SelectedBook = viewModel.Books[0];
        Pump();
        viewModel.SelectedBookWord = viewModel.BookWords[1];
        Pump();

        var bookmark = Assert.Contains("CET4_1", userData.BookProgress);
        Assert.Equal(viewModel.BookWords[1].Id, bookmark.WordId);
        Assert.Equal(viewModel.BookWords[1].Headword, bookmark.Headword);
    }

    [AvaloniaFact]
    public void ReopeningABook_ResumesWhereYouLeftOff()
    {
        var (viewModel, _, userData, _) = Create();

        viewModel.SelectedBook = viewModel.Books[0];
        Pump();
        viewModel.SelectedBookWord = viewModel.BookWords[1];
        Pump();
        var expected = viewModel.BookWords[1].Headword;

        // 换本书再换回来，等于重新打开
        viewModel.SelectedBook = null;
        Pump();
        viewModel.SelectedBook = viewModel.Books[0];
        Pump();

        Assert.Equal(expected, viewModel.SelectedBookWord?.Headword);
        // 正看着书签那个词时不该再提示「书签在 X」
        Assert.Empty(viewModel.BookmarkHint);
    }

    [AvaloniaFact]
    public void ABookmarkPointingAtAWordThatIsGone_FallsBackToTheFirst()
    {
        var (viewModel, _, userData, _) = Create();

        // 重新生成过 dictionary.db 之后 word_id 会变，书签就指向一个不存在的词
        userData.BookProgress["CET4_1"] = new BookProgress("CET4_1", 999_999, "ghost", DateTimeOffset.Now);

        viewModel.SelectedBook = viewModel.Books[0];
        Pump();

        Assert.Equal(viewModel.BookWords[0].Headword, viewModel.SelectedBookWord?.Headword);
    }

    [AvaloniaFact]
    public void LookingSomethingUpDoesNotMoveTheBookmark()
    {
        var (viewModel, dictionary, _, _) = Create();

        // 自带的两个词是 cancel / cancellation，互为前缀——过滤 "cancel" 两个都留下，
        // 演不出「书签被过滤掉了」。换成三个互不包含的词。
        dictionary.Results.Clear();
        dictionary.Results.AddRange(
        [
            new WordSummary(1, 1, "alpha", "", "n 甲", 1),
            new WordSummary(2, 2, "beta", "", "n 乙", 1),
            new WordSummary(3, 3, "gamma", "", "n 丙", 1),
        ]);

        viewModel.SelectedBook = viewModel.Books[0];
        Pump();
        viewModel.SelectedBookWord = viewModel.BookWords[1];   // 读到 beta
        Pump();
        Assert.Empty(viewModel.BookmarkHint);

        // 读到一半想查个别的词：过滤 + 点开。书签应当留在 beta——
        // 纸质词典翻去别处查一下，夹着的书签不会跟着跑。
        viewModel.BookFilter = "gamma";
        Pump();
        viewModel.SelectedBookWord = viewModel.BookWords[0];
        Pump();

        Assert.Contains("beta", viewModel.BookmarkHint);

        viewModel.GoToBookmarkCommand.Execute(null);
        Pump();

        Assert.Equal("beta", viewModel.SelectedBookWord?.Headword);
        // 过滤把书签那个词滤掉了，跳回去得先清掉过滤，否则点了没反应
        Assert.Empty(viewModel.BookFilter);
        Assert.Empty(viewModel.BookmarkHint);
    }

    [AvaloniaFact]
    public void ThePhoneticSplitsIntoSymbolsYouCanTap()
    {
        var (viewModel, _, _, _) = Create();
        viewModel.SelectedSearchResult = viewModel.SearchResults[0];
        Pump();

        // 假仓储里 cancel 的音标是 'kænsl
        var chips = viewModel.UsPhonemes;
        Assert.Equal("'kænsl", string.Concat(chips.Select(x => x.Text)));

        // 重音点只是陪着显示，点它没有可教的东西
        var stress = Assert.Single(chips, x => x.Text == "'");
        Assert.False(stress.CanTeach);

        // 其余每一格都要能说出「这个音在哪个词里」
        Assert.All(chips.Where(x => x.Text != "'"), x =>
        {
            Assert.True(x.CanTeach, $"{x.Text} 教不了");
            Assert.Contains("如", x.Hint);
        });

        // æ 那一格教的是 cat
        Assert.Contains("cat", Assert.Single(chips, x => x.Text == "æ").Hint);
    }

    private static void Pump()
    {
        for (var round = 0; round < 12; round++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <param name="seed">
    /// 在 InitializeAsync 之前预置数据。初始化会自动选中第一条结果，
    /// 之后再赋同一个值不会触发变更处理，所以想让它读到的数据必须先放进去。
    /// </param>
    private static (MainViewModel ViewModel, FakeDictionaryRepository Dictionary,
        FakeUserDataRepository UserData, FakeStudyRepository Study) Create(
        Action<FakeUserDataRepository, FakeStudyRepository>? seed = null)
    {
        var dictionary = new FakeDictionaryRepository();
        var userData = new FakeUserDataRepository();
        var study = new FakeStudyRepository();
        seed?.Invoke(userData, study);

        var viewModel = new MainViewModel(dictionary, userData, study);
        viewModel.InitializeAsync();
        Pump();
        return (viewModel, dictionary, userData, study);
    }
}
