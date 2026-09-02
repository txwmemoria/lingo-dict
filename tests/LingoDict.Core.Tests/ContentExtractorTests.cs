using System.Text.Json;
using LingoDict.Infrastructure.Data;

namespace LingoDict.Core.Tests;

public sealed class ContentExtractorTests
{
    [Fact]
    public void Extract_ReadsEveryContentSection()
    {
        var content = Parse(
            """
            {
              "ukphone": "ə'brʌptlɪ",
              "usphone": "ə'brʌptli",
              "trans": [
                { "tranCn": "突然地", "pos": "adv", "tranOther": "suddenly" },
                { "tranCn": "唐突地", "pos": "adv" }
              ],
              "sentence": { "sentences": [
                { "sContent": "The path ends off abruptly.",
                  "sContent_eng": "The path ends off <b>abruptly</b>.",
                  "sCn": "这条路突然到头了。" }
              ] },
              "realExamSentence": { "sentences": [
                { "sContent": "One had to quit abruptly on a given day.",
                  "sourceInfo": { "level": "CET4", "year": "2017.6", "type": "阅读理解" } }
              ] },
              "phrase": { "phrases": [
                { "pContent": "come to an abrupt end", "pCn": "戛然而止" }
              ] },
              "syno": { "synos": [
                { "pos": "adv", "tran": "突然地", "hwds": [ { "w": "suddenly" }, { "w": "sharp" } ] }
              ] },
              "relWord": { "rels": [
                { "pos": "adj", "words": [ { "hwd": "abrupt", "tran": " 突然的" } ] }
              ] }
            }
            """);

        var extracted = ContentExtractor.Extract(content);

        Assert.Equal("ə'brʌptlɪ", extracted.UkPhone);
        Assert.Equal("ə'brʌptli", extracted.UsPhone);
        Assert.Equal(["adv 突然地 suddenly", "adv 唐突地"], extracted.Translations);
        Assert.Equal("adv 突然地 suddenly\nadv 唐突地", extracted.Translation);

        // 普通例句和真题例句分开，真题带出处、通常没有译文。
        var sentence = Assert.Single(extracted.Sentences);
        Assert.Equal("这条路突然到头了。", sentence.Translation);
        Assert.Equal("abruptly", sentence.Emphasis);
        Assert.False(sentence.IsExam);

        var exam = Assert.Single(extracted.ExamSentences);
        Assert.Equal("CET4 2017.6 阅读理解", exam.Source);
        Assert.Equal(string.Empty, exam.Translation);
        Assert.True(exam.IsExam);

        Assert.Equal("come to an abrupt end", Assert.Single(extracted.Phrases).Primary);

        // 同义词保留成词表而不是拼接串，界面才能把每个词做成可点击的跳转。
        var synonym = Assert.Single(extracted.Synonyms);
        Assert.Equal("adv 突然地", synonym.Meaning);
        Assert.Equal(["suddenly", "sharp"], synonym.Words);

        var related = Assert.Single(extracted.RelatedWords);
        Assert.Equal("abrupt", related.Word);
        Assert.Equal("adj 突然的", related.Note);
    }

    [Fact]
    public void Extract_ToleratesMissingSections()
    {
        var extracted = ContentExtractor.Extract(Parse("""{ "trans": [] }"""));

        Assert.Equal(string.Empty, extracted.UkPhone);
        Assert.Equal(string.Empty, extracted.Translation);
        Assert.Empty(extracted.Translations);
        Assert.Empty(extracted.Sentences);
        Assert.Empty(extracted.Phrases);
        Assert.Empty(extracted.Synonyms);
        Assert.Empty(extracted.RelatedWords);
    }

    [Fact]
    public void Extract_AcceptsSentenceListWithoutWrapperObject()
    {
        var extracted = ContentExtractor.Extract(Parse(
            """{ "sentence": [ { "sContent": "Plain array form.", "sCn": "数组形式。" } ] }"""));

        Assert.Equal("Plain array form.", Assert.Single(extracted.Sentences).Text);
    }

    [Fact]
    public void Extract_DropsEntriesWithNoTextAtAll()
    {
        var extracted = ContentExtractor.Extract(Parse(
            """{ "phrase": { "phrases": [ { "pContent": "", "pCn": "" }, { "pContent": "keep up" } ] } }"""));

        Assert.Equal("keep up", Assert.Single(extracted.Phrases).Primary);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
