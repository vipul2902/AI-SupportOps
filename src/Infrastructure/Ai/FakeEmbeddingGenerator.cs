using System.Text;
using AISupportOps.Domain.Documents;
using Microsoft.Extensions.AI;

namespace AISupportOps.Infrastructure.Ai;

/// <summary>
/// Deterministic "embedding" via feature hashing: each word and adjacent word pair is hashed into
/// one of 1536 buckets (with a hashed sign), then the vector is L2-normalized. Texts sharing words
/// get high cosine similarity, which makes retrieval behaviour testable without network calls,
/// API keys, or cost. It does not understand synonyms; real quality requires a real model.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelId = "fake-hashing-embedding-v1";

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(v => new Embedding<float>(Embed(v)) { ModelId = ModelId }).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public static float[] Embed(string text)
    {
        var vector = new float[DocumentChunk.EmbeddingDimensions];
        var words = Tokenize(text);
        for (var i = 0; i < words.Count; i++)
        {
            Add(vector, words[i], 1.0f);
            if (i + 1 < words.Count)
            {
                Add(vector, words[i] + " " + words[i + 1], 0.5f);
            }
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private static List<string> Tokenize(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static void Add(float[] vector, string feature, float weight)
    {
        var hash = Fnv1a(feature);
        var bucket = (int)(hash % (uint)vector.Length);
        vector[bucket] += (hash & 0x8000_0000) == 0 ? weight : -weight;
    }

    /// <summary>Stable across processes and runtimes (unlike string.GetHashCode).</summary>
    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
