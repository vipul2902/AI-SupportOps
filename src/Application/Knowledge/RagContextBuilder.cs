using System.Globalization;
using System.Net;
using System.Text;
using AISupportOps.Application.Ingestion;

namespace AISupportOps.Application.Knowledge;

/// <summary>A retrieved chunk that made it into the prompt, with its citation number.</summary>
public sealed record ContextSource(int Number, RetrievedChunk Chunk);

public sealed record RagContext(string Text, IReadOnlyList<ContextSource> Sources, int TokenCount);

/// <summary>
/// Turns ranked chunks into the "sources" block of the prompt:
/// numbered, best-first, within a token budget, with document metadata, and with the content
/// escaped so a malicious document cannot close the &lt;source&gt; tag and smuggle in instructions.
/// </summary>
public sealed class RagContextBuilder(ITokenCounter tokens)
{
    public RagContext Build(IReadOnlyList<RetrievedChunk> rankedChunks, int maxTokens)
    {
        var sources = new List<ContextSource>();
        var builder = new StringBuilder();
        var used = 0;

        foreach (var chunk in rankedChunks.DistinctBy(c => c.ChunkId))
        {
            var number = sources.Count + 1;
            var block = Format(number, chunk);
            var blockTokens = tokens.Count(block);
            if (used + blockTokens > maxTokens)
            {
                // Chunks are ranked, so skipping the rest drops only the least relevant ones.
                break;
            }

            builder.Append(block).Append('\n');
            sources.Add(new ContextSource(number, chunk));
            used += blockTokens;
        }

        return new RagContext(builder.ToString().TrimEnd(), sources, used);
    }

    private static string Format(int number, RetrievedChunk chunk)
    {
        var attributes = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"id=\"{number}\" document=\"{Attr(chunk.FileName)}\"");
        if (chunk.PageNumber is { } page)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" page=\"{page}\"");
        }

        if (chunk.Heading is { } heading)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" section=\"{Attr(heading)}\"");
        }

        return $"<source {attributes}>\n{EscapeContent(chunk.Content)}\n</source>";
    }

    private static string Attr(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Neutralizes anything that looks like our delimiters inside untrusted document text.</summary>
    public static string EscapeContent(string content) =>
        content
            .Replace("<source", "&lt;source", StringComparison.OrdinalIgnoreCase)
            .Replace("</source", "&lt;/source", StringComparison.OrdinalIgnoreCase)
            .Replace("<question", "&lt;question", StringComparison.OrdinalIgnoreCase)
            .Replace("</question", "&lt;/question", StringComparison.OrdinalIgnoreCase);
}
