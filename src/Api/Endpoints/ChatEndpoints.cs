using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using AISupportOps.Api.Auth;
using AISupportOps.Application.Chat;

namespace AISupportOps.Api.Endpoints;

internal static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // Streams the assistant's answer as Server-Sent Events:
        //   event: meta   data: { conversationId, userMessageId, conversationTitle }
        //   event: delta  data: { text }                      (many)
        //   event: done   data: { messageId, outcome, citations, usage, ... }
        //   event: error  data: { message, messageId }        (instead of done)
        app.MapPost("/api/chat", async (ChatRequest request, ChatService chat, CancellationToken ct) =>
            {
                // Validation and lookups happen here, while a 4xx status can still be returned.
                var turn = await chat.BeginTurnAsync(request, ct);
                return TypedResults.ServerSentEvents(ToSse(chat.StreamTurnAsync(turn, ct), ct));
            })
            .WithTags("Chat")
            .RequireAuthorization(Policies.Viewer)
            .RequireRedisRateLimit(RateLimitPolicies.Ai);

        var conversations = app.MapGroup("/api/conversations")
            .WithTags("Chat")
            .RequireAuthorization(Policies.Viewer);

        conversations.MapGet("/", (ConversationService svc, CancellationToken ct, int page = 1, int pageSize = 30) =>
            svc.ListAsync(page, pageSize, ct));

        conversations.MapGet("/{id:guid}", (Guid id, ConversationService svc, CancellationToken ct) =>
            svc.GetAsync(id, ct));

        conversations.MapPatch("/{id:guid}", (Guid id, RenameConversationRequest request, ConversationService svc, CancellationToken ct) =>
            svc.RenameAsync(id, request, ct));

        conversations.MapDelete("/{id:guid}", async (Guid id, ConversationService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        return app;
    }

    // Declared as object so System.Text.Json serializes each event's runtime type.
    private static async IAsyncEnumerable<SseItem<object>> ToSse(
        IAsyncEnumerable<ChatStreamEvent> events, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in events.WithCancellation(ct))
        {
            yield return new SseItem<object>(e, e.EventType);
        }
    }
}
