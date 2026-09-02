namespace LingoDict.App.Tests;

/// <summary>
/// 应用图标的文件格式。
/// </summary>
/// <remarks>
/// ICO 从 Vista 起允许内嵌 PNG，但实际上只有 256×256 那一档是各处都认的。
/// 小尺寸用 PNG 时，<c>ExtractAssociatedIcon</c> 这类 API 读得出来，
/// 资源管理器建图标缓存的那条路径却可能读不出来——症状是 exe 图标看着是好的，
/// 任务栏和开始菜单却显示系统默认图标，不报任何错。
///
/// 图标由 <c>scripts/make-icon.py</c> 生成，那个脚本已经按惯例写了；
/// 这条用例是防着有人换一张图标时又用回全 PNG。
/// </remarks>
public sealed class IconTests
{
    /// <summary>超过这一档才允许用 PNG，和 make-icon.py 里的 PNG_FROM 对齐。</summary>
    private const int PngFrom = 128;

    private sealed record Entry(int Width, int Height, int BitCount, bool IsPng, int Length);

    [Fact]
    public void SmallSizesAreBitmapsNotPng()
    {
        var entries = Read();

        Assert.All(
            entries.Where(x => x.Width < PngFrom),
            x => Assert.False(x.IsPng, $"{x.Width}×{x.Height} 用了 PNG——任务栏和开始菜单可能读不出来"));
    }

    /// <summary>
    /// 微软 app-icon-construction「Icon scaling」那张表里出现的每一档。
    /// </summary>
    /// <remarks>
    /// <code>
    ///   缩放             100%  125%  150%  200%  250%  300%  400%
    ///   右键菜单/标题栏/托盘   16    20    24    32    40    48    64
    ///   任务栏/搜索结果/所有应用 24    30    36    48    60    72    96
    ///   开始磁贴            32    40    48    64    80    96   256
    /// </code>
    /// Windows 先找完全相同的尺寸，找不到才取上一档缩下来。缺档不会报错，
    /// 只会让某个界面上的图标发虚甚至退回系统默认——150% 下任务栏和搜索结果
    /// 要的 36px 就曾经缺过，症状正是那两处显示默认图标。
    /// </remarks>
    [Fact]
    public void CoversEverySizeInMicrosoftsScalingTable()
    {
        var widths = Read().Select(x => x.Width).ToHashSet();

        int[] required = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
        var missing = required.Where(x => !widths.Contains(x)).ToArray();

        Assert.True(missing.Length == 0, $"缺这几档：{string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryEntryIsSquare()
    {
        Assert.All(Read(), x => Assert.Equal(x.Width, x.Height));
    }

    [Fact]
    public void EveryEntryIsThirtyTwoBitAndNonEmpty()
    {
        var entries = Read();

        Assert.NotEmpty(entries);
        Assert.All(entries, x => Assert.Equal(32, x.BitCount));
        Assert.All(entries, x => Assert.True(x.Length > 0, $"{x.Width}×{x.Height} 是空的"));
    }

    private static List<Entry> Read()
    {
        var path = Locate("src/LingoDict.App/Assets/lingodict.ico");
        Assert.SkipWhen(path is null, "找不到 lingodict.ico");

        var bytes = File.ReadAllBytes(path!);
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));

        var count = BitConverter.ToUInt16(bytes, 4);
        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var at = 6 + (i * 16);
            // 目录里 0 表示 256——这个字段只有一个字节
            var width = bytes[at] == 0 ? 256 : bytes[at];
            var height = bytes[at + 1] == 0 ? 256 : bytes[at + 1];
            var length = BitConverter.ToInt32(bytes, at + 8);
            var offset = BitConverter.ToInt32(bytes, at + 12);
            var isPng = bytes.AsSpan(offset, 8).SequenceEqual("\x89PNG\r\n\x1a\n"u8);
            entries.Add(new Entry(width, height, BitConverter.ToUInt16(bytes, at + 6), isPng, length));
        }

        return entries;
    }

    private static string? Locate(string relative)
    {
        var parts = relative.Split('/');
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
