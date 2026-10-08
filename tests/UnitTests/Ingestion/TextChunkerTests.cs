using AISupportOps.Application.Ingestion;

namespace AISupportOps.UnitTests.Ingestion;

public class TextChunkerTests
{
    /// <summary>Deterministic stand-in for a real tokenizer: one token per whitespace-separated word.</summary>
    private sealed class WordCounter : ITokenCounter
    {
        public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static readonly WordCounter Words = new();

    [Fact]
    public void Small_document_becomes_a_single_chunk()
    {
        var chunks = new TextChunker(Words, maxTokens: 50, overlapTokens: 5)
            .Chunk([new ExtractedSection("Hello world.\n\nSecond paragraph.", null)], markdownHeadings: false);

        var chunk = Assert.Single(chunks);
        Assert.Equal("Hello world.\n\nSecond paragraph.", chunk.Content);
        Assert.Equal(4, chunk.TokenCount);
    }

    [Fact]
    public void No_chunk_exceeds_the_token_budget_and_all_text_is_covered()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i => $"Paragraph {i} has exactly seven words here.");
        var text = string.Join("\n\n", paragraphs);

        var chunks = new TextChunker(Words, maxTokens: 40, overlapTokens: 10)
            .Chunk([new ExtractedSection(text, null)], markdownHeadings: false);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 40));
        for (var i = 1; i <= 30; i++)
        {
            Assert.Contains(chunks, c => c.Content.Contains($"Paragraph {i} ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Consecutive_chunks_overlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"Sentence number {i} is here."));

        var chunks = new TextChunker(Words, maxTokens: 15, overlapTokens: 5)
            .Chunk([new ExtractedSection(text, null)], markdownHeadings: false);

        for (var i = 1; i < chunks.Count; i++)
        {
            var lastParagraphOfPrevious = chunks[i - 1].Content.Split("\n\n")[^1];
            Assert.StartsWith(lastParagraphOfPrevious, chunks[i].Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Oversized_paragraph_is_split_by_sentences_then_words()
    {
        var longSentence = string.Join(' ', Enumerable.Repeat("word", 45)) + ".";
        var text = $"Short intro sentence. {longSentence} Closing sentence here.";

        var chunks = new TextChunker(Words, maxTokens: 20, overlapTokens: 0)
            .Chunk([new ExtractedSection(text, null)], markdownHeadings: false);

        Assert.All(chunks, c => Assert.True(c.TokenCount <= 20, $"{c.TokenCount} tokens: {c.Content}"));
        Assert.Equal(45 + 6, chunks.Sum(c => c.TokenCount));
    }

    [Fact]
    public void Chunks_keep_page_numbers_and_never_span_pages()
    {
        var chunks = new TextChunker(Words, maxTokens: 100, overlapTokens: 10).Chunk(
            [new ExtractedSection("Page one text.", 1), new ExtractedSection("Page two text.", 2)],
            markdownHeadings: false);

        Assert.Collection(chunks,
            c => Assert.Equal((1, "Page one text."), (c.PageNumber!.Value, c.Content)),
            c => Assert.Equal((2, "Page two text."), (c.PageNumber!.Value, c.Content)));
    }

    [Fact]
    public void Markdown_headings_label_chunks()
    {
        const string text = "# Billing\n\nInvoices are monthly.\n\n## Refunds\n\nRefunds take five days.";

        var chunks = new TextChunker(Words, maxTokens: 6, overlapTokens: 0)
            .Chunk([new ExtractedSection(text, null)], markdownHeadings: true);

        Assert.Contains(chunks, c => c.Heading == "Billing" && c.Content.Contains("Invoices", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Heading == "Refunds" && c.Content.Contains("five days", StringComparison.Ordinal));
    }

    [Fact]
    public void A_heading_without_body_merges_into_the_next_section_instead_of_becoming_a_chunk()
    {
        const string text = "# Billing and invoices\n\n## Invoices\n\nInvoices are monthly.\n\n## Refunds\n\nRefunds take five days.";

        var chunks = new TextChunker(Words, maxTokens: 500, overlapTokens: 50)
            .Chunk([new ExtractedSection(text, null)], markdownHeadings: true);

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("# Billing and invoices\n\n## Invoices", chunks[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, c => c.Content.Split("\n\n").All(p => p.StartsWith('#')));
    }

    [Fact]
    public void Markdown_sections_start_new_chunks_even_when_they_would_fit_together()
    {
        const string text = "# Billing\n\nInvoices are monthly.\n\n## Refunds\n\nRefunds take five days.";

        var chunks = new TextChunker(Words, maxTokens: 500, overlapTokens: 50)
            .Chunk([new ExtractedSection(text, null)], markdownHeadings: true);

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain("Refunds", chunks[0].Content, StringComparison.Ordinal);
        Assert.Equal(("Billing", "Refunds"), (chunks[0].Heading, chunks[1].Heading));
    }
}
