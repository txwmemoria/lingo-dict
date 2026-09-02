namespace LingoDict.Grammar;

/// <summary>
/// 动词词形表，服务 <c>$TRANSFORM_VERB_VB_VBZ</c> 这类标签：
/// 标签只说「从原形变第三人称单数」，具体写法要查表。
/// </summary>
/// <remarks>
/// 数据来自 grammarly/gector 的 verb-form-vocab.txt（Apache-2.0）。原文件是
/// 168750 行的 <c>abandon_abandoned:VB_VBD</c>，其实只有 33778 个源词，
/// 这里按源词分组后 gzip 存成 527 KB 内嵌进程序集。
/// </remarks>
internal sealed class VerbForms
{
    private readonly Dictionary<string, Dictionary<string, string>> _table;

    private VerbForms(Dictionary<string, Dictionary<string, string>> table) => _table = table;

    public static VerbForms Create()
    {
        var table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var line in GrammarAssets.ReadLines("verb-forms.txt"))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var forms = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in line.AsSpan(tab + 1).ToString().Split(' '))
            {
                var equals = entry.IndexOf('=');
                if (equals > 0)
                {
                    forms[entry[..equals]] = entry[(equals + 1)..];
                }
            }

            table[line[..tab]] = forms;
        }

        return new VerbForms(table);
    }

    /// <summary>把 <paramref name="word"/> 从 <paramref name="pair"/>（如 <c>VB_VBZ</c>）左边的形态变到右边。</summary>
    /// <returns>查不到时返回 <c>null</c>，调用方应当放弃这条建议而不是硬改。</returns>
    public string? Convert(string word, string pair)
    {
        if (_table.TryGetValue(word, out var forms) && forms.TryGetValue(pair, out var exact))
        {
            return exact;
        }

        // 表里存的都是小写。句首的 Go / Went 查不到，降一次大小写再查，
        // 命中后把首字母还原——GECToR 原版没有这一步，句首动词会白白漏掉。
        var lowered = word.ToLowerInvariant();
        if (lowered != word && _table.TryGetValue(lowered, out forms) && forms.TryGetValue(pair, out var found))
        {
            return char.IsUpper(word[0]) && found.Length > 0
                ? char.ToUpperInvariant(found[0]) + found[1..]
                : found;
        }

        return null;
    }
}
