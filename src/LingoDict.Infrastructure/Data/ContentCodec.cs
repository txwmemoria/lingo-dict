using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LingoDict.Core.Models;

namespace LingoDict.Infrastructure.Data;

/// <summary>
/// 词条正文在库里怎么存。原始 kajweb JSON 里 43% 是界面从不读取的字段
/// （sSpeech / sContent_eng / sourceInfo / star / desc…），加上单行超过页面内联上限
/// 造成的溢出浪费，未处理时要占 230 MiB。这里只留用得到的字段、用短键序列化再压缩。
/// </summary>
internal static class ContentCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 默认编码器会把每个汉字转义成 \uXXXX，压缩前先胀三倍，必须放宽。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>紧凑 JSON，未压缩。去重哈希基于这一层，避免噪声字段造成的假差异。</summary>
    public static byte[] Serialize(ContentExtractor.ExtractedContent content)
    {
        var stored = new StoredContent
        {
            Translations = Empty(content.Translations) ? null : [.. content.Translations],
            Sentences = Examples(content.Sentences),
            ExamSentences = Examples(content.ExamSentences),
            Phrases = Pairs(content.Phrases),
            Synonyms = Empty(content.Synonyms)
                ? null
                : [.. content.Synonyms.Select(x => new StoredSynonym
                {
                    Meaning = Nullify(x.Meaning),
                    Words = x.Words.Count == 0 ? null : [.. x.Words],
                })],
            RelatedWords = Empty(content.RelatedWords)
                ? null
                : [.. content.RelatedWords.Select(x => new[] { x.Word, x.Note })],
        };

        return JsonSerializer.SerializeToUtf8Bytes(stored, Options);
    }

    public static byte[] Compress(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new ZLibStream(buffer, CompressionLevel.SmallestSize))
        {
            deflate.Write(payload);
        }

        return buffer.ToArray();
    }

    public static DecodedContent Decode(byte[] stored)
    {
        using var input = new MemoryStream(stored);
        using var deflate = new ZLibStream(input, CompressionMode.Decompress);
        var content = JsonSerializer.Deserialize<StoredContent>(deflate, Options) ?? new StoredContent();

        return new DecodedContent(
            content.Translations ?? [],
            ToExamples(content.Sentences),
            ToExamples(content.ExamSentences),
            ToPairs(content.Phrases),
            content.Synonyms is null
                ? []
                : [.. content.Synonyms.Select(x => new SynonymGroup(x.Meaning ?? string.Empty, x.Words ?? []))],
            content.RelatedWords is null
                ? []
                : [.. content.RelatedWords.Select(x => new WordLink(At(x, 0), At(x, 1)))]);
    }

    private static bool Empty<T>(IReadOnlyCollection<T> values) => values.Count == 0;

    private static string? Nullify(string value) => value.Length == 0 ? null : value;

    private static List<string[]>? Pairs(IReadOnlyList<TextPair> values) =>
        values.Count == 0 ? null : [.. values.Select(x => new[] { x.Primary, x.Secondary })];

    /// <summary>例句存成四元组：原文、译文、被强调的词、真题出处。尾部空串会被裁掉。</summary>
    private static List<string[]>? Examples(IReadOnlyList<ExampleSentence> values) =>
        values.Count == 0
            ? null
            : [.. values.Select(x => Trim([x.Text, x.Translation, x.Emphasis, x.Source]))];

    private static string[] Trim(string[] parts)
    {
        var length = parts.Length;
        while (length > 1 && parts[length - 1].Length == 0)
        {
            length--;
        }

        return length == parts.Length ? parts : parts[..length];
    }

    private static IReadOnlyList<ExampleSentence> ToExamples(List<string[]>? values) =>
        values is null
            ? []
            : [.. values.Select(x => new ExampleSentence(At(x, 0), At(x, 1), At(x, 2), At(x, 3)))];

    private static IReadOnlyList<TextPair> ToPairs(List<string[]>? values) =>
        values is null ? [] : [.. values.Select(x => new TextPair(At(x, 0), At(x, 1)))];

    private static string At(string[] values, int index) =>
        index < values.Length ? values[index] ?? string.Empty : string.Empty;

    private sealed class StoredContent
    {
        [JsonPropertyName("t")]
        public List<string>? Translations { get; set; }

        [JsonPropertyName("s")]
        public List<string[]>? Sentences { get; set; }

        [JsonPropertyName("x")]
        public List<string[]>? ExamSentences { get; set; }

        [JsonPropertyName("p")]
        public List<string[]>? Phrases { get; set; }

        [JsonPropertyName("y")]
        public List<StoredSynonym>? Synonyms { get; set; }

        [JsonPropertyName("r")]
        public List<string[]>? RelatedWords { get; set; }
    }

    private sealed class StoredSynonym
    {
        [JsonPropertyName("m")]
        public string? Meaning { get; set; }

        [JsonPropertyName("w")]
        public List<string>? Words { get; set; }
    }
}

/// <summary>解码后的正文。音标不在其中——它们是 entries 表的独立列，不必存两遍。</summary>
internal sealed record DecodedContent(
    IReadOnlyList<string> Translations,
    IReadOnlyList<ExampleSentence> Sentences,
    IReadOnlyList<ExampleSentence> ExamSentences,
    IReadOnlyList<TextPair> Phrases,
    IReadOnlyList<SynonymGroup> Synonyms,
    IReadOnlyList<WordLink> RelatedWords);
