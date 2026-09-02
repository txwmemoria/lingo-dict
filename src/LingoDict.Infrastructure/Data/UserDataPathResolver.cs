namespace LingoDict.Infrastructure.Data;

public static class UserDataPathResolver
{
    public const string CommandLineOption = "--user-data";

    public const string EnvironmentVariable = "LINGODICT_USERDATA";

    /// <summary>
    /// 优先级：<c>--user-data &lt;path&gt;</c> 命令行参数 &gt; <c>LINGODICT_USERDATA</c> 环境变量 &gt; 默认位置。
    /// 前两者是为了能用独立的测试 profile 启动，而不必动真实的用户数据。
    /// </summary>
    public static string Resolve(IReadOnlyList<string>? arguments = null)
    {
        var fromCommandLine = ReadOption(arguments);
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            return Path.GetFullPath(fromCommandLine);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return DefaultPath();
    }

    /// <summary>
    /// Windows 下是 %APPDATA%\LingoDict\userdata.db，macOS/Linux 下是 ~/.config/LingoDict/userdata.db。
    /// 刻意放在应用目录之外，卸载或重装不会带走用户数据。
    /// </summary>
    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(root, "LingoDict", "userdata.db");
    }

    private static string? ReadOption(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], CommandLineOption, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
