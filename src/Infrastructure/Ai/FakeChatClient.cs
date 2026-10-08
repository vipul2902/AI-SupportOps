using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AISupportOps.Application.Knowledge;
using Microsoft.Extensions.AI;

namespace AISupportOps.Infrastructure.Ai;

/// <summary>
/// Deterministic stand-in for a chat model (tests, offline development). It behaves like a
/// well-instructed RAG model in the simplest possible way: it answers with the first sentence of
/// source [1] and cites it, or declines when the prompt contains no sources. It does not reason.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    public const string ModelId = "fake-chat-v1";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var text = Answer(list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty);
        var inputChars = list.Sum(m => m.Text.Length);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = ModelId,
            FinishReason = ChatFinishReason.Stop,
            // Rough 4-characters-per-token estimate, so usage tracking has non-zero data offline.
            Usage = new UsageDetails { InputTokenCount = inputChars / 4, OutputTokenCount = text.Length / 4 },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var word in Regex.Split(response.Text, @"(?<=\s)"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word) { ModelId = ModelId };
        }
    }

    public static string Answer(string userMessage)
    {
        var first = FirstSource().Match(userMessage);
        if (!first.Success)
        {
            return RagService.NoAnswerMessage;
        }

        var content = first.Groups["content"].Value.Trim();
        var sentence = FirstSentence().Match(content) is { Success: true } m ? m.Groups["s"].Value.Trim() : content;
        return $"According to the documentation: {sentence} [{first.Groups["id"].Value}]";
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"<source id=""(?<id>\d+)""[^>]*>\n(?<content>.*?)\n</source>", RegexOptions.Singleline)]
    private static partial Regex FirstSource();

    // Skip a leading Markdown heading line, then take up to the first sentence end.
    [GeneratedRegex(@"(?:^#.*\n+)?(?<s>[^\n]*?[.!?])(?=\s|$)")]
    private static partial Regex FirstSentence();
}
