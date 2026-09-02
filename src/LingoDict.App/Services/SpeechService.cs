using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace LingoDict.App.Services;

public interface ISpeechService
{
    bool IsAvailable { get; }

    /// <summary>可用嗓音；不可用时为空。</summary>
    IReadOnlyList<string> Voices { get; }

    void Speak(string text, string? voice);
}

/// <summary>
/// 用操作系统自带的 TTS 朗读，完全离线。
///
/// 源数据里的 ukspeech/usspeech 只是有道 TTS 接口的查询串（如 "abruptly&amp;type=1"），
/// 压缩包内没有任何音频文件；用它们就必须联网并把查询词发给第三方，与"离线词典"相悖。
/// </summary>
/// <remarks>
/// 全程只用一个发声器，新的朗读会打断上一条。
///
/// 原来每次朗读都新建一个 SAPI SpVoice、异步播、谁也不打断谁，Unix 那边则是每次
/// 起一个新的 say/spd-say 子进程。快点几下「再听一遍」「下一个」或音素按钮，
/// 几个声音就一起出来了——听音辨词整个功能都建立在「听清一个词」上，重叠一次就废了。
/// 顺带，那些 SpVoice 和 Process 谁也没释放。
/// </remarks>
public sealed class SpeechService : ISpeechService, IDisposable
{
    private readonly Lazy<IReadOnlyList<string>> _voices;
    private readonly Lock _gate = new();

    /// <summary>Windows 的 SAPI 发声器，整个进程共用一个。</summary>
    private dynamic? _speaker;

    /// <summary>macOS/Linux 上正在念的那个子进程。</summary>
    private Process? _process;

    private bool _disposed;

    public SpeechService()
    {
        _voices = new Lazy<IReadOnlyList<string>>(Discover);
    }

    public bool IsAvailable => Voices.Count > 0;

    public IReadOnlyList<string> Voices => _voices.Value;

    /// <summary>
    /// 建过几个 SAPI 发声器。测试用：这个数字必须一直是 1，
    /// 「每次朗读新建一个」正是声音会重叠的根因。
    /// </summary>
    internal int SpeakerCreations { get; private set; }

    public void Speak(string text, string? voice)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SpeakWindows(text, voice);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Run("say", voice is null ? [text] : ["-v", voice, text]);
                }
                else
                {
                    Run("spd-say", ["-e", text]);
                }
            }
            catch (Exception)
            {
                // 朗读失败不该打断查词；IsAvailable 已经在设置页提示过能力缺失。
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            KillPrevious();

            if (_speaker is not null)
            {
                if (OperatingSystem.IsWindows())
                {
                    // 退出时把还没念完的那条掐掉，否则声音会拖到窗口关了以后
                    TryPurge(_speaker);
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_speaker);
                }

                _speaker = null;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void SpeakWindows(string text, string? voice)
    {
        var speaker = Speaker();
        if (speaker is null)
        {
            return;
        }

        if (voice is not null)
        {
            var tokens = speaker.GetVoices();
            for (var index = 0; index < tokens.Count; index++)
            {
                if (string.Equals((string)tokens.Item(index).GetDescription(), voice, StringComparison.Ordinal))
                {
                    speaker.Voice = tokens.Item(index);
                    break;
                }
            }
        }

        // 1 = SVSFlagsAsync（不阻塞界面线程）
        // 2 = SVSFPurgeBeforeSpeak（先清掉还在念的，这条是「不重叠」的关键）
        speaker.Speak(text, 1 | 2);
    }

    /// <summary>共用的 SAPI 发声器，第一次用到时才建。</summary>
    [SupportedOSPlatform("windows")]
    private dynamic? Speaker()
    {
        if (_speaker is not null)
        {
            return _speaker;
        }

        var type = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (type is null)
        {
            return null;
        }

        _speaker = Activator.CreateInstance(type);
        if (_speaker is not null)
        {
            SpeakerCreations++;
        }

        return _speaker;
    }

    [SupportedOSPlatform("windows")]
    private static void TryPurge(dynamic speaker)
    {
        try
        {
            // 念一句空的，只为带上 SVSFPurgeBeforeSpeak
            speaker.Speak(string.Empty, 1 | 2);
        }
        catch (Exception)
        {
            // 已经断开的 COM 对象没什么可清的
        }
    }

    private IReadOnlyList<string> Discover()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return DiscoverWindows();
            }

            if (OperatingSystem.IsMacOS())
            {
                return [.. Capture("say", ["-v", "?"])
                    .Select(line => line.Split("  ", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => x!)];
            }

            return Capture("spd-say", ["--version"]).Count > 0 ? ["默认"] : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<string> DiscoverWindows()
    {
        // 走同一个发声器：枚举嗓音原来会再建一个 SpVoice 然后丢掉
        var speaker = Speaker();
        if (speaker is null)
        {
            return [];
        }

        var tokens = speaker.GetVoices();
        var result = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            result.Add((string)tokens.Item(index).GetDescription());
        }

        return result;
    }

    /// <summary>起一个念词的子进程，先掐掉上一个。</summary>
    private void Run(string file, string[] arguments)
    {
        KillPrevious();

        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        _process = Process.Start(info);
    }

    private void KillPrevious()
    {
        var previous = _process;
        _process = null;
        if (previous is null)
        {
            return;
        }

        try
        {
            if (!previous.HasExited)
            {
                previous.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 已经自己退干净了
        }
        finally
        {
            previous.Dispose();
        }
    }

    private static IReadOnlyList<string> Capture(string file, string[] arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return [];
        }

        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        process.WaitForExit(3000);
        return lines;
    }
}
