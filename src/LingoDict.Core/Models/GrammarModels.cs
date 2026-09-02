namespace LingoDict.Core.Models;

/// <summary>一处写作建议对原文做的动作。</summary>
public enum GrammarEditKind
{
    /// <summary>把这个词换成别的。</summary>
    Replace,

    /// <summary>在这个词后面补一个词。</summary>
    Insert,

    /// <summary>这个词多余。</summary>
    Delete,

    /// <summary>这个词和后面那个应该连成一个。</summary>
    Merge,

    /// <summary>这个词应该拆开。</summary>
    Split,
}

/// <summary>
/// 一条写作建议。刻意叫「建议」不叫「错误」：模型在评测集上是 79%，
/// 剩下的 21% 不该以「你错了」的口气呈现给学习者。
/// </summary>
/// <param name="Start">在原文里的起始字符位置。</param>
/// <param name="Length">原词的长度；合并两个词时盖住两个词。</param>
/// <param name="Original">原词。</param>
/// <param name="Suggestion">建议的写法；<see cref="GrammarEditKind.Delete"/> 时为空串。</param>
/// <param name="Category">给中文使用者看的归类，如「介词」「冠词」「动词形式」。</param>
/// <param name="Label">模型给出的原始编辑标签，排查问题时用。</param>
/// <param name="Confidence">模型对这个标签的把握，0–1。</param>
public sealed record WritingIssue(
    int Start,
    int Length,
    string Original,
    string Suggestion,
    GrammarEditKind Kind,
    string Category,
    string Label,
    double Confidence);
