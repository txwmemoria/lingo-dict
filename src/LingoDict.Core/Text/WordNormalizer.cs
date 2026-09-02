using System.Globalization;
using System.Text;

namespace LingoDict.Core.Text;

public static class WordNormalizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLowerInvariant());
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
