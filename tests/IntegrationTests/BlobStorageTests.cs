using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Storage;
using AISupportOps.IntegrationTests.Fixtures;
using Azure.Storage.Blobs;
using Testcontainers.Azurite;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

/// <summary>Azure Blob storage against Azurite (the official local Azure Storage emulator).</summary>
[Collection(ApiCollection.Name)]
public sealed class BlobStorageTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck") // Azurite may lag the newest SDK's REST API version
        .Build();

    private BlobContainerClient _container = null!;

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();
        _container = new BlobServiceClient(_azurite.GetConnectionString()).GetBlobContainerClient($"docs-{Guid.NewGuid():N}");
        await _container.CreateAsync();
    }

    public Task DisposeAsync() => _azurite.DisposeAsync().AsTask();

    [Fact]
    public async Task Saves_reads_and_deletes_blobs()
    {
        var storage = new AzureBlobFileStorage(_container);
        var key = $"{Guid.NewGuid():N}/{Guid.NewGuid():N}";

        await storage.SaveAsync(key, new MemoryStream("hello blob"u8.ToArray()), CancellationToken.None);
        await using (var read = await storage.OpenReadAsync(key, CancellationToken.None))
        {
            Assert.Equal("hello blob", await new StreamReader(read).ReadToEndAsync());
        }

        await storage.DeleteAsync(key, CancellationToken.None);
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(key, CancellationToken.None));
        await storage.DeleteAsync(key, CancellationToken.None); // idempotent
    }

    [Fact]
    public async Task Never_overwrites_an_existing_blob()
    {
        var storage = new AzureBlobFileStorage(_container);
        var key = $"t/{Guid.NewGuid():N}";
        await storage.SaveAsync(key, new MemoryStream("original"u8.ToArray()), CancellationToken.None);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() =>
            storage.SaveAsync(key, new MemoryStream("replacement"u8.ToArray()), CancellationToken.None));

        await using var read = await storage.OpenReadAsync(key, CancellationToken.None);
        Assert.Equal("original", await new StreamReader(read).ReadToEndAsync());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("a\\b")]
    [InlineData(" ")]
    public async Task Rejects_unsafe_keys(string key)
    {
        var storage = new AzureBlobFileStorage(_container);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(key, new MemoryStream([1]), CancellationToken.None));
    }

    [Fact]
    public async Task Full_upload_and_ingestion_flow_works_on_blob_storage()
    {
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Storage:Provider", "AzureBlob");
            b.UseSetting("Storage:AzureBlob:ConnectionString", _azurite.GetConnectionString());
            b.UseSetting("Storage:AzureBlob:ContainerName", _container.Name);
        });
        using var anonymous = app.CreateClient();
        using var client = app.CreateClient(await anonymous.RegisterAsync());
        var text = $"# Billing\n\nInvoices are issued on the first day of each month. {Guid.NewGuid()}";

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", "billing.md");
        var created = await (await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form)).ReadAsync<DocumentResponse>(HttpStatusCode.Created);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        DocumentResponse doc;
        do
        {
            await Task.Delay(100);
            doc = await (await client.GetAsync(new Uri($"/api/documents/{created.Id}", UriKind.Relative))).ReadAsync<DocumentResponse>(HttpStatusCode.OK);
        }
        while (doc.Status is DocumentStatus.Uploaded or DocumentStatus.Processing && DateTime.UtcNow < deadline);

        Assert.Equal(DocumentStatus.Processed, doc.Status); // the worker read the file back from Blob storage
        Assert.Equal(text, await client.GetStringAsync(new Uri($"/api/documents/{created.Id}/content", UriKind.Relative)));
        Assert.Equal(1, await _container.GetBlobsAsync().CountAsync());
    }
}
