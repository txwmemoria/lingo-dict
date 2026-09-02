using LingoDict.Infrastructure.Data;

namespace LingoDict.Core.Tests;

/// <summary>
/// ECDICT 的读取。
/// </summary>
/// <remarks>
/// 这份 CSV 的 definition / translation 列里带逗号和引号，按逗号切会错位，
/// 而错位之后取到的 frq 和 exchange 是别的列的内容——不报错，只是全都不对。
/// 所以这里逐条钉住列位和引号规则。
/// </remarks>
public sealed class FrequencyIndexTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingodict-tests", Guid.NewGuid().ToString("N"));

    private const string Header =
        "word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail,audio";

    private string Write(params string[] lines)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ecdict.csv");
        File.WriteAllLines(path, [Header, .. lines]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsFrequencyAndFormsFromTheRightColumns()
    {
        var index = FrequencyIndex.Load(Write(
            "perceive,pəˈsiːv,,感知,,,,,3000,2165,d:perceived/p:perceived/3:perceives/i:perceiving,,"));

        Assert.Equal(1, index.Count);
        Assert.Equal(2165, index.Frequency("perceive"));
        Assert.Equal(["perceived", "perceives", "perceiving"], index.Forms("perceive").Order());
    }

    [Fact]
    public void CommasInsideQuotedColumnsDoNotShiftTheColumns()
    {
        // translation 里既有逗号又有转义引号——错一位，frq 就读成了 bnc
        var index = FrequencyIndex.Load(Write(
            "cancel,ˈkænsl,,\"v. 取消，撤销；删去, 又名\"\"废止\"\"\",,,,,1200,880,3:cancels/d:cancelled,,"));

        Assert.Equal(880, index.Frequency("cancel"));
        Assert.Equal(["cancelled", "cancels"], index.Forms("cancel").Order());
    }

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        // 词频是可选输入：没有它导入照常完成，只是少了排序和词形回退
        Assert.Equal(0, FrequencyIndex.Load(null).Count);
        Assert.Equal(0, FrequencyIndex.Load(Path.Combine(_directory, "nope.csv")).Count);
    }

    [Fact]
    public void UnknownFrequencyIsZeroNotSomethingThatSortsFirst()
    {
        var index = FrequencyIndex.Load(Write("chondrule,,,球粒,,,,,0,0,,,"));

        // 0 在排序里必须垫底，不能被当成"最常用"
        Assert.Equal(0, index.Frequency("chondrule"));
        Assert.Empty(index.Forms("chondrule"));
    }

    [Fact]
    public void OnlyInflectionsAreTakenNotTheLemmaPointer()
    {
        // 0 是原形、1 是原形的变换类型，两者都是反向的：从变化形式指回原形。
        // 我们是从已有词头展开变化形式，收了它们会把 cancel 自己也当成"变化形式"。
        var index = FrequencyIndex.Load(Write(
            "cancelled,,,取消,,,,,0,9000,0:cancel/1:p,,"));

        Assert.Empty(index.Forms("cancelled"));
    }

    [Fact]
    public void AFormIdenticalToTheHeadwordIsDropped()
    {
        // put 的过去式还是 put，登记它只会让 word_forms 里多一条指向自己的记录
        var index = FrequencyIndex.Load(Write(
            "put,pʊt,,放置,,,,,100,90,p:put/d:put/3:puts/i:putting,,"));

        Assert.Equal(["puts", "putting"], index.Forms("put").Order());
    }

    [SkippableFact]
    public void ReadsTheRealEcdictIfItIsPresent()
    {
        var path = Locate("data/ecdict/ecdict.csv");
        Skip.If(path is null, "缺少 data/ecdict/ecdict.csv，见 README「词频与词形」");

        var index = FrequencyIndex.Load(path!);

        // 真实文件 77 万行。数量级对不上就说明解析在某处提前停了。
        Assert.True(index.Count > 700_000, $"只读到 {index.Count:N0} 条");
        Assert.True(index.Frequency("the") is > 0 and < 100, $"the 的词频是 {index.Frequency("the")}");
        Assert.Contains("cancelled", index.Forms("cancel"));
    }

    private static string? Locate(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relative.Split('/')]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
