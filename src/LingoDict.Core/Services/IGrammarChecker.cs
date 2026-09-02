using LingoDict.Core.Models;

namespace LingoDict.Core.Services;

/// <summary>对一段英文给出写作建议。</summary>
public interface IGrammarChecker
{
    /// <summary>模型是否已就绪。权重是单独分发的，装不上时整个功能应当优雅缺席而不是崩。</summary>
    bool IsReady { get; }

    /// <summary>模型缺失或加载失败时的原因，给界面显示；就绪时为 <c>null</c>。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 提前把模型准备好。
    /// </summary>
    /// <remarks>
    /// 加载可能要一两秒（权重上百兆），放在用户第一次点检查时做会像卡住。
    /// 界面切到写作页时先调它。重复调用无副作用。
    /// </remarks>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 忘掉「模型在不在、能不能用」的判断，下次用时重新解析。
    /// </summary>
    /// <remarks>
    /// 权重是运行期装上的：刚下载完，检查器里还记着「没有安装写作检查模型」。
    /// 装完调一次这个，界面才不用重启就能用上。
    /// </remarks>
    void Invalidate();

    /// <summary>按原文里的字符位置返回建议，已按出现顺序排好。</summary>
    Task<IReadOnlyList<WritingIssue>> CheckAsync(string text, CancellationToken cancellationToken = default);
}
