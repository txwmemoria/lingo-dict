using System.IO.Compression;
using System.Reflection;

namespace LingoDict.Grammar;

/// <summary>嵌在程序集里的分词表 / 标签表 / 动词词形表，全部以 gzip 存放。</summary>
internal static class GrammarAssets
{
    public static Stream Open(string name)
    {
        var resource = $"LingoDict.Grammar.Assets.{name}.gz";
        var stream = typeof(GrammarAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"程序集里找不到内嵌资源 {resource}，可用的有：" +
                string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames()));
        return new GZipStream(stream, CompressionMode.Decompress);
    }

    public static IEnumerable<string> ReadLines(string name)
    {
        using var reader = new StreamReader(Open(name));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
