using LingoDict.Core.Text;

namespace LingoDict.Core.Tests;

public sealed class WordNormalizerTests
{
    [Theory]
    [InlineData("Cancel", "cancel")]
    [InlineData(" long-time ", "longtime")]
    [InlineData("café", "cafe")]
    [InlineData("你好", "你好")]
    [InlineData("", "")]
    public void Normalize_ProducesStableSearchKey(string input, string expected)
    {
        Assert.Equal(expected, WordNormalizer.Normalize(input));
    }
}
