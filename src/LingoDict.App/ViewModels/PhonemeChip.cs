using System.Collections.Generic;
using System.Linq;
using LingoDict.Core.Text;

namespace LingoDict.App.ViewModels;

/// <summary>
/// 音标里的一格。认得的音素可点可悬停，重音点这类记号只是陪着显示。
/// </summary>
/// <remarks>
/// 一条音标最多十来个符号，所以直接渲染成一排独立控件，
/// 而不是像例句取词那样在一整块文本上按坐标命中。短文本上前者简单得多，
/// 也天然拿到 hover 和点击。
/// </remarks>
public sealed record PhonemeChip(string Text, Phoneme? Phoneme)
{
    /// <summary>能不能教。记号不能，不认识的字符也不能。</summary>
    public bool CanTeach => Phoneme is not null;

    /// <summary>悬停时显示的说明。</summary>
    public string Hint => Phoneme is null
        ? string.Empty
        : Phoneme.Tip.Length == 0
            ? $"{Phoneme.Symbol}　如 {Phoneme.Example}　（点击朗读）"
            : $"{Phoneme.Symbol}　如 {Phoneme.Example}\n{Phoneme.Tip}　（点击朗读）";

    public static IReadOnlyList<PhonemeChip> From(string? phonetic) =>
        [.. Phonetics.Split(phonetic).Select(x => new PhonemeChip(x.Text, x.Phoneme))];
}
