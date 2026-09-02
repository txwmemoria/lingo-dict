using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoDict.App.Services;
using LingoDict.Core.Text;

namespace LingoDict.App.ViewModels;

/// <summary>音标表里的一格。</summary>
public sealed record PhonemeCard(Phoneme Phoneme)
{
    public string Symbol => Phoneme.Symbol;

    public string Example => Phoneme.Example;

    public string Tip => Phoneme.Tip;

    public bool HasTip => Phoneme.Tip.Length > 0;
}

/// <summary>
/// 音标表 + 听音辨词。
/// </summary>
/// <remarks>
/// 两件事放一个窗口：一个是「系统过一遍」，一个是「练能不能听出来」。
/// 认得符号却听不出区别的人很多，所以后者不是附赠，是另一半。
/// </remarks>
public partial class PhoneticsViewModel : ViewModelBase
{
    private readonly ISpeechService? _speech;
    private readonly Func<string?> _voice;
    private readonly Random _random = new();

    public PhoneticsViewModel()
        : this(null, () => null)
    {
    }

    public PhoneticsViewModel(ISpeechService? speech, Func<string?> voice)
    {
        _speech = speech;
        _voice = voice;

        static IReadOnlyList<PhonemeCard> Group(PhonemeKind kind) =>
        [
            .. Phonetics.All
                .Where(x => x.Kind == kind)
                // 同一个音在美音英音里有两种写法（ɡ/g、ɛ/e、oʊ/əʊ/o），
                // 按「例词 + 是词里哪几个字母」去重，否则表上会并排出现
                // 好几张看着一模一样的卡。注意 g 和 o 都拿 go 当例词，
                // 但教的是词里不同的部分，所以拼写也得进 key。
                .GroupBy(x => (x.Example, x.Spelling))
                .Select(x => new PhonemeCard(x.First())),
        ];

        Vowels = Group(PhonemeKind.Vowel);
        Diphthongs = Group(PhonemeKind.Diphthong);
        Consonants = Group(PhonemeKind.Consonant);

        NextRound();
    }

    public IReadOnlyList<PhonemeCard> Vowels { get; }

    public IReadOnlyList<PhonemeCard> Diphthongs { get; }

    public IReadOnlyList<PhonemeCard> Consonants { get; }

    // ——— 听音辨词 ———

    [ObservableProperty]
    private MinimalPair _pair = MinimalPairs.All[0];

    /// <summary>这一轮实际念的是哪个词。用户选完之前不显示。</summary>
    [ObservableProperty]
    private string _answer = string.Empty;

    [ObservableProperty]
    private bool _isAnswered;

    [ObservableProperty]
    private bool _wasRight;

    [ObservableProperty]
    private int _correct;

    [ObservableProperty]
    private int _asked;

    public string Score => Asked == 0 ? "" : $"{Correct} / {Asked}";

    /// <summary>朗读一个词。音标表和辨词练习都走这里。</summary>
    [RelayCommand]
    private void Speak(string? word)
    {
        if (_speech is not null && !string.IsNullOrWhiteSpace(word))
        {
            _speech.Speak(word, _voice());
        }
    }

    /// <summary>再念一遍这一轮的词。没听清是常态，不该逼人猜。</summary>
    [RelayCommand]
    private void Replay() => Speak(Answer);

    [RelayCommand]
    private void Choose(string? word)
    {
        if (IsAnswered || string.IsNullOrEmpty(word))
        {
            return;
        }

        Asked++;
        WasRight = word == Answer;
        if (WasRight)
        {
            Correct++;
        }

        IsAnswered = true;
        OnPropertyChanged(nameof(Score));
    }

    [RelayCommand]
    private void NextRound()
    {
        Pair = MinimalPairs.All[_random.Next(MinimalPairs.All.Count)];

        // 左右各一半概率，否则听几轮就能靠位置猜
        Answer = _random.Next(2) == 0 ? Pair.Left : Pair.Right;
        IsAnswered = false;
        WasRight = false;
        Speak(Answer);
    }
}
