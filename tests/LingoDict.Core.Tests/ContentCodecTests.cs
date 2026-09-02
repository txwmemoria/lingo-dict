using System.Text;
using LingoDict.Core.Models;
using LingoDict.Infrastructure.Data;

namespace LingoDict.Core.Tests;

public sealed class ContentCodecTests
{
    private static readonly ContentExtractor.ExtractedContent Sample = new(
        UkPhone: "ə'brʌptlɪ",
        UsPhone: "ə'brʌptli",
        Translation: "adv 突然地\nadv 唐突地",
        Translations: ["adv 突然地", "adv 唐突地"],
        Sentences: [new ExampleSentence("The path ends off abruptly.", "这条路突然到头了。", "abruptly", "")],
        ExamSentences: [new ExampleSentence("...quit abruptly on a given day...", "", "", "CET4 2017.6 阅读理解")],
        Phrases: [new TextPair("come to an abrupt end", "戛然而止")],
        Synonyms: [new SynonymGroup("adv 突然地", ["suddenly", "sharp"])],
        RelatedWords: [new WordLink("abrupt", "adj 突然的")]);

    [Fact]
    public void RoundTrip_PreservesEverythingTheDetailPageShows()
    {
        var decoded = ContentCodec.Decode(ContentCodec.Compress(ContentCodec.Serialize(Sample)));

        Assert.Equal(Sample.Translations, decoded.Translations);
        Assert.Equal(Sample.Sentences, decoded.Sentences);
        Assert.Equal(Sample.ExamSentences, decoded.ExamSentences);

        // 强调词与真题出处都要能往返，否则例句加粗和来源标注就丢了。
        Assert.Equal("abruptly", decoded.Sentences[0].Emphasis);
        Assert.Equal("CET4 2017.6 阅读理解", decoded.ExamSentences[0].Source);
        Assert.True(decoded.ExamSentences[0].IsExam);
        Assert.False(decoded.Sentences[0].IsExam);
        Assert.Equal(Sample.Phrases, decoded.Phrases);
        Assert.Equal(Sample.RelatedWords, decoded.RelatedWords);

        var synonym = Assert.Single(decoded.Synonyms);
        Assert.Equal("adv 突然地", synonym.Meaning);
        Assert.Equal(["suddenly", "sharp"], synonym.Words);
    }

    [Fact]
    public void RoundTrip_HandlesCompletelyEmptyContent()
    {
        var empty = new ContentExtractor.ExtractedContent(
            string.Empty, string.Empty, string.Empty, [], [], [], [], [], []);

        var decoded = ContentCodec.Decode(ContentCodec.Compress(ContentCodec.Serialize(empty)));

        Assert.Empty(decoded.Translations);
        Assert.Empty(decoded.Sentences);
        Assert.Empty(decoded.ExamSentences);
        Assert.Empty(decoded.Phrases);
        Assert.Empty(decoded.Synonyms);
        Assert.Empty(decoded.RelatedWords);
    }

    [Fact]
    public void Serialize_WritesChineseAsUtf8RatherThanEscapes()
    {
        var payload = ContentCodec.Serialize(Sample);

        // 默认的 JSON 编码器会把每个汉字写成 \uXXXX，压缩前先胀三倍。
        Assert.Contains("突然地", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_OmitsSectionsThatHaveNoContent()
    {
        var onlyTranslations = Sample with
        {
            Sentences = [], ExamSentences = [], Phrases = [], Synonyms = [], RelatedWords = [],
        };

        var json = Encoding.UTF8.GetString(ContentCodec.Serialize(onlyTranslations));

        Assert.Contains("\"t\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"s\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"p\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_IsStableSoTheDedupHashIsStable()
    {
        Assert.Equal(ContentCodec.Serialize(Sample), ContentCodec.Serialize(Sample));
    }

    [Fact]
    public void Compress_ActuallyShrinksTypicalEntries()
    {
        var payload = ContentCodec.Serialize(Sample);

        Assert.True(
            ContentCodec.Compress(payload).Length < payload.Length,
            "压缩后应当比紧凑 JSON 更小");
    }

    [Fact]
    public void Serialize_DropsPhonesBecauseTheyLiveInTheirOwnColumns()
    {
        var json = Encoding.UTF8.GetString(ContentCodec.Serialize(Sample));

        Assert.DoesNotContain("brʌptl", json, StringComparison.Ordinal);
    }
}
