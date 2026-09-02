namespace LingoDict.Core.Text;

/// <summary>
/// 一组最小差异词：只差一个音的两个词。
/// </summary>
/// <param name="Left">左边那个词。</param>
/// <param name="Right">右边那个词。</param>
/// <param name="LeftSymbol">左词里那个音。</param>
/// <param name="RightSymbol">右词里那个音。</param>
/// <param name="Focus">这一组在练什么，一句话。</param>
public sealed record MinimalPair(string Left, string Right, string LeftSymbol, string RightSymbol, string Focus);

/// <summary>
/// 听音辨词的题库。
/// </summary>
/// <remarks>
/// 挑的全是**中文母语者分不清**的对立，不是随便找两个像的词。
/// 汉语里没有的区别（长短元音、θ/s、v/w、n/ŋ 韵尾、l/r）才值得练；
/// 本来就分得清的练了也是白练。
///
/// 这件事比认音标符号更实用：听不出区别，就永远读不对，
/// 而认得符号却听不出来的人很多。
/// </remarks>
public static class MinimalPairs
{
    public static IReadOnlyList<MinimalPair> All { get; } =
    [
        // 长短元音——汉语没有这个区别，是最普遍的一类
        new("ship", "sheep", "ɪ", "iː", "短 ɪ 对长 iː"),
        new("bit", "beat", "ɪ", "iː", "短 ɪ 对长 iː"),
        new("live", "leave", "ɪ", "iː", "短 ɪ 对长 iː"),
        new("fill", "feel", "ɪ", "iː", "短 ɪ 对长 iː"),
        new("full", "fool", "ʊ", "uː", "短 ʊ 对长 uː"),
        new("pull", "pool", "ʊ", "uː", "短 ʊ 对长 uː"),

        // æ 对 e——「哎」和「诶」在汉语里不区分意义
        new("bad", "bed", "æ", "e", "æ 嘴张得更大"),
        new("man", "men", "æ", "e", "æ 嘴张得更大"),
        new("sat", "set", "æ", "e", "æ 嘴张得更大"),
        new("had", "head", "æ", "e", "æ 嘴张得更大"),

        // θ / ð——汉语完全没有的齿间音
        new("think", "sink", "θ", "s", "θ 舌尖伸到齿间"),
        new("thick", "sick", "θ", "s", "θ 舌尖伸到齿间"),
        new("mouth", "mouse", "θ", "s", "θ 舌尖伸到齿间"),
        new("breathe", "breeze", "ð", "z", "ð 舌尖伸到齿间"),

        // v / w——汉语没有唇齿音 v
        new("vest", "west", "v", "w", "v 上齿咬下唇"),
        new("vine", "wine", "v", "w", "v 上齿咬下唇"),
        new("veil", "wail", "v", "w", "v 上齿咬下唇"),

        // n / ŋ 韵尾——南方口音尤其容易混
        new("sin", "sing", "n", "ŋ", "词尾 n 舌尖抵齿龈，ŋ 不碰"),
        new("thin", "thing", "n", "ŋ", "词尾 n 舌尖抵齿龈，ŋ 不碰"),
        new("ban", "bang", "n", "ŋ", "词尾 n 舌尖抵齿龈，ŋ 不碰"),
        new("win", "wing", "n", "ŋ", "词尾 n 舌尖抵齿龈，ŋ 不碰"),

        // l / r——汉语的「日」既不是 l 也不是 r
        new("light", "right", "l", "r", "r 舌头卷起但不碰上颚"),
        new("glass", "grass", "l", "r", "r 舌头卷起但不碰上颚"),
        new("collect", "correct", "l", "r", "r 舌头卷起但不碰上颚"),
        new("lace", "race", "l", "r", "r 舌头卷起但不碰上颚"),

        // s / ʃ 和 tʃ / ʃ
        new("see", "she", "s", "ʃ", "ʃ 舌位更后、嘴更圆"),
        new("chip", "ship", "tʃ", "ʃ", "tʃ 有个爆破的开头"),
        new("cheap", "sheep", "tʃ", "ʃ", "tʃ 有个爆破的开头"),
        new("watch", "wash", "tʃ", "ʃ", "tʃ 有个爆破的开头"),
    ];
}
