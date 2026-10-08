using System.Text.RegularExpressions;

namespace AISupportOps.Application.Ingestion;

public sealed record TextChunk(string Content, int TokenCount, int? PageNumber, string? Heading);

/// <summary>
/// Recursive, structure-aware chunking: split into paragraphs; paragraphs that are too large into
/// sentences; sentences that are still too large into word windows. Then greedily pack these units
/// up to the token budget, starting each new chunk with the tail of the previous one (overlap).
/// Splitting on natural boundaries keeps chunks semantically coherent, which improves embeddings.
/// </summary>
public sealed partial class TextChunker(ITokenCounter tokens, int maxTokens, int overlapTokens)
{
    private readonly int _overlapTokens = Math.Min(overlapTokens, maxTokens / 2);

    private sealed record Unit(string Text, int Tokens, string? Heading, bool IsHeading = false);

    public IReadOnlyList<TextChunk> Chunk(IEnumerable<ExtractedSection> sections, bool markdownHeadings)
    {
        var chunks = new List<TextChunk>();
        string? heading = null;

        // Chunks never span pages (one page per citation) and, for Markdown/DOCX, never span
        // headings: each chunk covers one topic, which keeps its embedding focused. Found by the
        // evaluation suite: multi-section chunks diluted embeddings and missed short questions.
        foreach (var section in sections)
        {
            var units = new List<Unit>();
            foreach (var paragraph in section.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (markdownHeadings && HeadingLine().Match(paragraph) is { Success: true } match)
                {
                    // Flush the previous section, unless it is only headings (e.g. a document title
                    // directly followed by its first section): a chunk with no body is retrieval noise,
                    // so those headings carry forward into the next chunk instead.
                    if (units.Any(u => !u.IsHeading))
                    {
                        Pack(units, section.PageNumber, chunks);
                        units = [];
                    }

                    heading = match.Groups["title"].Value.Trim();
                    units.Add(new Unit(paragraph, tokens.Count(paragraph), heading, IsHeading: true));
                    continue;
                }

                units.AddRange(SplitToFit(paragraph).Select(text => new Unit(text, tokens.Count(text), heading)));
            }

            Pack(units, section.PageNumber, chunks);
        }

        return chunks;
    }

    private void Pack(List<Unit> units, int? page, List<TextChunk> chunks)
    {
        var current = new List<Unit>();
        var currentTokens = 0;
        var hasNewContent = false;

        foreach (var unit in units)
        {
            if (currentTokens + unit.Tokens > maxTokens && hasNewContent)
            {
                Emit(current, page, chunks);
                current = TakeOverlap(current);
                currentTokens = current.Sum(u => u.Tokens);
                hasNewContent = false;

                // Never let overlap push a chunk over budget.
                if (currentTokens + unit.Tokens > maxTokens)
                {
                    current.Clear();
                    currentTokens = 0;
                }
            }

            current.Add(unit);
            currentTokens += unit.Tokens;
            hasNewContent = true;
        }

        if (hasNewContent)
        {
            Emit(current, page, chunks);
        }
    }

    private void Emit(List<Unit> units, int? page, List<TextChunk> chunks)
    {
        var content = string.Join("\n\n", units.Select(u => u.Text));
        // Label the chunk with the most recent section heading it covers.
        chunks.Add(new TextChunk(content, tokens.Count(content), page, units.LastOrDefault(u => u.Heading is not null)?.Heading));
    }

    /// <summary>Trailing units of the previous chunk, up to the overlap budget.</summary>
    private List<Unit> TakeOverlap(List<Unit> previous)
    {
        var overlap = new List<Unit>();
        var total = 0;
        for (var i = previous.Count - 1; i >= 0; i--)
        {
            if (total + previous[i].Tokens > _overlapTokens)
            {
                break;
            }

            overlap.Insert(0, previous[i]);
            total += previous[i].Tokens;
        }

        return overlap;
    }

    private IEnumerable<string> SplitToFit(string paragraph)
    {
        if (tokens.Count(paragraph) <= maxTokens)
        {
            yield return paragraph;
            yield break;
        }

        foreach (var sentence in SentenceBoundary().Split(paragraph).Where(s => s.Length > 0))
        {
            if (tokens.Count(sentence) <= maxTokens)
            {
                yield return sentence;
                continue;
            }

            foreach (var window in SplitWords(sentence))
            {
                yield return window;
            }
        }
    }

    /// <summary>Last resort for a giant "sentence" (tables, code, minified text): fixed word windows.</summary>
    private IEnumerable<string> SplitWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var window = new List<string>();
        var windowTokens = 0;
        foreach (var word in words)
        {
            var wordTokens = tokens.Count(word) + 1;
            if (windowTokens + wordTokens > maxTokens && window.Count > 0)
            {
                yield return string.Join(' ', window);
                window.Clear();
                windowTokens = 0;
            }

            window.Add(word);
            windowTokens += wordTokens;
        }

        if (window.Count > 0)
        {
            yield return string.Join(' ', window);
        }
    }

    [GeneratedRegex(@"^#{1,6}\s+(?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=\p{Lu}|\d|[""'(\[])")]
    private static partial Regex SentenceBoundary();
}
