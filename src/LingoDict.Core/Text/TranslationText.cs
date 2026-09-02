namespace LingoDict.Core.Text;

/// <summary>
/// 释义文本的显示整形。
/// </summary>
public static class TranslationText
{
    /// <summary>
    /// 公共尾巴至少这么长才算数。
    /// </summary>
    /// <remarks>
    /// 英文释义句普遍七八十字符起。定这么低是为了连短句也一起收掉，
    /// 又不至于把两条恰好都以「；比较级」收尾的中文行误当成重复。
    /// </remarks>
    private const int MinimumTail = 20;

    /// <summary>
    /// 去掉与参照文本重复的英文尾巴。
    /// </summary>
    /// <remarks>
    /// 「其他词书的释义」列出同一个词在别的词书里的写法。实测 15,664 个有多条
    /// 释义的词头里，**9,093 个（64%）各条的英文完全一致**，而英文占这些行
    /// 82% 的字符——也就是说那一段大部分是同一句话抄了好几遍，
    /// 真正不同的中文被挤在开头。而且完整的英文在上面「释义」里已经有了。
    ///
    /// 源数据把词性、中文、英文用空格拼成一行，回头切不开。但不需要切：
    /// 要去掉的正是各条之间的**公共后缀**，纯文本比对就能定位，不必判断
    /// 哪一段是英文。
    ///
    /// 四条约束，避免误伤：尾巴要够长；从空格处断开（不切在词中间）；
    /// 空格后必须是 ASCII 字母（只吃英文尾巴，不动「…；比较级」这类中文收尾）；
    /// **剩下的部分必须含中文**。
    ///
    /// 最后一条是关键。只要求「剩下的非空」不够——
    /// 「to officially end a law…」会在第一个空格处被切成孤零零的「to」。
    /// 这一段要保住的本来就是各词书写法不同的中文，剩不下中文就说明切错了地方，
    /// 整行留着。宁可重复，不可切碎。
    /// </remarks>
    public static string WithoutRepeatedTail(string value, string reference)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(reference))
        {
            return value;
        }

        var lines = value.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = TrimLine(lines[index], reference);
            if (!ReferenceEquals(trimmed, lines[index]))
            {
                lines[index] = trimmed;
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : value;
    }

    private static string TrimLine(string line, string reference)
    {
        // 从最长的尾巴往回缩：第一个命中的就是最长公共后缀，
        // 也就是能砍掉最多重复的那一刀。
        for (var start = 0; start <= line.Length - MinimumTail; start++)
        {
            if (line[start] != ' ' || start + 1 >= line.Length || !IsAsciiLetter(line[start + 1]))
            {
                continue;
            }

            var head = line[..start].TrimEnd();
            if (!HasChinese(head))
            {
                // 剩下的没有中文 = 这一刀切在英文句子内部，或者这行本来就只有英文
                continue;
            }

            if (reference.Contains(line[start..], StringComparison.Ordinal))
            {
                return head;
            }
        }

        return line;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool HasChinese(string value)
    {
        foreach (var c in value)
        {
            if (c is >= '一' and <= '鿿')
            {
                return true;
            }
        }

        return false;
    }
}
