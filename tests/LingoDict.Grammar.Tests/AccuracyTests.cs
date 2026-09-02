using System.Text.Json;
using LingoDict.Core.Models;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 把 <c>eval/gec.json</c> 的 42 组最小差异句从**用户看到的那一端**跑一遍。
/// </summary>
/// <remarks>
/// eval 里的数字是在 Python 上按标签判的；这里按 <see cref="GectorGrammarChecker.CheckAsync"/>
/// 真正吐出来的建议判——中间隔着置信度过滤、词形变换失败丢弃、以及标点拆词。
/// 那些环节任何一个改动都会挪动这个数字，所以把它钉在测试里：
/// README 和 eval/RESULTS.md 里写的 81% 就是这条用例保证的。
/// </remarks>
public sealed class AccuracyTests
{
    private sealed record Pair(string Cat, string Right, string Wrong);

    /// <summary>42 组里至少要过这么多。测出来是 34，留一格给量化/依赖升级的抖动。</summary>
    private const int Expected = 33;

    /// <summary>正确句被乱改的上限。误报比漏报要命，这条比上面那条更该守住。</summary>
    private const int MaxFalseAlarms = 4;

    [Fact]
    public void MatchesTheNumberWePublish()
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);

        var path = GrammarModel.Find("eval/gec.json");
        Assert.SkipWhen(path is null, "找不到 eval/gec.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path!));
        var pairs = document.RootElement.GetProperty("pairs").EnumerateArray()
            .Select(x => new Pair(
                x.GetProperty("cat").GetString()!,
                x.GetProperty("right").GetString()!,
                x.GetProperty("wrong").GetString()!))
            .ToArray();

        var checker = new GectorGrammarChecker(GrammarModel.Path!);
        IReadOnlyList<WritingIssue> Check(string text) => checker.CheckAsync(text).GetAwaiter().GetResult();

        int hits = 0, falseAlarms = 0;
        var missed = new List<string>();
        foreach (var pair in pairs)
        {
            var onCorrect = Check(pair.Right);
            var onWrong = Check(pair.Wrong);
            if (onCorrect.Count > 0)
            {
                falseAlarms++;
            }

            if (onCorrect.Count == 0 && onWrong.Count > 0)
            {
                hits++;
            }
            else
            {
                missed.Add($"[{pair.Cat}] {pair.Wrong}");
            }
        }

        var detail = $"通过 {hits}/{pairs.Length}，误报 {falseAlarms}。未通过：{string.Join(" | ", missed)}";
        Assert.True(hits >= Expected, detail);
        Assert.True(falseAlarms <= MaxFalseAlarms, detail);
    }
}
