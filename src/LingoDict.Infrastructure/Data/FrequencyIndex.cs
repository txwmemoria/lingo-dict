using System.Text;
using LingoDict.Core.Text;

namespace LingoDict.Infrastructure.Data;

/// <summary>
/// 词频和词形变化，来自 ECDICT（MIT）。
/// </summary>
/// <remarks>
/// 词书源数据里没有这两样，而它们各自解决一个具体问题：
///
/// **词频**——搜索结果原来按「命中类型 → bm25 → 词头长度」排。词头长度只是
/// 「常用度」的粗糙替身：搜「取消」时 <c>cancel</c> 和 <c>abrogate</c> 没法区分，
/// 首屏浏览更是纯按长度排，短的生僻词排在常用词前面。
///
/// **词形变化**——库里 23,755 个词头基本都是原形，用户输入 <c>cancelled</c>
/// 查不到任何东西。ECDICT 的 exchange 字段给出每个词的变化形式，
/// 实测能新增 25,229 个可查形式，几乎翻一倍。
///
/// 这份数据是可选输入：文件不在时索引为空，导入照常完成，
/// 只是没有词频、也没有词形回退。
/// </remarks>
public sealed class FrequencyIndex
{
    /// <summary>ECDICT 的列序：word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail,audio</summary>
    private const int WordColumn = 0;

    private const int FrequencyColumn = 9;

    private const int ExchangeColumn = 10;

    /// <summary>
    /// exchange 里我们要的那几类。
    /// </summary>
    /// <remarks>
    /// p 过去式、d 过去分词、i 现在分词、3 第三人称单数、
    /// r 比较级、t 最高级、s 复数。
    /// 刻意不收 <c>0</c>（原形）和 <c>1</c>（原形的变换类型）：
    /// 那两个是反向的——从变化形式指回原形，而我们是从已有的原形展开变化形式。
    /// </remarks>
    private const string WantedKinds = "pdi3rts";

    private readonly Dictionary<string, int> _frequency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _forms = new(StringComparer.Ordinal);

    private FrequencyIndex()
    {
    }

    public static FrequencyIndex Empty { get; } = new();

    /// <summary>读到了多少条。为 0 表示这次导入没有词频数据。</summary>
    public int Count => _frequency.Count;

    /// <summary>
    /// 读入 ECDICT。文件不存在时返回空索引而不是抛错——它是可选输入。
    /// </summary>
    public static FrequencyIndex Load(string? csvPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
        {
            return Empty;
        }

        var index = new FrequencyIndex();
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var header = ReadRecord(reader);
        if (header is null || header.Count <= ExchangeColumn)
        {
            throw new InvalidDataException($"{csvPath} 的列数不像 ECDICT（需要至少 {ExchangeColumn + 1} 列）。");
        }

        while (ReadRecord(reader) is { } row)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Count <= ExchangeColumn)
            {
                continue;
            }

            var normalized = WordNormalizer.Normalize(row[WordColumn]);
            if (normalized.Length == 0)
            {
                continue;
            }

            // 同一个词可能出现多行，保留第一行——ECDICT 自身基本已去重。
            if (index._frequency.ContainsKey(normalized))
            {
                continue;
            }

            index._frequency[normalized] = int.TryParse(row[FrequencyColumn], out var frequency) && frequency > 0
                ? frequency
                : 0;

            var forms = ParseExchange(row[ExchangeColumn], normalized);
            if (forms.Count > 0)
            {
                index._forms[normalized] = forms;
            }
        }

        return index;
    }

    /// <summary>词频排名，越小越常用；0 表示未知。</summary>
    public int Frequency(string normalizedHeadword) => _frequency.GetValueOrDefault(normalizedHeadword);

    /// <summary>这个词头的变化形式（已归一化、已去掉与词头相同的）。</summary>
    public IReadOnlyList<string> Forms(string normalizedHeadword) =>
        _forms.TryGetValue(normalizedHeadword, out var forms) ? forms : [];

    private static List<string> ParseExchange(string exchange, string normalizedHeadword)
    {
        var result = new List<string>();
        if (exchange.Length == 0)
        {
            return result;
        }

        foreach (var part in exchange.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon != 1 || !WantedKinds.Contains(part[0], StringComparison.Ordinal))
            {
                continue;
            }

            var form = WordNormalizer.Normalize(part[(colon + 1)..]);
            if (form.Length > 0 && !string.Equals(form, normalizedHeadword, StringComparison.Ordinal)
                && !result.Contains(form, StringComparer.Ordinal))
            {
                result.Add(form);
            }
        }

        return result;
    }

    /// <summary>
    /// 读一条 CSV 记录。
    /// </summary>
    /// <remarks>
    /// 自己写而不是引第三方包：只要三列，而 definition / translation 里带逗号和引号，
    /// 按逗号切会错位——所以引号规则必须照 RFC 4180 处理，但也仅此而已。
    /// 返回 <c>null</c> 表示到文件末尾。
    /// </remarks>
    private static List<string>? ReadRecord(StreamReader reader)
    {
        if (reader.EndOfStream)
        {
            return null;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        while (true)
        {
            var read = reader.Read();
            if (read < 0)
            {
                fields.Add(field.ToString());
                return fields;
            }

            var c = (char)read;
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }

                // 引号内的两个连续引号代表一个字面引号
                if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                    continue;
                }

                quoted = false;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    return fields;
                default:
                    field.Append(c);
                    break;
            }
        }
    }
}
