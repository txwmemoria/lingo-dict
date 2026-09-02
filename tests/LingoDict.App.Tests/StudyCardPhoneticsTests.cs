using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LingoDict.App.ViewModels;
using LingoDict.Core.Models;

namespace LingoDict.App.Tests;

/// <summary>
/// 复习卡片上的发音和音标。
/// </summary>
/// <remarks>
/// 卡片原本连朗读按钮都没有——只能干看着拼写回忆，
/// 而「不知道怎么读」恰恰是背单词时最常卡住的地方。
/// </remarks>
public sealed class StudyCardPhoneticsTests
{
    private static (StudyViewModel Study, FakeSpeechService Speech) Create(bool withVoices = true)
    {
        var speech = new FakeSpeechService(withVoices ? ["Zira"] : []);
        var repository = new FakeStudyRepository();
        repository.AddCardsAsync(
            [new WordReference(1, 10, "cancel", "'kænsl", "v 取消")], null, DateTimeOffset.Now.AddMinutes(-1));

        var study = new StudyViewModel(repository, speech, () => "Zira");
        study.RefreshAsync();
        Pump();
        return (study, speech);
    }

    [AvaloniaFact]
    public void TheCardsPhoneticSplitsIntoTappableSymbols()
    {
        var (study, _) = Create();

        Assert.Equal("'kænsl", string.Concat(study.CardPhonemes.Select(x => x.Text)));
        Assert.Contains("cat", Assert.Single(study.CardPhonemes, x => x.Text == "æ").Hint);
    }

    [AvaloniaFact]
    public void TappingASymbolReadsItsExampleWordNotTheCard()
    {
        var (study, speech) = Create();

        study.SpeakPhonemeCommand.Execute(study.CardPhonemes.Single(x => x.Text == "æ"));

        // 念的是例词 cat，不是卡片上的 cancel——孤立音素 TTS 念不了，
        // 而且放在熟词里本来就更好记
        Assert.Equal(("cat", "Zira"), speech.LastSpoken);
    }

    [AvaloniaFact]
    public void TheCardCanBeReadAloudBeforeItIsFlipped()
    {
        var (study, speech) = Create();

        Assert.False(study.IsAnswerVisible);
        Assert.True(study.CanSpeak);

        study.SpeakCommand.Execute(null);

        // 翻面前就能听：听音回忆本来也是一种复习方式
        Assert.Equal(("cancel", "Zira"), speech.LastSpoken);
    }

    [AvaloniaFact]
    public void WithoutAVoiceTheSpeakerIsHiddenInsteadOfDead()
    {
        var (study, _) = Create(withVoices: false);

        Assert.False(study.CanSpeak);
    }

    [AvaloniaFact]
    public void NoCardMeansNothingToSpeakOrSplit()
    {
        var study = new StudyViewModel();

        Assert.Null(study.CurrentCard);
        Assert.False(study.CanSpeak);
        Assert.Empty(study.CardPhonemes);
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
}
