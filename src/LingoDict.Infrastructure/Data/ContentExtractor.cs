using System.Text.Json;
using LingoDict.Core.Models;
using LingoDict.Core.Text;

namespace LingoDict.Infrastructure.Data;

internal static class ContentExtractor
{
    internal sealed record ExtractedContent(
        string UkPhone,
        string UsPhone,
        string Translation,
        IReadOnlyList<string> Translations,
        IReadOnlyList<ExampleSentence> Sentences,
        IReadOnlyList<ExampleSentence> ExamSentences,
        IReadOnlyList<TextPair> Phrases,
        IReadOnlyList<SynonymGroup> Synonyms,
        IReadOnlyList<WordLink> RelatedWords);

    /// <param name="headword">
    /// 例句加粗的兜底目标。源数据里只有约 12% 的例句带 &lt;b&gt; 标记，
    /// 其余用词头本身去匹配——命中不了变形词（cancelled），但比完全不加粗好。
    /// </param>
    public static ExtractedContent Extract(JsonElement content, string headword = "")
    {
        var translations = new List<string>();
        var sentences = new List<ExampleSentence>();
        var examSentences = new List<ExampleSentence>();
        var phrases = new List<TextPair>();
        var synonyms = new List<SynonymGroup>();
        var relatedWords = new List<WordLink>();

        if (content.TryGetProperty("trans", out var trans) && trans.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in trans.EnumerateArray())
            {
                var pos = GetString(item, "pos");
                var chinese = GetString(item, "tranCn");
                var english = GetString(item, "tranOther");
                var value = JoinParts(pos, chinese, english);
                if (value.Length > 0)
                {
                    translations.Add(value);
                }
            }
        }

        AddSentences(content, "sentence", sentences, headword);
        AddSentences(content, "realExamSentence", examSentences, headword);

        if (content.TryGetProperty("phrase", out var phraseRoot) &&
            phraseRoot.TryGetProperty("phrases", out var phraseItems) &&
            phraseItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in phraseItems.EnumerateArray())
            {
                phrases.Add(new TextPair(GetString(item, "pContent"), GetString(item, "pCn")));
            }
        }

        if (content.TryGetProperty("syno", out var synoRoot) &&
            synoRoot.TryGetProperty("synos", out var synoItems) &&
            synoItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in synoItems.EnumerateArray())
            {
                var words = new List<string>();
                if (item.TryGetProperty("hwds", out var hwds) && hwds.ValueKind == JsonValueKind.Array)
                {
                    words.AddRange(hwds.EnumerateArray().Select(x => GetString(x, "w")).Where(x => x.Length > 0));
                }

                synonyms.Add(new SynonymGroup(JoinParts(GetString(item, "pos"), GetString(item, "tran")), words));
            }
        }

        if (content.TryGetProperty("relWord", out var relRoot) &&
            relRoot.TryGetProperty("rels", out var relItems) &&
            relItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in relItems.EnumerateArray())
            {
                var pos = GetString(group, "pos");
                if (!group.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in words.EnumerateArray())
                {
                    relatedWords.Add(new WordLink(GetString(item, "hwd"), JoinParts(pos, GetString(item, "tran"))));
                }
            }
        }

        return new ExtractedContent(
            GetString(content, "ukphone"),
            GetString(content, "usphone"),
            string.Join("\n", translations),
            translations,
            [.. sentences.Where(IsUseful)],
            [.. examSentences.Where(IsUseful)],
            [.. phrases.Where(IsUseful)],
            synonyms.Where(x => x.Words.Count > 0 || x.Meaning.Length > 0).ToArray(),
            relatedWords.Where(x => x.Word.Length > 0 || x.Note.Length > 0).ToArray());
    }

    private static void AddSentences(
        JsonElement content,
        string propertyName,
        ICollection<ExampleSentence> target,
        string headword)
    {
        if (!content.TryGetProperty(propertyName, out var sentenceRoot))
        {
            return;
        }

        JsonElement items;
        if (sentenceRoot.ValueKind == JsonValueKind.Object &&
            sentenceRoot.TryGetProperty("sentences", out var nestedItems))
        {
            items = nestedItems;
        }
        else if (sentenceRoot.ValueKind == JsonValueKind.Array)
        {
            items = sentenceRoot;
        }
        else
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            var text = GetString(item, "sContent");
            var marked = Emphasis(GetString(item, "sContent_eng"));
            target.Add(new ExampleSentence(
                text,
                GetString(item, "sCn"),
                marked.Length > 0 ? marked : Fallback(text, headword),
                Source(item)));
        }
    }

    /// <summary>
    /// sContent_eng 与 sContent 只差一对 &lt;b&gt; 标记，取出被标出的那段即可，
    /// 界面按它把例句里的目标词加粗。
    /// </summary>
    private static string Emphasis(string marked)
    {
        const string Open = "<b>";
        const string Close = "</b>";
        var start = marked.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += Open.Length;
        var end = marked.IndexOf(Close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? string.Empty : marked[start..end].Trim();
    }

    /// <summary>
    /// 句中确实出现了词头（或其屈折形式）才回填，避免存一个根本匹配不上的值。
    /// 判定与渲染端共用 <see cref="SearchText.IsInflectionOf"/>。
    /// </summary>
    private static string Fallback(string text, string headword) =>
        headword.Length > 0 &&
        SearchText.SplitWords(text).Any(x => x.IsWord && SearchText.IsInflectionOf(x.Text, headword))
            ? headword
            : string.Empty;

    /// <summary>真题出处，拼成「CET4 2017.6 阅读理解」。</summary>
    private static string Source(JsonElement item)
    {
        if (!item.TryGetProperty("sourceInfo", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return JoinParts(GetString(info, "level"), GetString(info, "year"), GetString(info, "type"));
    }

    private static bool IsUseful(TextPair value) => value.Primary.Length > 0 || value.Secondary.Length > 0;

    private static bool IsUseful(ExampleSentence value) => value.Text.Length > 0 || value.Translation.Length > 0;

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string JoinParts(params string[] values) =>
        string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
}
