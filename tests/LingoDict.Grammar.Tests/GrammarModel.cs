namespace LingoDict.Grammar.Tests;

/// <summary>
/// 定位仓库里的文件。模型是 129 MB 的构建产物（artifacts/ 不入库），
/// 新克隆的仓库上没有——那时相关用例应当 Skip，而不是假装通过、也不是把 CI 弄红。
/// </summary>
internal static class GrammarModel
{
    public static string? Path { get; } = Find("artifacts/grammar/gector-int8.onnx");

    public const string Missing =
        "缺少 artifacts/grammar/gector-int8.onnx，跑 eval/gector_export.py 生成后再测";

    public static string? Find(string relativePath)
    {
        var parts = relativePath.Split('/');
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
