using System.Text.Json;
using LingoDict.Grammar;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 拿 HuggingFace 的输出当基准，逐条比对 C# 分词器。
/// </summary>
/// <remarks>
/// 黄金样本由 <c>eval/gector_golden.py</c> 生成，含 42 组评测句的正反两句
/// 加 18 条容易踩空的输入（缩写、连字符、货币、多空格、非 ASCII、生造词）。
/// 分词器出错不会抛异常，只会让下游标签悄悄错位，所以只能这样验。
/// </remarks>
public sealed class TokenizerGoldenTests
{
    private sealed record Case(string Text, int[] Ids, string[] Tokens, int[] WordIds);

    private static readonly Case[] Golden = LoadGolden();
    private static readonly ByteLevelBpeTokenizer Tokenizer = ByteLevelBpeTokenizer.Create();

    private static Case[] LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "tokenizer.json");
        return JsonSerializer.Deserialize<Case[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

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
    public void MatchesHuggingFace(string text)
    {
        var expected = Golden.Single(x => x.Text == text);
        // 和推理时一样：先按空白切词，$START 由分词器自己插
        var (ids, wordIds) = Tokenizer.Encode(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(expected.Ids, ids);
        Assert.Equal(expected.WordIds, wordIds);
    }

    [Fact]
    public void GoldenCoversTheTrickyInputs()
    {
        // 样本文件要是被换成个空壳，上面的 Theory 会「全部通过」。
        Assert.True(Golden.Length >= 100, $"黄金样本只有 {Golden.Length} 条，太少了");
        Assert.Contains(Golden, x => x.Text.Contains('\''));
        Assert.Contains(Golden, x => x.Text.Contains('-'));
        Assert.Contains(Golden, x => x.Text.Any(c => c > 127));
    }

    [Fact]
    public void StartTokenSitsRightAfterBos()
    {
        var (ids, wordIds) = Tokenizer.Encode(["Hello"]);

        Assert.Equal(Tokenizer.BosId, ids[0]);
        Assert.Equal(Tokenizer.StartId, ids[1]);
        Assert.Equal(Tokenizer.EosId, ids[^1]);
        // $START 占 0 号词位，真实单词从 1 开始——标签对齐全靠这个约定
        Assert.Equal(0, wordIds[1]);
        Assert.Equal(1, wordIds[2]);
    }
}
