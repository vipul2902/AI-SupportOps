using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Documents;
using AISupportOps.Domain.Tenants;
using AISupportOps.IntegrationTests.Fixtures;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DocumentTests(ApiFactory factory)
{
    [Fact]
    public async Task Upload_stores_metadata_and_content_round_trips()
    {
        var owner = await NewTenantAsync();
        using var client = factory.CreateClient(owner);
        var text = $"How to reset your password: open Settings > Security. {Guid.NewGuid()}";

        var created = await (await UploadAsync(client, "reset-guide.md", text)).ReadAsync<DocumentResponse>(HttpStatusCode.Created);

        Assert.Equal("reset-guide.md", created.FileName);
        Assert.Equal(DocumentKind.Markdown, created.Kind);
        Assert.Equal(DocumentStatus.Uploaded, created.Status);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), created.SizeBytes);

        var download = await client.GetAsync(new Uri($"/api/documents/{created.Id}/content", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(text, await download.Content.ReadAsStringAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task List_is_paged_and_scoped_to_tenant()
    {
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        using var aClient = factory.CreateClient(a);
        using var bClient = factory.CreateClient(b);
        for (var i = 0; i < 3; i++)
        {
            await UploadAsync(aClient, $"doc-{i}.txt", $"content {i} {Guid.NewGuid()}");
        }

        var aPage = await (await aClient.GetAsync(new Uri("/api/documents?page=1&pageSize=2", UriKind.Relative)))
            .ReadAsync<PagedResponse<DocumentResponse>>(HttpStatusCode.OK);
        var bList = await (await bClient.GetAsync(new Uri("/api/documents", UriKind.Relative)))
            .ReadAsync<PagedResponse<DocumentResponse>>(HttpStatusCode.OK);

        Assert.Equal(3, aPage.TotalCount);
        Assert.Equal(2, aPage.Items.Count);
        Assert.Equal(0, bList.TotalCount);
    }

    [Fact]
    public async Task Other_tenant_cannot_read_download_or_delete()
    {
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        using var aClient = factory.CreateClient(a);
        var doc = await (await UploadAsync(aClient, "secret.txt", $"Tenant A confidential {Guid.NewGuid()}"))
            .ReadAsync<DocumentResponse>(HttpStatusCode.Created);
        using var bClient = factory.CreateClient(b);

        Assert.Equal(HttpStatusCode.NotFound, (await bClient.GetAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bClient.GetAsync(new Uri($"/api/documents/{doc.Id}/content", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bClient.DeleteAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_upload_and_agent_cannot_delete()
    {
        var owner = await NewTenantAsync();
        using var ownerClient = factory.CreateClient(owner);
        var doc = await (await UploadAsync(ownerClient, "a.txt", $"x {Guid.NewGuid()}")).ReadAsync<DocumentResponse>(HttpStatusCode.Created);
        var viewer = await factory.AddMemberAsync(owner, TenantRole.Viewer);
        var agent = await factory.AddMemberAsync(owner, TenantRole.Agent);
        using var viewerClient = factory.CreateClient(viewer);
        using var agentClient = factory.CreateClient(agent);

        Assert.Equal(HttpStatusCode.OK, (await viewerClient.GetAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(viewerClient, "b.txt", "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(agentClient, "c.txt", $"agent upload {Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.DeleteAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Disallowed_or_disguised_files_are_rejected_with_415()
    {
        using var client = factory.CreateClient(await NewTenantAsync());

        var exe = await UploadAsync(client, "tool.exe", "MZ fake exe");
        var disguised = await UploadAsync(client, "invoice.pdf", "MZ\u0090\u0000 not really a pdf");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, exe.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, disguised.StatusCode);
    }

    [Fact]
    public async Task File_over_size_limit_is_rejected_with_413_and_not_stored()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var tooBig = new string('a', (int)ApiFactory.MaxUploadBytes + 1);

        var response = await UploadAsync(client, "big.txt", tooBig);
        var list = await (await client.GetAsync(new Uri("/api/documents", UriKind.Relative))).ReadAsync<PagedResponse<DocumentResponse>>(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, list.TotalCount);
        Assert.DoesNotContain(Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories),
            f => f.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_content_in_same_tenant_returns_409_but_other_tenant_may_upload_it()
    {
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        using var aClient = factory.CreateClient(a);
        using var bClient = factory.CreateClient(b);
        var content = $"shared content {Guid.NewGuid()}";

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(aClient, "one.txt", content)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(aClient, "renamed.txt", content)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(bClient, "one.txt", content)).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_metadata_and_stored_file()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var doc = await (await UploadAsync(client, "temp.txt", $"temporary {Guid.NewGuid()}")).ReadAsync<DocumentResponse>(HttpStatusCode.Created);
        var filesBefore = Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Count();

        var delete = await client.DeleteAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/documents/{doc.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(filesBefore - 1, Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Count());
    }

    private async Task<Application.Identity.AuthResponse> NewTenantAsync()
    {
        using var client = factory.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); // ignored by the server
        form.Add(file, "file", fileName);
        return await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form);
    }
}
