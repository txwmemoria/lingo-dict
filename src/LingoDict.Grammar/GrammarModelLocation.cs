namespace LingoDict.Grammar;

/// <summary>模型权重放在哪儿。</summary>
/// <remarks>
/// 安装器把应用装在 <c>%LOCALAPPDATA%\LingoDict</c>，程序目录本来就可写，
/// 下载的权重直接落在那儿最省事——卸载时能一并清掉，也和随包分发时的位置一致。
/// 只有装在只读位置（Program Files、只读介质）时才退到用户目录。
/// </remarks>
public static class GrammarModelLocation
{
    public const string FileName = "gector-int8.onnx";

    private const string FolderName = "grammar";

    /// <summary>按优先级列出可能的位置：程序目录优先，其次用户目录。</summary>
    public static IReadOnlyList<string> Candidates() =>
    [
        Path.Combine(AppContext.BaseDirectory, FolderName, FileName),
        Path.Combine(UserFolder(), FileName),
    ];

    /// <summary>第一个真实存在的权重文件；都不存在时返回 <c>null</c>。</summary>
    public static string? Resolve() => Candidates().FirstOrDefault(File.Exists);

    /// <summary>下载/导入时写到哪儿——程序目录能写就写那儿。</summary>
    public static string TargetPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, FolderName);
        return CanWrite(beside)
            ? Path.Combine(beside, FileName)
            : Path.Combine(UserFolder(), FileName);
    }

    private static string UserFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "LingoDict",
        FolderName);

    /// <summary>真去建一个文件试试。权限判断在 Windows 上光看 ACL 是算不准的。</summary>
    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
