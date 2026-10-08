using AISupportOps.Application.Ingestion;
using Microsoft.ML.Tokenizers;

namespace AISupportOps.Infrastructure.Ingestion;

/// <summary>
/// Counts tokens with cl100k_base, the encoding used by OpenAI's text-embedding-3 models,
/// so chunk budgets match what the embedding API actually sees.
/// </summary>
internal sealed class TiktokenTokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public int Count(string text) => _tokenizer.CountTokens(text);
}
