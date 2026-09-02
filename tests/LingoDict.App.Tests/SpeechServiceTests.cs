using LingoDict.App.Services;

namespace LingoDict.App.Tests;

/// <summary>
/// 朗读不重叠。
/// </summary>
/// <remarks>
/// 原来每次朗读都新建一个 SAPI SpVoice、异步播、谁也不打断谁——快点几下
/// 「再听一遍」「下一个」或音素按钮，几个声音一起出来，听音辨词直接失效。
///
/// 声音重不重叠没法从测试里听，所以这里钉的是根因：
/// **只能有一个发声器**。数量一旦回到「每次一个」，重叠必然跟着回来。
/// </remarks>
public sealed class SpeechServiceTests
{
    [Fact]
    public void RepeatedSpeakingReusesOneVoiceInsteadOfStackingThemUp()
    {
        using var speech = new SpeechService();
        Assert.SkipUnless(OperatingSystem.IsWindows() && speech.IsAvailable, "这台机器上没有可用的 SAPI 嗓音");

        for (var round = 0; round < 10; round++)
        {
            speech.Speak("cancel", null);
        }

        // 枚举嗓音那次也走同一个——原来 DiscoverWindows 会另建一个然后丢掉
        Assert.Equal(1, speech.SpeakerCreations);
    }

    [Fact]
    public void DisposingStopsWhateverIsStillBeingRead()
    {
        var speech = new SpeechService();
        Assert.SkipUnless(OperatingSystem.IsWindows() && speech.IsAvailable, "这台机器上没有可用的 SAPI 嗓音");

        speech.Speak("cancellation", null);
        speech.Dispose();

        // Dispose 之后再念是静默的空操作，不是 ObjectDisposedException：
        // 关窗口时界面上还可能有挂着的朗读命令。
        speech.Speak("cancel", null);
        speech.Dispose();
    }

    [Fact]
    public void EmptyTextIsIgnoredWithoutTouchingTheSystem()
    {
        using var speech = new SpeechService();

        speech.Speak("", null);
        speech.Speak("   ", null);

        Assert.Equal(0, speech.SpeakerCreations);
    }
}
