namespace LingoDict.Core.Text;

public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>文本被切分后的一段，<paramref name="IsWord"/> 表示它是不是一个可查询的词。</summary>
public sealed record TextRun(string Text, bool IsWord);

/// <summary>
/// 把一段文本按查询词元切成"命中/未命中"的片段，供界面渲染高亮。
/// 词元来自 <see cref="SearchText.Tokenize"/>，与实际参与 FTS 匹配的是同一批。
/// </summary>
public static class TextHighlighter
{
    private static readonly IReadOnlyList<TextSegment> Empty = [];

    public static IReadOnlyList<TextSegment> Split(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        var tokens = SearchText.Tokenize(query ?? string.Empty);
        if (tokens.Count == 0)
        {
            return [new TextSegment(text, false)];
        }

        var matched = new bool[text.Length];
        foreach (var token in tokens)
        {
            var from = 0;
            while (from <= text.Length - token.Length)
            {
                var at = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                for (var index = at; index < at + token.Length; index++)
                {
                    matched[index] = true;
                }

                from = at + token.Length;
            }
        }

        return Merge(text, matched);
    }

    private static List<TextSegment> Merge(string text, bool[] matched)
    {
        var segments = new List<TextSegment>();
        var start = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            if (index < text.Length && matched[index] == matched[start])
            {
                continue;
            }

            segments.Add(new TextSegment(text[start..index], matched[start]));
            start = index;
        }

        return segments;
    }
}
