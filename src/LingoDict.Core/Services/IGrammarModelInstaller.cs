namespace LingoDict.Core.Services;

/// <param name="Received">已经落盘的字节数（含断点续传之前已有的部分）。</param>
/// <param name="Total">总字节数；未知时为 0。</param>
/// <param name="BytesPerSecond">最近一段时间的平均速度。</param>
/// <param name="Stage">给界面显示的当前阶段，如「下载中」「校验中」。</param>
public sealed record ModelInstallProgress(long Received, long Total, double BytesPerSecond, string Stage);

/// <summary>
/// 装配写作检查用的模型权重。
/// </summary>
/// <remarks>
/// 权重有 123 MiB，且是第三方产物（仅限非商业用途），所以不进安装包，
/// 由用户在应用内明确地下载或从本地导入。
/// </remarks>
public interface IGrammarModelInstaller
{
    /// <summary>权重是否已经就位。</summary>
    bool IsInstalled { get; }

    /// <summary>已装上时是它的路径，否则是装上之后会在的位置。</summary>
    string ModelPath { get; }

    /// <summary>下载后应有的字节数，用来在界面上显示体积、也用来挡住下到一半的文件。</summary>
    long ExpectedBytes { get; }

    /// <summary>配没配下载源。没有时界面只提供「从本地导入」。</summary>
    bool CanDownload { get; }

    /// <summary>从下载源取回权重。支持断点续传，取消后再调会接着上次下。</summary>
    Task DownloadAsync(IProgress<ModelInstallProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>把用户自己准备的 .onnx 装进来。</summary>
    Task InstallFromFileAsync(
        string sourcePath,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>删掉已装的权重，连同下了一半的临时文件。</summary>
    void Remove();
}
