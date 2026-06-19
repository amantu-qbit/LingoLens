using LingoLens.Core.Translation;
using Xunit;

namespace LingoLens.Tests.Core;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("  hello   world  ", "hello world")]
    [InlineData("line\n\tbreak", "line break")]
    [InlineData("", "")]
    public void Normalize_collapses_whitespace(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Normalize_folds_fullwidth_to_halfwidth()
    {
        // Full-width "ＡＢＣ" should fold to ASCII "ABC" under NFKC.
        Assert.Equal("ABC", TextNormalizer.Normalize("ＡＢＣ"));
    }

    [Fact]
    public void Hash_is_stable_and_urlsafe()
    {
        var h1 = TextNormalizer.Hash("你好世界");
        var h2 = TextNormalizer.Hash("你好世界");
        Assert.Equal(h1, h2);
        Assert.DoesNotContain('+', h1);
        Assert.DoesNotContain('/', h1);
        Assert.DoesNotContain('=', h1);
    }

    [Fact]
    public void Hash_differs_for_different_input() =>
        Assert.NotEqual(TextNormalizer.Hash("a"), TextNormalizer.Hash("b"));

    [Theory]
    [InlineData("你好", true)]
    [InlineData("Hello", false)]
    [InlineData("混合text", true)]
    [InlineData("123 !@#", false)]
    public void ContainsCjk_detects_ideographs(string text, bool expected) =>
        Assert.Equal(expected, TextNormalizer.ContainsCjk(text));

    [Fact]
    public void CjkRatio_is_one_for_pure_chinese() =>
        Assert.Equal(1.0, TextNormalizer.CjkRatio("你好世界"), 3);

    [Fact]
    public void CjkRatio_is_zero_for_pure_latin() =>
        Assert.Equal(0.0, TextNormalizer.CjkRatio("hello"), 3);
}
