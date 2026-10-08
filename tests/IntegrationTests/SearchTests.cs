using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Ai;
using AISupportOps.Infrastructure.Persistence;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SearchTests(ApiFactory factory)
{
    private const string PasswordDoc = "# Account security\n\nTo reset your password, open Settings, choose Security, then click Reset password. A reset link is emailed to you.";
    private const string ShippingDoc = "# Shipping\n\nOrders ship within two business days. International delivery takes five to ten days.";

    [Fact]
    public async Task Search_ranks_the_relevant_chunk_first_with_citation_metadata()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var password = await UploadAndWaitAsync(client, "security.md", PasswordDoc);
        await UploadAndWaitAsync(client, "shipping.md", ShippingDoc);

        var response = await SearchAsync(client, new SearchRequest("How do I reset my password?"));

        Assert.NotEmpty(response.Results);
        var top = response.Results[0];
        Assert.Equal(password.Id, top.DocumentId);
        Assert.Equal("security.md", top.FileName);
        Assert.Equal("Account security", top.Heading);
        Assert.Contains("Reset password", top.Content, StringComparison.Ordinal);
        Assert.True(response.Results.Zip(response.Results.Skip(1)).All(p => p.First.Score >= p.Second.Score), "results must be sorted by score");
    }

    [Fact]
    public async Task Search_never_returns_another_tenants_chunks()
    {
        using var aClient = factory.CreateClient(await NewTenantAsync());
        using var bClient = factory.CreateClient(await NewTenantAsync());
        var secret = $"Tenant A internal escalation code {Guid.NewGuid():N} for password reset.";
        await UploadAndWaitAsync(aClient, "internal.md", secret);

        // Identical query, from tenant B: must see nothing, even though the text matches perfectly.
        var response = await SearchAsync(bClient, new SearchRequest(secret, TopK: 20, MinScore: -1));

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Search_can_be_restricted_to_specific_documents()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        await UploadAndWaitAsync(client, "security.md", PasswordDoc);
        var shipping = await UploadAndWaitAsync(client, "shipping.md", ShippingDoc);

        var response = await SearchAsync(client, new SearchRequest("reset password", DocumentIds: [shipping.Id], MinScore: -1));

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r => Assert.Equal(shipping.Id, r.DocumentId));
    }

    [Fact]
    public async Task Deleted_documents_disappear_from_search()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var doc = await UploadAndWaitAsync(client, "temp.md", $"Temporary policy {Guid.NewGuid():N} about refunds.");

        await client.DeleteAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative));
        var response = await SearchAsync(client, new SearchRequest("Temporary policy about refunds", MinScore: -1));

        Assert.DoesNotContain(response.Results, r => r.DocumentId == doc.Id);
    }

    [Fact]
    public async Task Empty_knowledge_base_returns_empty_results_not_an_error()
    {
        using var client = factory.CreateClient(await NewTenantAsync());

        var response = await SearchAsync(client, new SearchRequest("anything at all"));

        Assert.Empty(response.Results);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("valid query", 0)]
    [InlineData("valid query", 21)]
    public async Task Invalid_search_requests_return_400(string query, int topK)
    {
        using var client = factory.CreateClient(await NewTenantAsync());

        var response = await client.PostAsJsonAsync("/api/search", new SearchRequest(query, topK), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_requires_authentication()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/search", new SearchRequest("password"), Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Processed_chunks_store_embeddings_with_model_id()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var doc = await UploadAndWaitAsync(client, "faq.md", $"FAQ entry {Guid.NewGuid():N}.");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chunks = await db.DocumentChunks.IgnoreQueryFilters().Where(c => c.DocumentId == doc.Id).ToListAsync();

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.Equal(DocumentChunk.EmbeddingDimensions, c.Embedding!.Length);
            Assert.Equal(FakeEmbeddingGenerator.ModelId, c.EmbeddingModel);
        });
    }

    /// <summary>
    /// Proves the HNSW index serves cosine-distance ordering. With a selective tenant filter on a small
    /// tenant, the planner may instead pick the tenant B-tree and sort exactly — cheaper and 100% accurate.
    /// Correctness of tenant filtering under either plan is covered by the isolation test above.
    /// </summary>
    [Fact]
    public async Task Hnsw_index_serves_cosine_distance_ordering()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var probe = new Pgvector.Vector(Enumerable.Repeat(0.01f, DocumentChunk.EmbeddingDimensions).ToArray());

        await using var transaction = await db.Database.BeginTransactionAsync();
        // Tiny test tables make a sequential scan cheaper; disable it to prove the index is *usable*.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
        var plan = await db.Database.SqlQuery<string>(
            $"EXPLAIN SELECT id FROM document_chunks ORDER BY embedding <=> {probe} LIMIT 5").ToListAsync();

        Assert.Contains(plan, line => line.Contains("ix_document_chunks_embedding", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private async Task<AuthResponse> NewTenantAsync()
    {
        using var client = factory.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task<SearchResponse> SearchAsync(HttpClient client, SearchRequest request) =>
        await (await client.PostAsJsonAsync("/api/search", request, Json)).ReadAsync<SearchResponse>(HttpStatusCode.OK);

    private static async Task<DocumentResponse> UploadAndWaitAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var created = await (await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form)).ReadAsync<DocumentResponse>(HttpStatusCode.Created);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var doc = await (await client.GetAsync(new Uri($"/api/documents/{created.Id}", UriKind.Relative))).ReadAsync<DocumentResponse>(HttpStatusCode.OK);
            if (doc.Status is DocumentStatus.Processed or DocumentStatus.Failed || DateTime.UtcNow > deadline)
            {
                Assert.Equal(DocumentStatus.Processed, doc.Status);
                return doc;
            }

            await Task.Delay(100);
        }
    }
}
