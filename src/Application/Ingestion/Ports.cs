using AISupportOps.Domain.Documents;

namespace AISupportOps.Application.Ingestion;

/// <summary>A contiguous piece of extracted text, e.g. one PDF page or a whole text file.</summary>
public sealed record ExtractedSection(string Text, int? PageNumber);

/// <summary>Turns a stored file into plain text. One implementation per <see cref="DocumentKind"/>.</summary>
public interface ITextExtractor
{
    DocumentKind Kind { get; }

    /// <summary>
    /// True when the output uses Markdown-style "#" heading lines, which the chunker
    /// uses to attach section headings to chunks.
    /// </summary>
    bool EmitsMarkdownHeadings { get; }

    Task<IReadOnlyList<ExtractedSection>> ExtractAsync(Stream content, CancellationToken ct);
}

/// <summary>Counts model tokens. Chunk budgets are in tokens because model limits and cost are.</summary>
public interface ITokenCounter
{
    int Count(string text);
}

/// <summary>Thrown by extractors for files that are corrupt, encrypted, or otherwise unreadable.</summary>
public sealed class DocumentExtractionException(string message, Exception? inner = null) : Exception(message, inner);
