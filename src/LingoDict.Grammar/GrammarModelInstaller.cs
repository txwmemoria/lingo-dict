using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LingoDict.Core.Services;

namespace LingoDict.Grammar;

/// <param name="Urls">下载源，按顺序试。</param>
/// <param name="Bytes">文件应有的字节数。</param>
/// <param name="Sha256">文件应有的 SHA-256（小写十六进制）。</param>
public sealed record GrammarModelSource(IReadOnlyList<string> Urls, long Bytes, string Sha256);

/// <summary>
/// 下载或导入 GECToR 的 int8 权重。
/// </summary>
/// <remarks>
/// 权重不进安装包：它有 123 MiB，而且是第三方产物——gotutiyan/gector 的模型卡写明
/// <c>Only non-commercial purposes</c>（训练数据含 NUCLE 和 Lang-8，本身就禁止商用）。
/// 做成应用内按需获取，安装包干净，第三方权重也变成用户明确同意的动作。
/// </remarks>
public sealed class GrammarModelInstaller : IGrammarModelInstaller
{
    /// <summary>
    /// eval/gector_export.py 产出的那一份 int8 模型。
    /// </summary>
    /// <remarks>
    /// 托管在本仓库的 <c>model-v1</c> Release，由 <c>.github/workflows/model.yml</c> 生成：
    /// 和 tests/LingoDict.Grammar.Tests/Golden/tagging.json 102/102 逐词一致，42 组 34/42。
    /// 字节数和哈希跟作者本地那份（128_683_357 字节）不同，是导出工具版本带来的元数据差异，
    /// 推理结果一致。重新发布模型时三个字段都要跟着换，见 docs/grammar.md。
    /// 许可仍是上游的「仅限非商业用途」，界面在下载前会提示。
    /// </remarks>
    public static readonly GrammarModelSource Official = new(
        Urls: ["https://github.com/txwmemoria/lingo-dict/releases/download/model-v1/gector-int8.onnx"],
        Bytes: 128_737_105,
        Sha256: "1c7c3bd46b7ad528afb69c1fce63e39583c8e632094398bb4412d9550087d8bd");

    private readonly GrammarModelSource _source;
    private readonly Func<string> _targetPath;
    private readonly Func<IReadOnlyList<string>> _candidates;
    private readonly Func<HttpClient> _clientFactory;
    private readonly Action<string> _validate;

    /// <param name="source">下载源和校验信息。</param>
    /// <param name="targetPath">装到哪儿。</param>
    /// <param name="clientFactory">建 HttpClient 的方式，测试用来塞假的处理器。</param>
    /// <param name="validate">装好之前的最后一道检查，默认真拿 onnxruntime 打开一次。</param>
    public GrammarModelInstaller(
        GrammarModelSource? source = null,
        Func<string>? targetPath = null,
        Func<HttpClient>? clientFactory = null,
        Action<string>? validate = null)
    {
        _source = source ?? Official;
        _targetPath = targetPath ?? GrammarModelLocation.TargetPath;

        // 调用方指定了落点，就**只**认这个落点。
        // 不这么做的话，IsInstalled 和 Remove 会顺着默认候选路径摸到
        // %LOCALAPPDATA%\LingoDict\grammar 去——于是跑一遍测试就把用户
        // 真正装好的那份权重删了，而且 IsInstalled 会因为那份权重在不在
        // 而时真时假，表现成一个「偶发」失败。
        _candidates = targetPath is null
            ? GrammarModelLocation.Candidates
            : () => [targetPath()];
        // 不设总超时：123 MiB 在慢网上要下很久，超时该由取消令牌管
        _clientFactory = clientFactory ?? (() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        _validate = validate ?? GrammarModelCheck.Validate;
    }

    public bool IsInstalled => _candidates().Any(File.Exists);

    public string ModelPath => _candidates().FirstOrDefault(File.Exists) ?? _targetPath();

    public long ExpectedBytes => _source.Bytes;

    public bool CanDownload => _source.Urls.Count > 0;

    public async Task DownloadAsync(
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_source.Urls.Count == 0)
        {
            throw new InvalidOperationException("没有配置下载源，请改用「从本地文件导入」。");
        }

        var target = _targetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";

        var failures = new List<string>();
        foreach (var url in _source.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await FetchAsync(url, partial, progress, cancellationToken).ConfigureAwait(false);
                await FinishAsync(partial, target, verifyHash: true, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // 取消保留 .part，下次接着下
                throw;
            }
            catch (Exception error)
            {
                failures.Add($"{Host(url)}：{error.Message}");
            }
        }

        throw new IOException("所有下载源都没成功。" + string.Join("；", failures));
    }

