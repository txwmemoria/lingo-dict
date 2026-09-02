using LingoDict.Core.Models;
using LingoDict.Core.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LingoDict.Grammar;

/// <summary>
/// 用 GECToR（序列标注式语法纠错，int8 量化后 129 MB）给出写作建议。
/// </summary>
/// <remarks>
/// 选它而不是生成式小模型的理由和数字都在 <c>eval/RESULTS.md</c>：
/// 输出空间是 5001 个固定编辑标签，结构上编不出理由；42 组最小差异句上
/// 81%、每句 13 ms，误报只有 3/42——对「建议」类功能，误报比漏报要命得多。
///
/// 只跑一遍，不做 GECToR 原版的多轮迭代：迭代会改变词的位置，而界面要把建议
/// 画回原文的字符区间上；而且评测的 79% 就是单轮的数字，多轮会让实际表现和
/// 测出来的数字对不上。
/// </remarks>
public sealed class GectorGrammarChecker : IGrammarChecker, IDisposable
{
    /// <summary>模型训练长度是 80 个 token，留出首尾三个特殊符号的位置。</summary>
    private const int MaxPieces = 77;

    private readonly Func<string?> _locate;
    private readonly double _minConfidence;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private ByteLevelBpeTokenizer? _tokenizer;
    private EditLabel? _edits;
    private string[]? _labels;
    private InferenceSession? _session;
    private string? _unavailable;

    /// <param name="locate">
    /// 去哪儿找 gector-int8.onnx，找不到返回 <c>null</c>。
    /// 是个委托而不是固定路径：权重可以在运行期由用户下载或导入，
    /// 装完调一次 <see cref="Invalidate"/> 就能重新解析，不必重启。
    /// </param>
    /// <param name="minConfidence">
    /// 低于这个把握的建议直接不显示。
    /// 默认 0.3 是量出来的：在 42 组最小差异句上，0～0.4 都是 34/42，
    /// 0.5 掉到 32、0.6 掉到 26，而误报数从头到尾都是 3——
    /// 也就是说抬高阈值只会砍掉真问题，换不来更干净的输出。取 0.3 留一点余量。
    /// </param>
    public GectorGrammarChecker(Func<string?>? locate = null, double minConfidence = 0.3)
    {
        _locate = locate ?? GrammarModelLocation.Resolve;
        _minConfidence = minConfidence;
        _unavailable = Missing();
    }

    /// <summary>固定路径的重载，测试和一次性场景用。</summary>
    public GectorGrammarChecker(string modelPath, double minConfidence = 0.3)
        : this(() => File.Exists(modelPath) ? modelPath : null, minConfidence)
    {
    }

    public bool IsReady => _unavailable is null;

    /// <summary>权重是否已经在内存里。构造时必须还是 false，否则启动会白等一两秒。</summary>
    internal bool IsLoaded => _session is not null;

    public string? UnavailableReason => _unavailable;

