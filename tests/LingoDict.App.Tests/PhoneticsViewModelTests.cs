using LingoDict.App.ViewModels;
using LingoDict.Core.Text;

namespace LingoDict.App.Tests;

/// <summary>音标表和听音辨词。无头环境没有 TTS，传 null 即可——这里验的是逻辑。</summary>
public sealed class PhoneticsViewModelTests
{
    private static PhoneticsViewModel Create() => new(null, () => null);

    [Fact]
    public void TheChartCoversAllThreeGroups()
    {
        var model = Create();

        Assert.NotEmpty(model.Vowels);
        Assert.NotEmpty(model.Diphthongs);
        Assert.NotEmpty(model.Consonants);

        // 同一个音在美音英音里有两种写法（ɡ/g、ɛ/e、oʊ/əʊ）。
        // 不去重的话表上会并排出现几张看着一样的卡。
        var all = model.Vowels.Concat(model.Diphthongs).Concat(model.Consonants).ToList();
        Assert.Equal(all.Count, all.Select(x => (x.Example, x.Phoneme.Spelling)).Distinct().Count());
    }

    [Fact]
    public void EachRoundSpeaksOneOfTheTwoWords()
    {
        var model = Create();

        for (var i = 0; i < 30; i++)
        {
            Assert.True(
                model.Answer == model.Pair.Left || model.Answer == model.Pair.Right,
                $"念的 {model.Answer} 不在 {model.Pair.Left}/{model.Pair.Right} 里");
            model.NextRoundCommand.Execute(null);
        }
    }

    [Fact]
    public void BothSidesGetPicked()
    {
        // 总念左边的话，听几轮就能靠位置猜，练的就不是耳朵了
        var model = Create();
        var left = 0;
        for (var i = 0; i < 200; i++)
        {
            if (model.Answer == model.Pair.Left)
            {
                left++;
            }

            model.NextRoundCommand.Execute(null);
        }

        Assert.InRange(left, 60, 140);
    }

    [Fact]
    public void ScoringCountsRightAndWrong()
    {
        var model = Create();

        model.ChooseCommand.Execute(model.Answer);
        Assert.True(model.WasRight);
        Assert.Equal("1 / 1", model.Score);

        model.NextRoundCommand.Execute(null);
        var wrong = model.Answer == model.Pair.Left ? model.Pair.Right : model.Pair.Left;
        model.ChooseCommand.Execute(wrong);
        Assert.False(model.WasRight);
        Assert.Equal("1 / 2", model.Score);
    }

    [Fact]
    public void AnsweringTwiceInOneRoundDoesNotPadTheScore()
    {
        var model = Create();

        model.ChooseCommand.Execute(model.Answer);
        model.ChooseCommand.Execute(model.Answer);

        Assert.Equal("1 / 1", model.Score);
    }

    [Fact]
    public void EveryPairContrastsTwoRealPhonemes()
    {
        Assert.All(MinimalPairs.All, x =>
        {
            Assert.NotNull(Phonetics.Find(x.LeftSymbol));
            Assert.NotNull(Phonetics.Find(x.RightSymbol));
            Assert.NotEqual(x.Left, x.Right);
            Assert.False(string.IsNullOrWhiteSpace(x.Focus), $"{x.Left}/{x.Right} 没说在练什么");
        });
    }
}
