namespace LingoDict.Infrastructure.Data;

public static class DictionaryPathResolver
{
    public static string Resolve()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "dictionary.db");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "dictionary.db");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return bundled;
    }
}
