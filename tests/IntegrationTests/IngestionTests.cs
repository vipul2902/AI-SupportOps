using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Ingestion;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Ingestion;
using AISupportOps.IntegrationTests.Fixtures;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AISupportOps.IntegrationTests;

/// <summary>End-to-end: upload → background worker → chunks, using real PDF/DOCX files.</summary>
[Collection(ApiCollection.Name)]
public class IngestionTests(ApiFactory factory)
{
    [Fact]
    public async Task Markdown_is_processed_into_chunks_with_headings()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var markdown = $"# Account security\n\nTo reset your password open Settings > Security. Ref {Guid.NewGuid()}\n\n## Two-factor\n\nEnable 2FA from the same page.";

        var doc = await UploadAndWaitAsync(client, "security.md", Encoding.UTF8.GetBytes(markdown));

        Assert.Equal(DocumentStatus.Processed, doc.Status);
        Assert.True(doc.ChunkCount >= 1);
        Assert.NotNull(doc.ProcessedAt);
        var chunks = await ChunksAsync(client, doc.Id);
        Assert.Contains(chunks, c => c.Content.Contains("reset your password", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Heading is "Account security" or "Two-factor");
    }

    [Fact]
    public async Task Pdf_text_is_extracted_per_page()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var pdf = BuildPdf($"Refund policy page one {Guid.NewGuid():N}", "Shipping takes three business days");

        var doc = await UploadAndWaitAsync(client, "policy.pdf", pdf);

        Assert.Equal(DocumentStatus.Processed, doc.Status);
        var chunks = await ChunksAsync(client, doc.Id);
        Assert.Contains(chunks, c => c.PageNumber == 1 && c.Content.Contains("Refund policy", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.PageNumber == 2 && c.Content.Contains("three business days", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Docx_text_is_extracted_with_heading_styles()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var docx = BuildDocx(("Heading1", "Billing"), (null, $"Invoices are issued monthly. {Guid.NewGuid()}"));

        var doc = await UploadAndWaitAsync(client, "billing.docx", docx);

        Assert.Equal(DocumentStatus.Processed, doc.Status);
        var chunk = Assert.Single(await ChunksAsync(client, doc.Id));
        Assert.Equal("Billing", chunk.Heading);
        Assert.Contains("Invoices are issued monthly.", chunk.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_without_text_fails_with_actionable_error()
    {
        using var client = factory.CreateClient(await NewTenantAsync());

        var doc = await UploadAndWaitAsync(client, "scan.pdf", BuildPdf());

        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Equal(DocumentIngestionService.NoTextError, doc.Error);
    }

    [Fact]
    public async Task Corrupt_pdf_fails_without_leaking_internals()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var corrupt = Encoding.ASCII.GetBytes($"%PDF-1.7\nthis is not a real pdf {Guid.NewGuid()}");

        var doc = await UploadAndWaitAsync(client, "broken.pdf", corrupt);

        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Equal("The PDF is corrupt or uses unsupported features.", doc.Error);
    }

    [Fact]
    public async Task Reprocess_requeues_and_reindexes_without_duplicating_chunks()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var doc = await UploadAndWaitAsync(client, "faq.txt", Encoding.UTF8.GetBytes($"FAQ answer. {Guid.NewGuid()}"));
        var before = await ChunksAsync(client, doc.Id);

        var requeue = await client.PostAsync(new Uri($"/api/documents/{doc.Id}/reprocess", UriKind.Relative), null);
        var again = await WaitForTerminalStatusAsync(client, doc.Id);
        var after = await ChunksAsync(client, doc.Id);

        Assert.Equal(HttpStatusCode.Accepted, requeue.StatusCode);
        Assert.Equal(DocumentStatus.Processed, again.Status);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(c => c.Content), after.Select(c => c.Content));
    }

    [Fact]
    public async Task Chunks_of_another_tenants_document_are_not_accessible()
    {
        using var aClient = factory.CreateClient(await NewTenantAsync());
        using var bClient = factory.CreateClient(await NewTenantAsync());
        var doc = await UploadAndWaitAsync(aClient, "private.txt", Encoding.UTF8.GetBytes($"Tenant A only. {Guid.NewGuid()}"));

        var response = await bClient.GetAsync(new Uri($"/api/documents/{doc.Id}/chunks", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_workers_never_process_the_same_document_twice()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var uploads = new List<DocumentResponse>();
        for (var i = 0; i < 8; i++)
        {
            var response = await UploadAsync(client, $"doc-{i}.txt", Encoding.UTF8.GetBytes($"Document {i}. {Guid.NewGuid()}"));
            uploads.Add(await response.ReadAsync<DocumentResponse>(HttpStatusCode.Created));
        }

        // Extra workers compete with the hosted one for the same queue.
        var workers = Enumerable.Range(0, 4).Select(_ => new DocumentIngestionWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptions<IngestionOptions>>(),
            TimeProvider.System,
            NullLogger<DocumentIngestionWorker>.Instance)).ToList();
        await Task.WhenAll(workers.Select(async w =>
        {
            while (await w.ProcessNextAsync(CancellationToken.None))
            {
            }
        }));

        foreach (var upload in uploads)
        {
            var doc = await WaitForTerminalStatusAsync(client, upload.Id);
            Assert.Equal(DocumentStatus.Processed, doc.Status);
            Assert.Single(await ChunksAsync(client, upload.Id));
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var ids = uploads.Select(u => u.Id).ToList();
        var attempts = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.Documents)
            .Where(d => ids.Contains(d.Id)).Select(d => d.ProcessingAttempts).ToList();
        Assert.All(attempts, a => Assert.Equal(1, a));
    }

    // ---- helpers ----

    private async Task<AuthResponse> NewTenantAsync()
    {
        using var client = factory.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task<DocumentResponse> UploadAndWaitAsync(HttpClient client, string fileName, byte[] content)
    {
        var created = await (await UploadAsync(client, fileName, content)).ReadAsync<DocumentResponse>(HttpStatusCode.Created);
        return await WaitForTerminalStatusAsync(client, created.Id);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form);
    }

    private static async Task<DocumentResponse> WaitForTerminalStatusAsync(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var doc = await (await client.GetAsync(new Uri($"/api/documents/{id}", UriKind.Relative))).ReadAsync<DocumentResponse>(HttpStatusCode.OK);
            if (doc.Status is DocumentStatus.Processed or DocumentStatus.Failed || DateTime.UtcNow > deadline)
            {
                return doc;
            }

            await Task.Delay(100);
        }
    }

    private static async Task<List<DocumentChunkResponse>> ChunksAsync(HttpClient client, Guid id) =>
        (await (await client.GetAsync(new Uri($"/api/documents/{id}/chunks?pageSize=100", UriKind.Relative)))
            .ReadAsync<PagedResponse<DocumentChunkResponse>>(HttpStatusCode.OK)).Items.ToList();

    /// <summary>Builds a real PDF with one page per string (no strings = one blank page).</summary>
    private static byte[] BuildPdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages.DefaultIfEmpty(null))
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (text is not null)
            {
                page.AddText(text, 12, new PdfPoint(40, 750), font);
            }
        }

        return builder.Build();
    }

    private static byte[] BuildDocx(params (string? Style, string Text)[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var docx = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = docx.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body(paragraphs.Select(p =>
            {
                var paragraph = new Paragraph(new Run(new Text(p.Text)));
                if (p.Style is not null)
                {
                    paragraph.PrependChild(new ParagraphProperties(new ParagraphStyleId { Val = p.Style }));
                }

                return paragraph;
            })));
        }

        return stream.ToArray();
    }
}