    /// <summary>
    /// 提前把模型读进内存。129 MB 的 session 建起来要一两秒，
    /// 放在用户点「检查」之后会像卡住，所以界面切到这一页时就先调它。
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null || _unavailable is not null)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null || _unavailable is not null)
            {
                return;
            }

            await Task.Run(LoadModel, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        _session?.Dispose();
        _session = null;
        _unavailable = Missing();
    }

    private string? Missing() => _locate() is null ? "没有安装写作检查模型" : null;

    private void LoadModel()
    {
        try
        {
            var path = _locate() ?? throw new FileNotFoundException("没有安装写作检查模型");
            var options = new SessionOptions
            {
                // 别把机器占满：用户是一边写一边检查，界面不能被推理拖住
                IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            var session = GrammarModelCheck.Open(path, options);
            _labels = GrammarAssets.ReadLines("labels.txt").ToArray();
            _tokenizer = ByteLevelBpeTokenizer.Create();
            _edits = new EditLabel(VerbForms.Create());
            _session = session;
        }
        catch (Exception error)
        {
            _unavailable = $"写作检查模型加载失败：{error.Message}";
        }
    }

    public async Task<IReadOnlyList<WritingIssue>> CheckAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        if (_session is null || _tokenizer is null || _edits is null || _labels is null)
        {
            return [];
        }

        return await Task.Run(() => Check(text, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private List<WritingIssue> Check(string text, CancellationToken cancellationToken)
    {
        var issues = new List<WritingIssue>();
        foreach (var sentence in EnglishText.Sentences(text, maxWords: 60))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var chunk in Chunk(sentence))
            {
                Tag(chunk, issues);
            }
        }

        return issues;
    }

    /// <summary>
    /// 一句话仍可能超过 77 个 token（长单词、大量标点）。这里按 token 数贪心切块，
    /// 而不是让分词器截断——截断是静默的，后半句会当作没写过。
    /// </summary>
    private IEnumerable<List<WordSpan>> Chunk(List<WordSpan> sentence)
    {
        var chunk = new List<WordSpan>();
        var pieces = 0;
        foreach (var word in sentence)
        {
            var cost = _tokenizer!.PieceCount(word.Text);
            if (chunk.Count > 0 && pieces + cost > MaxPieces)
            {
                yield return chunk;
                chunk = [];
                pieces = 0;
            }

            chunk.Add(word);
            pieces += cost;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// 逐词跑一遍模型，返回每个词的标签和把握度。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是为了能拿它和 Python 端逐词比对：<see cref="CheckAsync"/> 产出的
    /// 建议列表会因为置信度阈值、解析不出的标签而丢条目，用它验不出整条链路
    /// （张量摆放、argmax、标签表行号）是不是真的对齐。
    /// </remarks>
    internal (string Label, double Confidence)[] Predict(IReadOnlyList<string> words)
    {
        var (ids, wordIds) = _tokenizer!.Encode(words);

        var input = new DenseTensor<long>([1, ids.Length]);
        var mask = new DenseTensor<long>([1, ids.Length]);
        for (var i = 0; i < ids.Length; i++)
        {
            input[0, i] = ids[i];
            mask[0, i] = 1;
        }

        using var results = _session!.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", input),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        ]);

        var logits = results.First(r => r.Name == "logits").AsTensor<float>();
        var classes = logits.Dimensions[^1];

        // 一个词切成多个子词时只看第一个子词的标签——训练时就是这么对齐的。
        // $START（词位 0）和特殊符号（-1）不产生标签。
        var tags = new (string, double)[words.Count];
        var seen = new HashSet<int>();
        for (var position = 0; position < ids.Length; position++)
        {
            var wordIndex = wordIds[position];
            if (wordIndex <= 0 || !seen.Add(wordIndex))
            {
                continue;
            }

            var (best, confidence) = Best(logits, position, classes);
            tags[wordIndex - 1] = (_labels![best], confidence);
        }

        // 被截断的尾巴（正常不该发生，Chunk 已经按 token 数切过）当作没问题
        for (var i = 0; i < tags.Length; i++)
        {
            tags[i].Item1 ??= "$KEEP";
        }

        return tags;
    }

    private void Tag(List<WordSpan> words, List<WritingIssue> issues)
    {
        var tags = Predict(words.Select(w => w.Text).ToArray());
        for (var i = 0; i < words.Count; i++)
        {
            var (label, confidence) = tags[i];
            if (label == "$KEEP" || confidence < _minConfidence)
            {
                continue;
            }

            var word = words[i];
            var next = i + 1 < words.Count ? words[i + 1] : (WordSpan?)null;
            // 解析不出来（比如动词表里没有目标形态）就整条丢掉，
            // 宁可漏报也不给一个自己都算不出来的建议
            if (_edits!.Resolve(label, word.Text, next?.Text) is not { } resolved)
            {
                continue;
            }

            var (suggestion, kind) = resolved;

            // 模型偶尔会「建议」把词改成它原本的样子，那不是建议
            if (kind == GrammarEditKind.Replace && suggestion == word.Text)
            {
                continue;
            }

            // 合并两个词时区间要盖住两个：界面按这个区间画下划线、也按它替换文本。
            var length = kind == GrammarEditKind.Merge && next is { } following
                ? following.End - word.Start
                : word.Text.Length;

            issues.Add(new WritingIssue(
                word.Start,
                length,
                word.Text,
                suggestion,
                kind,
                EditLabel.Categorize(label, word.Text, suggestion),
                label,
                confidence));
        }
    }

    /// <summary>取概率最大的标签，顺带算出它的 softmax 概率作为把握度。</summary>
    private static (int Index, double Confidence) Best(Tensor<float> logits, int position, int classes)
    {
        var top = 0;
        var max = float.NegativeInfinity;
        for (var c = 0; c < classes; c++)
        {
            var value = logits[0, position, c];
            if (value > max)
            {
                max = value;
                top = c;
            }
        }

        // 减去最大值再取指数，5002 类直接 exp 会溢出
        var sum = 0.0;
        for (var c = 0; c < classes; c++)
        {
            sum += Math.Exp(logits[0, position, c] - max);
        }

        return (top, 1.0 / sum);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _loadGate.Dispose();
    }
}
