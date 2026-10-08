using System.Text;
using AISupportOps.Application.Ingestion;
using AISupportOps.Domain.Documents;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace AISupportOps.Infrastructure.Ingestion;

/// <summary>UTF-8 text and Markdown are already text; just decode.</summary>
internal sealed class PlainTextExtractor(DocumentKind kind) : ITextExtractor
{
    public DocumentKind Kind => kind;

    public bool EmitsMarkdownHeadings => kind == DocumentKind.Markdown;

    public async Task<IReadOnlyList<ExtractedSection>> ExtractAsync(Stream content, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return [new ExtractedSection(await reader.ReadToEndAsync(ct), PageNumber: null)];
    }
}

/// <summary>
/// PDF text via PdfPig, one section per page so chunks can cite page numbers. The content-order
/// extractor reconstructs reading order and word spacing better than raw glyph order.
/// Image-only (scanned) PDFs yield no text; OCR is out of scope for now.
/// </summary>
internal sealed class PdfTextExtractor : ITextExtractor
{
    public DocumentKind Kind => DocumentKind.Pdf;

    public bool EmitsMarkdownHeadings => false;

    public async Task<IReadOnlyList<ExtractedSection>> ExtractAsync(Stream content, CancellationToken ct)
    {
        var bytes = await ExtractorStreams.ReadAllAsync(content, ct);
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var sections = new List<ExtractedSection>(pdf.NumberOfPages);
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sections.Add(new ExtractedSection(ContentOrderTextExtractor.GetText(page), page.Number));
            }

            return sections;
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentExtractionException("The PDF is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DocumentExtractionException)
        {
            throw new DocumentExtractionException("The PDF is corrupt or uses unsupported features.", ex);
        }
    }
}

/// <summary>
/// DOCX via the OpenXML SDK. Heading/Title paragraph styles become Markdown headings so the
/// chunker can label chunks with their section. Character budget guards against ZIP bombs.
/// </summary>
internal sealed class DocxTextExtractor(IOptions<IngestionOptions> options) : ITextExtractor
{
    public DocumentKind Kind => DocumentKind.Docx;

    public bool EmitsMarkdownHeadings => true;

    public async Task<IReadOnlyList<ExtractedSection>> ExtractAsync(Stream content, CancellationToken ct)
    {
        var bytes = await ExtractorStreams.ReadAllAsync(content, ct);
        var budget = options.Value.MaxExtractedCharacters;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var docx = WordprocessingDocument.Open(stream, isEditable: false);
            var body = docx.MainDocumentPart?.Document?.Body
                ?? throw new DocumentExtractionException("The DOCX file has no document body.");

            var builder = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                ct.ThrowIfCancellationRequested();
                var text = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text)).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var isHeading = style is not null &&
                    (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.Equals("Title", StringComparison.OrdinalIgnoreCase));

                builder.Append(isHeading ? "## " : string.Empty).Append(text).Append("\n\n");
                if (builder.Length > budget)
                {
                    throw new DocumentExtractionException("The document contains more text than the configured limit.");
                }
            }

            return [new ExtractedSection(builder.ToString(), PageNumber: null)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DocumentExtractionException)
        {
            throw new DocumentExtractionException("The DOCX file is corrupt or not a Word document.", ex);
        }
    }
}

internal static class ExtractorStreams
{
    /// <summary>PDF and ZIP parsers need random access; blob streams may not be seekable. Size is already capped at upload.</summary>
    public static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
