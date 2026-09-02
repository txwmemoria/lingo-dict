using LingoDict.Core.Text;

namespace LingoDict.Core.Tests;

/// <summary>
/// 音标切分。切错了不会报错，只会在用户点某个音时教他一个错的音。
/// </summary>
public sealed class PhoneticsTests
{
    [Theory]
    // 双字符音素必须整体切出来，不能拆成两个单字符
    [InlineData("dʒʌdʒ", "dʒ|ʌ|dʒ")]
    [InlineData("tʃer", "tʃ|e|r")]
    [InlineData("ə'baʊt", "ə|'|b|aʊ|t")]
    [InlineData("θɔt", "θ|ɔ|t")]
    // 重音点和长音符并成一段带过，不参与教学
    [InlineData("'kænsl", "'|k|æ|n|s|l")]
    [InlineData("siː", "s|iː")]
    public void SplitsIntoPhonemesLongestFirst(string phonetic, string expected)
    {
        var actual = string.Join('|', Phonetics.Split(phonetic).Select(x => x.Text));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MarksAreNotClickable()
    {
        var segments = Phonetics.Split("ə'baʊt");

        var stress = Assert.Single(segments, x => x.Text == "'");
        Assert.Null(stress.Phoneme);
        Assert.All(segments.Where(x => x.Text != "'"), x => Assert.NotNull(x.Phoneme));
    }

    [Fact]
    public void UnknownCharactersPassThroughInsteadOfDerailingTheRest()
    {
        // 库里有 3 条音标带一个游离的 ʳ。它不该让整条音标不可点。
        var segments = Phonetics.Split("kaʳt");

        Assert.Equal("k|a|ʳ|t", string.Join('|', segments.Select(x => x.Text)));
        Assert.Null(segments.Single(x => x.Text == "ʳ").Phoneme);
        Assert.All(segments.Where(x => x.Text != "ʳ"), x => Assert.NotNull(x.Phoneme));
    }

    [Fact]
    public void EveryPhonemeTeachesWithARealExample()
    {
        Assert.All(Phonetics.All, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Example), $"{x.Symbol} 没有例词");
            // 例词里必须真有那几个字母，否则「指出是哪一段」会指空
            Assert.Contains(x.Spelling, x.Example, StringComparison.OrdinalIgnoreCase);
        });
    }

    [SkippableFact]
    public void TheTableCoversWhatIsActuallyInTheDictionary()
    {
        var db = Locate("artifacts/dictionary.db");
        Skip.If(db is null, "缺少 artifacts/dictionary.db");

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(NULLIF(e.us_phone, ''), e.uk_phone)
            FROM words w JOIN entries e ON e.id = w.canonical_entry_id
            WHERE COALESCE(NULLIF(e.us_phone, ''), e.uk_phone) <> '';
            """;

        int total = 0, clean = 0;
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            total++;
            var gaps = Phonetics.Split(reader.GetString(0))
                .Where(x => x.Phoneme is null && !"'ˈˌ,;:ː-().[]/  ​‘；（）&".Contains(x.Text[0]))
                .ToList();
            if (gaps.Count == 0)
            {
                clean++;
            }
            else
            {
                foreach (var gap in gaps)
                {
                    unknown[gap.Text] = unknown.GetValueOrDefault(gap.Text) + 1;
                }
            }
        }

        // 这张表是照着真实数据配的：99.99% 的音标能整条切开，剩下的是几个游离字符。
        // 掉下来就说明换了数据源或表被删了东西——那时用户会看到一串不可点的音标。
        var rate = clean / (double)total;
        Assert.True(
            rate > 0.99,
            $"只有 {rate:P2} 的音标能完整切开（{clean}/{total}），切不开的：" +
            string.Join(", ", unknown.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key}×{x.Value}")));
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

    [Fact]
    public void LongerSymbolsComeFirstInTheTable()
    {
        // Split 是按表的顺序做最长匹配的。双字符排到单字符后面，
        // tʃ 就会被切成 t + ʃ——这种错不会抛异常，只会教错音。
        var firstSingle = Phonetics.All.ToList().FindIndex(x => x.Symbol.Length == 1);
        var lastMulti = Phonetics.All.ToList().FindLastIndex(x => x.Symbol.Length > 1);

        Assert.True(lastMulti < firstSingle, "多字符音素必须全部排在单字符之前");
    }
}
