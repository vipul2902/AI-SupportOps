using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Ai;

namespace AISupportOps.UnitTests.Knowledge;

public class EmbeddingAndQueryTests
{
    [Fact]
    public void Fake_embeddings_are_deterministic_normalized_and_schema_sized()
    {
        var a = FakeEmbeddingGenerator.Embed("Reset your password in Settings");
        var b = FakeEmbeddingGenerator.Embed("Reset your password in Settings");

        Assert.Equal(DocumentChunk.EmbeddingDimensions, a.Length);
        Assert.Equal(a, b);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => (double)x * x)), precision: 5);
    }

    [Fact]
    public void Fake_embeddings_rank_overlapping_text_above_unrelated_text()
    {
        var query = FakeEmbeddingGenerator.Embed("how do I reset my password");
        var relevant = FakeEmbeddingGenerator.Embed("To reset your password, open Settings and choose Reset password.");
        var unrelated = FakeEmbeddingGenerator.Embed("Shipping usually takes three business days within the EU.");

        Assert.True(Cosine(query, relevant) > Cosine(query, unrelated));
    }

    [Theory]
    [InlineData("  how   do\tI\n reset ", "how do I reset")]
    [InlineData("bell\u0007 removed", "bell removed")]
    [InlineData("   ", "")]
    public void Query_preprocessing_normalizes_whitespace_and_control_characters(string input, string expected) =>
        Assert.Equal(expected, KnowledgeSearchService.Preprocess(input));

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
}
