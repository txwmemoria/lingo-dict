namespace LingoDict.Grammar;

/// <summary>原文里的一个词，连同它的字符区间——建议要能画回到用户写的那段文字上。</summary>
internal readonly record struct WordSpan(string Text, int Start)
{
    public int End => Start + Text.Length;
}

internal static class EnglishText
{
    /// <summary>会被拆成独立词的标点。</summary>
    /// <remarks>
    /// 模型是在分好词的语料上训练的，标点自成一个 token。直接按空白切，
    /// <c>advices.</c> 会当成一个词，<c>$TRANSFORM_AGREEMENT_SINGULAR</c>
    /// 砍掉末尾一个字符就变成 <c>advices</c>——标出了问题却给了个错的改法。
    /// 拆开之后 42 组题从 33 升到 34（误报 2→3），改法也对了。
    /// </remarks>
    private const string Punctuation = ".,!?;:\"')(][}{";

    /// <summary>
    /// 按句号 / 问号 / 感叹号 / 分号 / 换行断句，每句给出带位置的词，标点独立成词。
    /// </summary>
    /// <remarks>
    /// 断句是必须的：模型训练长度只有 80 个 token，整段喂进去会被静默截断，
    /// 后半段等于没检查。断句也让每句独立推理，一句的改动不影响另一句的位置。
    /// </remarks>
    public static IEnumerable<List<WordSpan>> Sentences(string text, int maxWords)
    {
        var current = new List<WordSpan>();
        var index = 0;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                // 空行 / 换行当作段落边界：用户分行写的两句本来就该分开看
                if (text[index] == '\n' && current.Count > 0)
                {
                    yield return current;
                    current = [];
                }

                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            Split(text, start, index, current);

            var last = current[^1].Text;
            if (current.Count >= maxWords || (last.Length == 1 && last[0] is '.' or '!' or '?' or ';'))
            {
                yield return current;
                current = [];
            }
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    /// <summary>把 <c>("hello!")</c> 这样一段无空白的文字剥成 <c>( " hello ! " )</c>。</summary>
    private static void Split(string text, int start, int end, List<WordSpan> into)
    {
        var left = start;
        while (left < end && Punctuation.Contains(text[left]))
        {
            into.Add(new WordSpan(text[left].ToString(), left));
            left++;
        }

        var right = end;
        while (right > left && Punctuation.Contains(text[right - 1]))
        {
            right--;
        }

        // 词内的标点（don't、well-known、$19.99）留在词里：
        // 模型的词表本来就认得它们，拆开反而不像训练时见过的样子。
        if (right > left)
        {
            into.Add(new WordSpan(text[left..right], left));
        }

        for (var i = right; i < end; i++)
        {
            into.Add(new WordSpan(text[i].ToString(), i));
        }
    }
}