    private async Task FetchAsync(
        string url,
        string partial,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var client = _clientFactory();
        var already = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (already > _source.Bytes)
        {
            // 上次下的比预期还大，说明源换了或文件坏了，重来
            File.Delete(partial);
            already = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (already > 0)
        {
            request.Headers.Range = new RangeHeaderValue(already, null);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // 服务端可能无视 Range 直接给整个文件。那就得从头写而不是往后追加，
        // 否则拼出来的长度对得上、内容却是错位的——而错位只有校验哈希时才会暴露。
        var append = already > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var offset = append ? already : 0;

        var declared = (response.Content.Headers.ContentLength ?? 0) + offset;
        if (declared != _source.Bytes)
        {
            // 最常见的失败不是网断，是拿回来一个登录页或错误页。先按长度挡掉，
            // 省得白下几十兆再去算哈希。
            throw new IOException($"服务端说这个文件有 {declared} 字节，预期 {_source.Bytes}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partial,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 16);

        await CopyAsync(input, output, offset, _source.Bytes, "下载中", progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task InstallFromFileAsync(
        string sourcePath,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"找不到 {sourcePath}");
        }

        var target = _targetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";

        var total = new FileInfo(sourcePath).Length;
        await using (var input = File.OpenRead(sourcePath))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            await CopyAsync(input, output, 0, total, "复制中", progress, cancellationToken).ConfigureAwait(false);
        }

        // 本地导入**不**校验哈希：README 就是教用户自己跑 gector_export.py 生成的，
        // 换个 torch 版本导出来字节就不一样，拿哈希卡会把好文件也拒掉。
        // 改用更实在的检查——真拿 onnxruntime 打开它，看输出维度对不对得上标签表。
        await FinishAsync(partial, target, verifyHash: false, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(
        string partial,
        string target,
        bool verifyHash,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var size = new FileInfo(partial).Length;

        if (verifyHash)
        {
            progress?.Report(new ModelInstallProgress(size, size, 0, "校验中"));
            var actual = await Sha256Async(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, _source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // 校验不过就得删掉：留着它，下次「续传」会从这堆坏字节后面接
                File.Delete(partial);
                throw new IOException($"校验不通过：算出来是 {Short(actual)}，预期 {Short(_source.Sha256)}");
            }
        }

        progress?.Report(new ModelInstallProgress(size, size, 0, "检查模型"));
        try
        {
            _validate(partial);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        File.Move(partial, target, overwrite: true);
    }

    public void Remove()
    {
        foreach (var path in _candidates().Append(_targetPath()).Distinct())
        {
            Delete(path);
            Delete(path + ".part");
        }

        static void Delete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>边拷边报进度。<paramref name="offset"/> 是续传时文件里已有的字节数。</summary>
    private static async Task CopyAsync(
        Stream input,
        Stream output,
        long offset,
        long total,
        string stage,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        var received = offset;
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var windowStart = TimeSpan.Zero;
        var windowBytes = offset;

        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                // 每 200ms 报一次就够了，报太密只会让界面忙着重绘
                if (watch.Elapsed - lastReport < TimeSpan.FromMilliseconds(200))
                {
                    continue;
                }

                var seconds = (watch.Elapsed - windowStart).TotalSeconds;
                progress?.Report(new ModelInstallProgress(
                    received, total, seconds > 0 ? (received - windowBytes) / seconds : 0, stage));
                lastReport = watch.Elapsed;
                windowStart = watch.Elapsed;
                windowBytes = received;
            }
        }
        finally
        {
            // 中断或取消时也要把缓冲刷干净，.part 才是能接着下的
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        progress?.Report(new ModelInstallProgress(received, total, 0, stage));
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Short(string hash) => hash.Length > 16 ? hash[..16] + "…" : hash;

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
