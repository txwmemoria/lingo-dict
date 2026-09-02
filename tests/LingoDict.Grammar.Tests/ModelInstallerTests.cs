using System.Security.Cryptography;
using LingoDict.Core.Services;

namespace LingoDict.Grammar.Tests;

/// <summary>
/// 下载和导入模型权重。
/// </summary>
/// <remarks>
/// 这条路径的失败方式都是安静的：拿回一个错误页当模型、断线后接错位置、
/// 服务端无视 Range 却按续传拼接——每一种都会产出一个「看着大小对」的坏文件，
/// 而坏文件要到 onnxruntime 解析时才炸，那时已经离现场很远了。
/// 真实的下载源还没定（上游权重是非商业授权，再分发要先确认），
/// 所以现在这些用例是唯一在跑这段代码的东西。
/// </remarks>
public sealed class ModelInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "lingodict-installer-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly byte[] _payload = Payload(300_000);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadsAndInstalls()
    {
        var server = new FakeModelServer(_payload);
        var installer = Create(server);

        var seen = new Recorder();
        await installer.DownloadAsync(seen, Cancel);

        Assert.True(installer.IsInstalled);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Target, Cancel));
        Assert.False(File.Exists(Target + ".part"), ".part 应该在装好之后消失");
        Assert.Contains(seen.Reports, x => x.Stage == "下载中");
        Assert.Contains(seen.Reports, x => x.Stage == "校验中");
    }

    [Fact]
    public async Task ResumesInsteadOfStartingOver()
    {
        // 第一次传到一半断线
        var server = new FakeModelServer(_payload) { BreakAfter = 100_000 };
        var installer = Create(server);
        await Assert.ThrowsAnyAsync<Exception>(() => installer.DownloadAsync(null, Cancel));
        Assert.Equal(100_000, new FileInfo(Target + ".part").Length);

        // 第二次应当带上 Range 接着下，而不是从零开始
        server.BreakAfter = null;
        await installer.DownloadAsync(null, Cancel);

        Assert.Equal(_payload, await File.ReadAllBytesAsync(Target, Cancel));
        Assert.Equal([null, 100_000L], server.SeenRanges);
    }

    [Fact]
    public async Task ServerIgnoringRangeStillProducesTheRightFile()
    {
        // 有些 CDN 和代理会无视 Range 直接回整个文件。要是照样往 .part 后面追加，
        // 拼出来的长度会超、内容会错位——必须识别出 200 而不是 206，从头写。
        var server = new FakeModelServer(_payload) { BreakAfter = 100_000 };
        var installer = Create(server);
        await Assert.ThrowsAnyAsync<Exception>(() => installer.DownloadAsync(null, Cancel));

        server.BreakAfter = null;
        server.IgnoreRange = true;
        await installer.DownloadAsync(null, Cancel);

        Assert.Equal(_payload, await File.ReadAllBytesAsync(Target, Cancel));
    }

    [Fact]
    public async Task RejectsAFileWhoseHashDoesNotMatch()
    {
        var tampered = (byte[])_payload.Clone();
        tampered[12345] ^= 0xFF;
        var installer = Create(new FakeModelServer(tampered));

        var error = await Assert.ThrowsAsync<IOException>(() => installer.DownloadAsync(null, Cancel));

        Assert.Contains("校验不通过", error.Message);
        Assert.False(installer.IsInstalled);
        // 坏文件必须删掉，否则下次「续传」会从这堆坏字节后面接
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task RejectsAnErrorPagePretendingToBeTheModel()
    {
        // 最常见的失败：拿回一个 200 的登录页/错误页。长度一比就该挡掉，
        // 不该白下完再去算哈希。
        var installer = Create(new FakeModelServer("<html>404</html>"u8.ToArray()));

        var error = await Assert.ThrowsAsync<IOException>(() => installer.DownloadAsync(null, Cancel));

        Assert.Contains("字节", error.Message);
        Assert.False(installer.IsInstalled);
    }

    [Fact]
    public async Task FallsBackToTheNextSource()
    {
        var broken = new FakeModelServer(_payload) { ForceStatus = System.Net.HttpStatusCode.NotFound };
        var working = new FakeModelServer(_payload);
        var installer = new GrammarModelInstaller(
            Source(["https://first.invalid/m.onnx", "https://second.invalid/m.onnx"]),
            () => Target,
            NextClient(broken, working),
            NoValidation);

        await installer.DownloadAsync(null, Cancel);

        Assert.Equal(_payload, await File.ReadAllBytesAsync(Target, Cancel));
        Assert.Equal(1, broken.Requests);
        Assert.Equal(1, working.Requests);
    }

    [Fact]
    public async Task CancellingKeepsWhatWasAlreadyDownloaded()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new FakeModelServer(_payload);
        // 从服务端这一侧触发取消：进度回调最快 200ms 一次，而 300 KB 瞬间就下完了，
        // 靠进度去掐根本来不及——那样测的就不是取消，是「下完之后取消」。
        server.Sent = bytes =>
        {
            if (bytes > 50_000)
            {
                cancellation.Cancel();
            }
        };
        var installer = Create(server);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.DownloadAsync(null, cancellation.Token));

        // 取消不该白费力气：已经落盘的部分留着，下次接着下
        Assert.True(File.Exists(Target + ".part"));
        Assert.InRange(new FileInfo(Target + ".part").Length, 1, _payload.Length - 1);

        await installer.DownloadAsync(null, Cancel);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Target, Cancel));
    }

    [Fact]
    public async Task RefusesToDownloadWithoutASource()
    {
        var installer = new GrammarModelInstaller(Source([]), () => Target, validate: NoValidation);

        Assert.False(installer.CanDownload);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.DownloadAsync(null, Cancel));
        Assert.Contains("本地文件导入", error.Message);
    }

    [Fact]
    public async Task ImportsALocalFile()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "mine.onnx");
        await File.WriteAllBytesAsync(source, _payload, Cancel);

        var installer = new GrammarModelInstaller(Source([]), () => Target, validate: NoValidation);
        await installer.InstallFromFileAsync(source, null, Cancel);

        Assert.True(installer.IsInstalled);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Target, Cancel));
    }

    [Fact]
    public async Task ImportDoesNotCheckTheHash()
    {
        // README 教用户自己跑 gector_export.py 生成模型，换个 torch 版本字节就不一样。
        // 对本地导入卡哈希会把好文件也拒掉，所以这里只做「能不能打开」的检查。
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "mine.onnx");
        await File.WriteAllBytesAsync(source, Payload(1234), Cancel);

        var installer = new GrammarModelInstaller(Source([]), () => Target, validate: NoValidation);
        await installer.InstallFromFileAsync(source, null, Cancel);

        Assert.True(installer.IsInstalled);
    }

    [Fact]
    public async Task ImportRejectsWhatTheModelCheckRefuses()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "not-a-model.onnx");
        await File.WriteAllTextAsync(source, "这不是模型", Cancel);

        var installer = new GrammarModelInstaller(
            Source([]), () => Target, validate: _ => throw new InvalidDataException("打不开"));

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallFromFileAsync(source, null, Cancel));

        Assert.False(installer.IsInstalled);
        Assert.False(File.Exists(Target + ".part"), "没通过检查的文件不该留在盘上");
    }

    [Fact]
    public async Task RemoveClearsBothTheModelAndAnyPartialDownload()
    {
        var installer = Create(new FakeModelServer(_payload) { BreakAfter = 100_000 });
        await Assert.ThrowsAnyAsync<Exception>(() => installer.DownloadAsync(null, Cancel));
        Assert.True(File.Exists(Target + ".part"));

        installer.Remove();

        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public void StaysInsideTheTargetPathItWasGiven()
    {
        // 这一条挡的是一个真实事故：IsInstalled 和 Remove 原来会顺着默认候选
        // 路径摸到 %LOCALAPPDATA%\LingoDict\grammar 去。后果有两个——
        // 跑一遍测试就把开发机上真正装好的那份 123 MiB 权重删了；
        // 而且 IsInstalled 会随那份权重在不在而时真时假，
        // 表现成本项目里追了三轮才逮到的「偶发失败」。
        var installer = new GrammarModelInstaller(Source([]), () => Target, validate: NoValidation);

        Assert.False(installer.IsInstalled, "指定了落点就不该再去看全局位置");
        Assert.StartsWith(_directory, installer.ModelPath, StringComparison.OrdinalIgnoreCase);

        // Remove 也一样：只许动自己那一份
        installer.Remove();
        Assert.StartsWith(_directory, installer.ModelPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 同步记录进度。
    /// </summary>
    /// <remarks>
    /// 不用 <see cref="Progress{T}"/>：它把回调 Post 到捕获的同步上下文上，
    /// 测试里没有同步上下文就丢到线程池，于是断言可能跑在回调之前——
    /// 机器闲的时候过、和别的测试项目并行跑满 CPU 时就红，最难查的那种。
    /// </remarks>
    private sealed class Recorder : IProgress<ModelInstallProgress>
    {
        public List<ModelInstallProgress> Reports { get; } = [];

        public void Report(ModelInstallProgress value) => Reports.Add(value);
    }

    private string Target => Path.Combine(_directory, "grammar", "gector-int8.onnx");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private GrammarModelInstaller Create(FakeModelServer server) => new(
        Source(["https://example.invalid/gector-int8.onnx"]),
        () => Target,
        server.CreateClient,
        NoValidation);

    private GrammarModelSource Source(string[] urls) => new(urls, _payload.Length, Sha256(_payload));

    /// <summary>用例里的载荷不是真 ONNX，所以把「拿 onnxruntime 打开」这一步换掉。</summary>
    private static void NoValidation(string path)
    {
    }

    private static Func<HttpClient> NextClient(params FakeModelServer[] servers)
    {
        var index = 0;
        return () => servers[Math.Min(index++, servers.Length - 1)].CreateClient();
    }

    /// <summary>可复现的伪随机字节：全零的话「错位拼接」这种 bug 反而测不出来。</summary>
    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        var random = new Random(20260828);
        random.NextBytes(bytes);
        return bytes;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
