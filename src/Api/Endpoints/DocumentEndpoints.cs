using AISupportOps.Api.Auth;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Documents;

namespace AISupportOps.Api.Endpoints;

internal static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        // JWT bearer auth (no cookies) means CSRF does not apply, so antiforgery is disabled.
        group.MapPost("/", async (IFormFile file, DocumentService documents, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                var created = await documents.UploadAsync(stream, file.FileName, ct);
                return Results.Created($"/api/documents/{created.Id}", created);
            })
            .DisableAntiforgery()
            .RequireAuthorization(Policies.Agent);

        group.MapGet("/", (DocumentService documents, CancellationToken ct, DocumentStatus? status, int page = 1, int pageSize = 20) =>
                documents.ListAsync(status, page, pageSize, ct))
            .RequireAuthorization(Policies.Viewer);

        group.MapGet("/{id:guid}", (Guid id, DocumentService documents, CancellationToken ct) =>
                documents.GetAsync(id, ct))
            .RequireAuthorization(Policies.Viewer);

        group.MapGet("/{id:guid}/content", async (Guid id, DocumentService documents, HttpContext http, CancellationToken ct) =>
            {
                var content = await documents.OpenContentAsync(id, ct);
                // Always download, never render inline: an uploaded file must not execute in our origin.
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(content.Content, content.ContentType, content.FileName);
            })
            .RequireAuthorization(Policies.Viewer);

        group.MapDelete("/{id:guid}", async (Guid id, DocumentService documents, CancellationToken ct) =>
            {
                await documents.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin);

        return app;
    }
}
