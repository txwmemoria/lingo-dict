using System.Text.Json;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 整条链路和 Python 端逐词比对。
/// </summary>
/// <remarks>
/// 分词器对上了只说明第一步对了。张量怎么摆、argmax 取哪一维、标签表的行号
/// 和模型的输出维度是否对齐——这些错了都不会抛异常，只会让标签悄悄换成
/// 另一个同样合法的标签。基准是 Python 用**同一个 int8 模型**跑出来的结果，
/// 由 <c>eval/gector_golden.py --onnx</c> 生成。
/// </remarks>
public sealed class TaggingGoldenTests
{
    private sealed record Case(string Text, string[] Labels);

    private static readonly Case[] Golden = JsonSerializer.Deserialize<Case[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "tagging.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static readonly Lazy<GectorGrammarChecker> Checker = new(() =>
    {
        var checker = new GectorGrammarChecker(GrammarModel.Path!);
        checker.WarmUpAsync().GetAwaiter().GetResult();
        Assert.True(checker.IsReady, checker.UnavailableReason);
        return checker;
    });

    public static TheoryData<string> Sentences()
    {
        var data = new TheoryData<string>();
        foreach (var item in Golden)
        {
            data.Add(item.Text);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sentences))]
    public void MatchesPython(string text)
    {
        Assert.SkipWhen(GrammarModel.Path is null, GrammarModel.Missing);

        var expected = Golden.Single(x => x.Text == text);
        var actual = Checker.Value
            .Predict(text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Label)
            .ToArray();

        Assert.Equal(expected.Labels, actual);
    }

    [Fact]
    public void GoldenActuallyContainsEdits()
    {
        // 基准要是全 $KEEP，上面的比对就退化成「两边都什么都没说」。
        Assert.True(
            Golden.Count(c => c.Labels.Any(l => l != "$KEEP")) >= 30,
            "基准里有非 $KEEP 标签的句子太少，比对没有区分度");
    }
}
