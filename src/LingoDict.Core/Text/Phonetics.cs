namespace LingoDict.Core.Text;

public enum PhonemeKind
{
    /// <summary>单元音。</summary>
    Vowel,

    /// <summary>双元音。</summary>
    Diphthong,

    /// <summary>辅音。</summary>
    Consonant,
}

/// <summary>
/// 一个音素，以及用来教它的那个例词。
/// </summary>
/// <param name="Symbol">音标符号，如 <c>ɪ</c>。</param>
/// <param name="Example">例词。挑的都是初中词汇——认不出例词，这条就白搭。</param>
/// <param name="Spelling">例词里发这个音的字母，用来在词里指出是哪一段。</param>
/// <param name="Tip">
/// 中文使用者在这个音上的典型错法。没有特别坑的就留空——
/// 每条都写等于没写，用户会全部略过。
/// </param>
public sealed record Phoneme(string Symbol, string Example, string Spelling, PhonemeKind Kind, string Tip = "");

/// <summary>
/// 音标的符号表和切分。
/// </summary>
/// <remarks>
/// 目的很具体：用户看着 <c>'trendɪŋ</c> 不知道 <c>ɪ</c> 怎么读时，
/// 能点它一下，听到「sit」。音标是符号系统，光听整词学不会；
/// 把符号绑到一个已经会读的词上，才教得动。
///
/// 库里 21,319 条音标，用这张表能完整切开 21,316 条（99.99%），
/// 剩下 3 条含一个游离的 ʳ。切不开的符号原样显示、不可点，不影响其余部分。
/// </remarks>
public static class Phonetics
{
    /// <summary>
    /// 音标里不发音的记号：重音点、长音符、分隔符、括号。切分时跳过。
    /// </summary>
    private const string Marks = "'ˈˌ,;:ː-().[]/  ​‘；（）&";

    /// <summary>
    /// 全部音素。
    /// </summary>
    /// <remarks>
    /// 按**美音**收，因为库里的音标是美式的（measure 记作 'mɛʒɚ、bird 记作 bɝd）。
    /// 同时收了几个英音才有的（ɑː iː ɒ əʊ ɜː），源数据里混着少量英音。
    ///
    /// 顺序要紧：<see cref="Split"/> 按最长匹配来，双字符的必须排在单字符前面，
    /// 否则 <c>tʃ</c> 会被切成 <c>t</c> + <c>ʃ</c>。
    /// </remarks>
    public static IReadOnlyList<Phoneme> All { get; } =
    [
        // ——— 双元音（两个字符，必须排最前）———
        new("aɪ", "my", "y", PhonemeKind.Diphthong),
        new("aʊ", "now", "ow", PhonemeKind.Diphthong),
        new("ɔɪ", "boy", "oy", PhonemeKind.Diphthong),
        new("eɪ", "day", "ay", PhonemeKind.Diphthong),
        new("oʊ", "go", "o", PhonemeKind.Diphthong),
        new("əʊ", "go", "o", PhonemeKind.Diphthong),
        new("ɪə", "here", "ere", PhonemeKind.Diphthong),
        new("ɛə", "hair", "air", PhonemeKind.Diphthong),
        new("ʊə", "sure", "ure", PhonemeKind.Diphthong),

        // ——— 长元音（带长音符，也是两个字符）———
        new("iː", "see", "ee", PhonemeKind.Vowel, "拖长的「衣」，和 ɪ 是长短之分"),
        new("ɑː", "car", "ar", PhonemeKind.Vowel),
        new("uː", "too", "oo", PhonemeKind.Vowel),
        new("ɔː", "law", "aw", PhonemeKind.Vowel),
        new("ɜː", "bird", "ir", PhonemeKind.Vowel),

        // ——— 双字符辅音 ———
        new("tʃ", "chair", "ch", PhonemeKind.Consonant),
        new("dʒ", "job", "j", PhonemeKind.Consonant),

        // ——— 单元音 ———
        new("ɪ", "sit", "i", PhonemeKind.Vowel, "短而松，不是「衣」。sit 和 seat 靠这个区分"),
        new("i", "happy", "y", PhonemeKind.Vowel),
        new("e", "bed", "e", PhonemeKind.Vowel),
        new("ɛ", "bed", "e", PhonemeKind.Vowel),
        new("æ", "cat", "a", PhonemeKind.Vowel, "嘴张得比「哎」大，bad 不是 bed"),
        new("ɑ", "hot", "o", PhonemeKind.Vowel),
        new("ɒ", "hot", "o", PhonemeKind.Vowel),
        new("ɔ", "dog", "o", PhonemeKind.Vowel),
        // 记作 Diphthong 而不是 Vowel：库里这个 o 就是 oʊ 的偷懒写法，
        // 归到单元音里，音标表上「go」会在双元音和单元音各出现一次。
        new("o", "go", "o", PhonemeKind.Diphthong),
        new("ʊ", "book", "oo", PhonemeKind.Vowel, "短而松，不是「乌」"),
        new("u", "too", "oo", PhonemeKind.Vowel),
        new("ʌ", "cup", "u", PhonemeKind.Vowel),
        new("ə", "about", "a", PhonemeKind.Vowel, "轻读音，几乎不出力。英语里最常见的音"),
        new("ɚ", "teacher", "er", PhonemeKind.Vowel, "轻读的儿化音，舌头往回卷一点"),
        new("ɝ", "bird", "ir", PhonemeKind.Vowel, "重读的儿化音"),
        new("ɜ", "bird", "ir", PhonemeKind.Vowel),
        // 同理，这个游离的 a 就是 ɑː；拼写也记成 ar，好和 ɑː 折成同一张卡。
        new("a", "car", "ar", PhonemeKind.Vowel),

        // ——— 单字符辅音 ———
        new("p", "pen", "p", PhonemeKind.Consonant),
        new("b", "book", "b", PhonemeKind.Consonant),
        new("t", "ten", "t", PhonemeKind.Consonant),
        new("d", "dog", "d", PhonemeKind.Consonant),
        new("k", "cat", "c", PhonemeKind.Consonant),
        new("g", "go", "g", PhonemeKind.Consonant),
        new("ɡ", "go", "g", PhonemeKind.Consonant),
        new("f", "five", "f", PhonemeKind.Consonant),
        new("v", "very", "v", PhonemeKind.Consonant, "上齿咬下唇，不是 w。very 不是 wery"),
        new("θ", "think", "th", PhonemeKind.Consonant, "舌尖伸到齿间送气，不是 s"),
        new("ð", "this", "th", PhonemeKind.Consonant, "舌尖伸到齿间出声，不是 z"),
        new("s", "sun", "s", PhonemeKind.Consonant),
        new("z", "zoo", "z", PhonemeKind.Consonant),
        new("ʃ", "she", "sh", PhonemeKind.Consonant),
        new("ʒ", "measure", "s", PhonemeKind.Consonant),
        new("m", "man", "m", PhonemeKind.Consonant),
        new("n", "no", "n", PhonemeKind.Consonant, "舌尖抵上齿龈，和 ŋ 不同"),
        new("ŋ", "sing", "ng", PhonemeKind.Consonant, "鼻音收尾，舌尖不碰上颚"),
        new("l", "look", "l", PhonemeKind.Consonant, "在词尾要把舌尖顶上去，如 well"),
        new("r", "red", "r", PhonemeKind.Consonant, "舌头卷起但不碰上颚，不是汉语的「日」"),
        new("w", "we", "w", PhonemeKind.Consonant),
        new("j", "yes", "y", PhonemeKind.Consonant),
        new("h", "hat", "h", PhonemeKind.Consonant),
    ];

