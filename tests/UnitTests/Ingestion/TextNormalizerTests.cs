using AISupportOps.Application.Ingestion;

namespace AISupportOps.UnitTests.Ingestion;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    [InlineData("too    many \t spaces", "too many spaces")]
    [InlineData("para one\n\n\n\n\npara two", "para one\n\npara two")]
    [InlineData("  line with trailing   \n   next", "line with trailing\nnext")]
    [InlineData("infor-\nmation", "information")]
    [InlineData("ﬁle ＡＢＣ", "file ABC")] // ligature and full-width letters folded by NFKC
    [InlineData("bell\u0007 and nul\u0000 removed", "bell and nul removed")]
    [InlineData("non breaking", "non breaking")]
    [InlineData("   \n\t  ", "")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));
}
