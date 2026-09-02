using Microsoft.ML.OnnxRuntime;

namespace LingoDict.Grammar;

/// <summary>拿 onnxruntime 真打开一次，确认这个文件确实是我们要的那个模型。</summary>
/// <remarks>
/// 用户自己导入的文件不能只看大小：可能是别的模型、别的量化方式、或者下了一半的。
/// 打开会话再核对输出维度和标签表行数，是这里能做的最实在的一道检查——
/// 标签表错位一行，整个输出就是乱的，而它不会抛任何异常。
/// </remarks>
internal static class GrammarModelCheck
{
    /// <summary>建一个会话并核对标签维度。不合格直接抛，消息给界面显示。</summary>
    /// <returns>校验通过的会话，调用方负责释放；只想校验的话直接 <c>using</c>。</returns>
    public static InferenceSession Open(string modelPath, SessionOptions options)
    {
        var session = new InferenceSession(modelPath, options);
        try
        {
            var labels = GrammarAssets.ReadLines("labels.txt").Count();
            var outputSize = session.OutputMetadata["logits"].Dimensions[^1];
            if (outputSize > 0 && outputSize != labels)
            {
                throw new InvalidDataException(
                    $"模型输出 {outputSize} 类，内嵌标签表却有 {labels} 行，两者不匹配");
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public static void Validate(string modelPath)
    {
        using var options = new SessionOptions();
        using var session = Open(modelPath, options);
    }
}
