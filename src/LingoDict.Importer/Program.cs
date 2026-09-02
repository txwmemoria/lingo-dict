using LingoDict.Infrastructure.Data;

var source = GetOption(args, "--source") ?? Path.Combine(Environment.CurrentDirectory, "data", "kajweb-dict");
var output = GetOption(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "dictionary.db");

Console.WriteLine("LingoDict 数据导入器");
Console.WriteLine($"数据源：{Path.GetFullPath(source)}");
Console.WriteLine($"输出：  {Path.GetFullPath(output)}");
Console.WriteLine();

try
{
    var builder = new DictionaryDatabaseBuilder();
    var result = builder.Build(source, output, value =>
    {
        Console.Write($"\r[{value.CompletedBooks,2}/{value.TotalBooks}] {value.ImportedRows,7:N0} 条  {value.CurrentBook,-52}");
    });

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"完成：{result.BookCount} 本词书，{result.MembershipCount:N0} 条词书记录，{result.WordCount:N0} 个词头");
    var collapsed = result.RowCount - result.MembershipCount;
    if (collapsed > 0)
    {
        Console.WriteLine($"合并：{collapsed:N0} 条同书内归一化重复词头（共读入 {result.RowCount:N0} 行）");
    }

    Console.WriteLine($"数据库：{result.DatabaseBytes / 1024d / 1024d:N1} MiB");
    Console.WriteLine($"耗时：{result.Duration.TotalSeconds:N1} 秒");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"导入失败：{exception.Message}");
    Console.Error.WriteLine(exception);
    return 1;
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
