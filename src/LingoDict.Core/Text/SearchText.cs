using System.Text;

namespace LingoDict.Core.Text;

/// <summary>
/// FTS5 的前处理。unicode61 分词器把一整串连续汉字当成单个词元，
/// 因此索引侧和查询侧都要按单字切开，"美好" 才能命中 "美好的时光"。
/// </summary>
public static class SearchText
{
    /// <summary>索引侧：在每个汉字两侧插入空格，使其成为独立词元。</summary>
    public static string Segment(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsIdeograph(rune))
            {
                builder.Append(' ').Append(rune.ToString()).Append(' ');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 查询侧：构造 FTS5 MATCH 表达式。所有词元组成一个短语（要求相邻），
    /// 末尾加 <c>*</c> 让最后一个词元做前缀匹配，因此 "can" 能命中 "cancel"。
    /// 无可用词元时返回 <see langword="null"/>。
    /// </summary>
    public static string? BuildMatchExpression(string query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(query.Length + 8);
        builder.Append('"');
        for (var index = 0; index < tokens.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(tokens[index]);
        }

        return builder.Append("\" *").ToString();
    }

    /// <summary>
    /// 切分查询串：每个汉字一个词元，连续的字母/数字一个词元，其余字符作为分隔符丢弃。
    /// 产出的词元只含字母、数字和汉字，因此可以安全地放进 FTS5 的双引号短语里。
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return tokens;
        }

        var current = new StringBuilder();
        foreach (var rune in query.EnumerateRunes())
        {
            if (IsIdeograph(rune))
            {
                Flush(current, tokens);
                tokens.Add(rune.ToString());
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                Flush(current, tokens);
            }
        }

        Flush(current, tokens);
        return tokens;
    }

    /// <summary>
    /// 取出 <paramref name="index"/> 处的整个单词，用于在例句里双击查词。
    /// 连字符和撇号算词内字符，这样 "long-time"、"don't" 不会被拆开；
    /// 句末的标点会被排除在外。取不到时返回空串。
    /// </summary>
    public static string WordAt(string? text, int index)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 点在词尾空白上时往左退一格，否则双击单词末尾会落空。
        var at = Math.Clamp(index, 0, text.Length - 1);
        if (!IsWordChar(text[at]) && at > 0 && IsWordChar(text[at - 1]))
        {
            at--;
        }

        if (!IsWordChar(text[at]))
        {
            return string.Empty;
        }

        var start = at;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        var end = at;
        while (end + 1 < text.Length && IsWordChar(text[end + 1]))
        {
            end++;
        }

        // 首尾的连字符/撇号不属于单词本身（例如破折号紧贴着词）。
        while (start <= end && !char.IsLetterOrDigit(text[start]))
        {
            start++;
        }

        while (end >= start && !char.IsLetterOrDigit(text[end]))
        {
            end--;
        }

        return end >= start ? text[start..(end + 1)] : string.Empty;
    }

    /// <summary>
    /// 把一段文本切成「词」和「非词」交替的片段，供界面逐词做 hover 高亮。
    /// 拼接所有片段能还原原文。
    /// </summary>
    public static IReadOnlyList<TextRun> SplitWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var runs = new List<TextRun>();
        var start = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            if (index < text.Length && IsWordChar(text[index]) == IsWordChar(text[start]))
            {
                continue;
            }

            runs.Add(new TextRun(text[start..index], IsWordChar(text[start])));
            start = index;
        }

        return runs;
    }

    /// <summary>
    /// <paramref name="word"/> 是不是 <paramref name="stem"/> 的同一个词（含常见屈折变化）。
    /// 例句加粗时导入端和渲染端必须用同一套判定，否则会存下匹配不上的值。
    ///
    /// 只允许追加不超过三个字符（cancel → cancelled），且词干至少四个字母——
    /// 否则 "can" 会把 "cancel" 也点亮。
    /// </summary>
    public static bool IsInflectionOf(string? word, string? stem)
    {
        if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(stem))
        {
            return false;
        }

        if (string.Equals(word, stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length >= 4
            && word.Length > stem.Length
            && word.Length - stem.Length <= 3
            && word.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWordChar(char value) =>
        char.IsLetterOrDigit(value) || value is '-' or '\'' or '’';

    private static void Flush(StringBuilder buffer, ICollection<string> tokens)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        tokens.Add(buffer.ToString());
        buffer.Clear();
    }

    private static bool IsIdeograph(Rune rune) => rune.Value switch
    {
        >= 0x3400 and <= 0x4DBF => true,   // CJK 扩展 A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK 基本区
        >= 0xF900 and <= 0xFAFF => true,   // CJK 兼容表意文字
        >= 0x20000 and <= 0x2FA1F => true, // CJK 扩展 B 及以后
        _ => false,
    };
}
