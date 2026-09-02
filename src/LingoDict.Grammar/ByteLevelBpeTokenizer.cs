using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoDict.Grammar;

/// <summary>
/// RoBERTa 的 byte-level BPE 分词器，只实现 GECToR 用到的那条路径：
/// 输入已经按空白切成词，逐词编码，取每个词的第一个子词的标签。
/// </summary>
/// <remarks>
/// 为什么自己写而不是找个包：GECToR 的 ONNX 只接受 token id，而
/// Microsoft.ML.Tokenizers 里没有和 HuggingFace 逐字节对齐的 RoBERTa 实现。
/// 分词器差一点点不会报错——它照样吐出合法 id，模型照样出结果，只是结果慢慢变歪。
/// 所以这里的每一步都对着 HuggingFace 的输出做了黄金样本比对，见
/// <c>tests/LingoDict.Grammar.Tests/Golden/tokenizer.json</c>。
/// </remarks>
public sealed partial class ByteLevelBpeTokenizer
{
    /// <summary>特殊符号：句首。</summary>
    public int BosId { get; }

    /// <summary>特殊符号：句尾。</summary>
    public int EosId { get; }

    /// <summary>GECToR 在每句前面插一个 $START，让第一个真实单词也能挂 $APPEND 标签。</summary>
    public int StartId { get; }

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string Left, string Right), int> _ranks;
    // 同一个词在一篇文章里会反复出现，BPE 合并是纯函数，缓存下来省一半时间
    private readonly Dictionary<string, string[]> _cache = new(StringComparer.Ordinal);

    private ByteLevelBpeTokenizer(Dictionary<string, int> vocab, Dictionary<(string, string), int> ranks, int startId)
    {
        _vocab = vocab;
        _ranks = ranks;
        BosId = vocab["<s>"];
        EosId = vocab["</s>"];
        StartId = startId;
    }

    /// <summary>从内嵌的 vocab.json + merges.txt 建一个分词器。</summary>
    public static ByteLevelBpeTokenizer Create()
    {
        using var stream = GrammarAssets.Open("vocab.json");
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
            ?? throw new InvalidDataException("内嵌的 vocab.json 不是合法的词表");

        var ranks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var line in GrammarAssets.ReadLines("merges.txt"))
        {
            // 首行是 `#version: 0.2` 之类的注释
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var space = line.IndexOf(' ');
            if (space > 0)
            {
                ranks[(line[..space], line[(space + 1)..])] = rank++;
            }
        }

        // $START 是训练时追加的 added_token，不在 vocab.json 里，排在基础词表之后
        return new ByteLevelBpeTokenizer(vocab, ranks, vocab.Count);
    }

    /// <summary>
    /// 编码一句话。<paramref name="words"/> 是调用方已按空白切好的词。
    /// </summary>
    /// <returns>
    /// token id，以及每个 id 属于第几个词（<c>-1</c> 表示 &lt;s&gt; / &lt;/s&gt; 这类特殊符号，
    /// <c>0</c> 是 $START，真实单词从 <c>1</c> 开始）。
    /// </returns>
    public (int[] Ids, int[] WordIds) Encode(IReadOnlyList<string> words, int maxTokens = 80)
    {
        var ids = new List<int> { BosId, StartId };
        var wordIds = new List<int> { -1, 0 };

        for (var index = 0; index < words.Count; index++)
        {
            foreach (var piece in EncodeWord(words[index]))
            {
                // 只有词表里有的片段才可能出现：byte-level 覆盖全部 256 个字节，
                // 且这个模型没有 unk_token，查不到就说明词表和模型对不上。
                if (!_vocab.TryGetValue(piece, out var id))
                {
                    throw new InvalidDataException($"词表里没有片段 “{piece}”，vocab.json 和模型不匹配");
                }

                ids.Add(id);
                wordIds.Add(index + 1);
            }
        }

        // 模型的训练长度是 80。超长句直接截断会把后半句静默丢掉，
        // 所以调用方应当先分句；这里只是兜底。留一格给 </s>。
        if (ids.Count > maxTokens - 1)
        {
            ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
            wordIds.RemoveRange(maxTokens - 1, wordIds.Count - (maxTokens - 1));
        }

        ids.Add(EosId);
        wordIds.Add(-1);
        return (ids.ToArray(), wordIds.ToArray());
    }

    /// <summary>这个词会占几个 token。调用方据此在超长文本里切块，免得被静默截断。</summary>
    public int PieceCount(string word) => EncodeWord(word).Count();

    /// <summary>编码单个词，返回它的子词片段。</summary>
    private IEnumerable<string> EncodeWord(string word)
    {
        // add_prefix_space=true：每个词前面补一个空格，于是词首子词都带 Ġ，
        // 模型据此区分「词的开头」和「词的中间」。
        foreach (Match match in GptSplit().Matches(" " + word))
        {
            foreach (var piece in Merge(ToByteChars(match.Value)))
            {
                yield return piece;
            }
        }
    }

    private string[] Merge(string chunk)
    {
        if (_cache.TryGetValue(chunk, out var cached))
        {
            return cached;
        }

        var parts = new List<string>(chunk.Length);
        foreach (var character in chunk)
        {
            parts.Add(character.ToString());
        }

        // 反复找出当前序列里 rank 最小的相邻对并合并，直到没有可合并的对。
        while (parts.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestAt = -1;
            for (var i = 0; i + 1 < parts.Count; i++)
            {
                if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestAt = i;
                }
            }

            if (bestAt < 0)
            {
                break;
            }

            // 同一对可能在序列里出现多次，HuggingFace 是一轮合并全部同名对，
            // 逐个合并会产生不同的切分——必须整轮扫。
            var left = parts[bestAt];
            var right = parts[bestAt + 1];
            var merged = new List<string>(parts.Count);
            for (var i = 0; i < parts.Count; )
            {
                if (i + 1 < parts.Count && parts[i] == left && parts[i + 1] == right)
                {
                    merged.Add(left + right);
                    i += 2;
                }
                else
                {
                    merged.Add(parts[i]);
                    i++;
                }
            }

            parts = merged;
        }

        return _cache[chunk] = parts.ToArray();
    }

    /// <summary>
    /// GPT-2 的字节 → 可打印字符映射。把每个字节搬到一个可见的 Unicode 码位上，
    /// 这样 BPE 可以在「字符」上做而不用管字节，空格也变成看得见的 Ġ。
    /// </summary>
    private static readonly char[] ByteToChar = BuildByteMap();

    private static char[] BuildByteMap()
    {
        var map = new char[256];
        var used = new bool[256];

        // 三段本来就可打印的 ASCII / Latin-1 原样保留
        foreach (var (from, to) in new[] { ('!', '~'), ('\u00a1', '\u00ac'), ('\u00ae', '\u00ff') })
        {
            for (var b = from; b <= to; b++)
            {
                map[b] = (char)b;
                used[b] = true;
            }
        }

        // 剩下的（控制符、空格、软连字符等）依次搬到 U+0100 起
        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            if (!used[b])
            {
                map[b] = (char)(256 + next++);
            }
        }

        return map;
    }

    private static string ToByteChars(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            builder.Append(ByteToChar[b]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// GPT-2 的预切分正则，和 HuggingFace tokenizers 的 ByteLevel(use_regex=true) 一致。
    /// 它把 <c>don't</c> 切成 <c>don</c> + <c>'t</c>、<c>well-known</c> 切成三段。
    /// </summary>
    /// <remarks>
    /// 查过一遍：这个词表里跨越正则边界的合并**一个都没有**（唯三的字母数字混合
    /// token 是 fairseq 的 madeupword0000-0002 占位符）。所以去掉正则，黄金样本
    /// 102 条仍然全过——GPT-2 的合并本来就是在切过的文本上学的，学不到跨边界的对。
    /// 保留它是因为它是 HuggingFace 的真实行为，且能把 BPE 的搜索限制在小片段上；
    /// 但也正因为如此，.NET 的 \p{L} 万一和 Rust 有出入，也不会改变输出。
    /// </remarks>
    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex GptSplit();
}