    private static readonly Dictionary<string, Phoneme> BySymbol =
        All.GroupBy(x => x.Symbol).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

    /// <summary>查一个音素；不认识返回 <c>null</c>。</summary>
    public static Phoneme? Find(string symbol) => BySymbol.GetValueOrDefault(symbol);

    /// <summary>音标里的一段：要么是一个认得的音素，要么是原样带过的记号。</summary>
    /// <param name="Text">原文里的那几个字符。</param>
    /// <param name="Phoneme">认出来的音素；<c>null</c> 表示重音点这类记号或不认识的字符。</param>
    public readonly record struct Segment(string Text, Phoneme? Phoneme);

    /// <summary>
    /// 把一条音标切成一段段，认得的音素单独成段。
    /// </summary>
    /// <remarks>
    /// 最长匹配：先试双字符再试单字符，否则 <c>tʃ</c> 会被切成 <c>t</c> + <c>ʃ</c>、
    /// <c>aʊ</c> 会被切成 <c>a</c> + <c>ʊ</c>——切错了点上去教的就是错的音。
    /// </remarks>
    public static IReadOnlyList<Segment> Split(string? phonetic)
    {
        var result = new List<Segment>();
        if (string.IsNullOrEmpty(phonetic))
        {
            return result;
        }

        var index = 0;
        while (index < phonetic.Length)
        {
            if (Marks.Contains(phonetic[index], StringComparison.Ordinal))
            {
                // 相邻的记号并成一段，免得渲染出一串独立的小方块
                var start = index;
                while (index < phonetic.Length && Marks.Contains(phonetic[index], StringComparison.Ordinal))
                {
                    index++;
                }

                result.Add(new Segment(phonetic[start..index], null));
                continue;
            }

            var matched = false;
            foreach (var phoneme in All)
            {
                if (phonetic.AsSpan(index).StartsWith(phoneme.Symbol, StringComparison.Ordinal))
                {
                    result.Add(new Segment(phoneme.Symbol, phoneme));
                    index += phoneme.Symbol.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                // 不认识的字符原样带过。切不开的那 3 条音标走这里，
                // 只是那一个字符不可点，其余照常。
                result.Add(new Segment(phonetic[index].ToString(), null));
                index++;
            }
        }

        return result;
    }
}
