using LingoDict.Core.Models;

namespace LingoDict.Grammar;

/// <summary>
/// 把 GECToR 的一个编辑标签翻译成「原词 → 建议写法」。
/// </summary>
/// <remarks>
/// 5002 个标签里 4969 个（$REPLACE_x / $APPEND_x）把目标词直接写在标签里，
/// 剩下的 $TRANSFORM_* 才需要形态变换，其中 20 个动词标签要查
/// <see cref="VerbForms"/>，其余都是纯字符串操作。
/// </remarks>
internal sealed class EditLabel
{
    private readonly VerbForms _verbs;

    public EditLabel(VerbForms verbs) => _verbs = verbs;

    /// <summary>
    /// 解析一个标签。返回 <c>null</c> 表示这条建议不成立（比如动词表里查不到目标形态），
    /// 调用方应当整条丢弃——宁可漏报，也不要给出一个自己都算不出来的「建议」。
    /// </summary>
    public (string Suggestion, GrammarEditKind Kind)? Resolve(string label, string word, string? next)
    {
        if (label.StartsWith("$REPLACE_", StringComparison.Ordinal))
        {
            return (label["$REPLACE_".Length..], GrammarEditKind.Replace);
        }

        if (label.StartsWith("$APPEND_", StringComparison.Ordinal))
        {
            return (label["$APPEND_".Length..], GrammarEditKind.Insert);
        }

        if (label == "$DELETE")
        {
            return ("", GrammarEditKind.Delete);
        }

        if (label == "$MERGE_SPACE" || label == "$MERGE_HYPHEN")
        {
            // 和后一个词合成一个：MERGE_SPACE 直接接上，MERGE_HYPHEN 用连字符接。
            // 没有后一个词就不成立。
            if (next is null)
            {
                return null;
            }

            var joiner = label == "$MERGE_HYPHEN" ? "-" : "";
            return (word + joiner + next, GrammarEditKind.Merge);
        }

        if (label.StartsWith("$TRANSFORM_", StringComparison.Ordinal))
        {
            return Transform(label["$TRANSFORM_".Length..], word);
        }

        // $KEEP、<PAD>、<OOV> 走不到这里
        return null;
    }

    private (string, GrammarEditKind)? Transform(string action, string word)
    {
        if (word.Length == 0)
        {
            return null;
        }

        switch (action)
        {
            case "CASE_LOWER":
                return (word.ToLowerInvariant(), GrammarEditKind.Replace);
            case "CASE_UPPER":
                return (word.ToUpperInvariant(), GrammarEditKind.Replace);
            case "CASE_CAPITAL":
                return (char.ToUpperInvariant(word[0]) + word[1..], GrammarEditKind.Replace);
            case "CASE_CAPITAL_1":
                // 第二个字母大写，专治 iPhone / eBay 这类
                return word.Length < 2
                    ? null
                    : (word[0] + char.ToUpperInvariant(word[1]).ToString() + word[2..], GrammarEditKind.Replace);
            case "SPLIT_HYPHEN":
                return word.Contains('-')
                    ? (word.Replace("-", " "), GrammarEditKind.Split)
                    : null;
            case "AGREEMENT_PLURAL":
                return (word + "s", GrammarEditKind.Replace);
            case "AGREEMENT_SINGULAR":
                // GECToR 就是简单砍掉末尾一个字符，不砍 s 结尾的词等于把词改坏了
                return word.EndsWith('s') ? (word[..^1], GrammarEditKind.Replace) : null;
        }

        if (action.StartsWith("VERB_", StringComparison.Ordinal))
        {
            var converted = _verbs.Convert(word, action["VERB_".Length..]);
            return converted is null ? null : (converted, GrammarEditKind.Replace);
        }

        return null;
    }

    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "in", "on", "at", "of", "to", "for", "with", "from", "by", "about", "into", "onto", "off",
        "over", "under", "between", "among", "through", "during", "before", "after", "against",
        "without", "within", "across", "behind", "beyond", "near", "since", "until", "till",
        "upon", "toward", "towards", "out", "up", "down", "along", "around", "beside", "besides",
        "despite", "except", "inside", "outside", "past", "per", "than", "throughout", "unlike",
    };

    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "a", "an", "the" };

    /// <summary>给中文使用者的归类。学习者最在意的是「我又栽在哪一类上了」。</summary>
    public static string Categorize(string label, string word, string suggestion)
    {
        if (label.StartsWith("$TRANSFORM_VERB_", StringComparison.Ordinal))
        {
            return "动词形式";
        }

        if (label.StartsWith("$TRANSFORM_AGREEMENT_", StringComparison.Ordinal))
        {
            return "单复数";
        }

        if (label.StartsWith("$TRANSFORM_CASE_", StringComparison.Ordinal))
        {
            return "大小写";
        }

        if (label is "$MERGE_SPACE" or "$MERGE_HYPHEN" || label == "$TRANSFORM_SPLIT_HYPHEN")
        {
            return "断词";
        }

        // 归类看的是被动的那个词：删的时候看原词，加的时候看新词。
        var subject = label == "$DELETE" ? word : suggestion;
        if (Articles.Contains(subject))
        {
            return "冠词";
        }

        if (Prepositions.Contains(subject))
        {
            return "介词";
        }

        if (subject.Length > 0 && !subject.Any(char.IsLetterOrDigit))
        {
            return "标点";
        }

        return label switch
        {
            "$DELETE" => "多余的词",
            _ when label.StartsWith("$APPEND_", StringComparison.Ordinal) => "缺词",
            _ => "用词",
        };
    }
}
