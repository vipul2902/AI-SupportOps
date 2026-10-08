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
        var system = list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var text = system.Contains("standalone question", StringComparison.OrdinalIgnoreCase)
            ? Rewrite(lastUser)
            : Answer(lastUser);
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
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word) { ModelId = ModelId };
        }

        // Real providers report usage in a final update; mimic that.
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)]) { ModelId = ModelId };
    }

    /// <summary>
    /// Fake follow-up rewriting: prepend the earlier user turns to the follow-up, so retrieval
    /// sees the topic words the follow-up omits ("and on mobile?" → "...reset password... and on mobile?").
    /// </summary>
    public static string Rewrite(string rewriteRequest)
    {
        var userTurns = UserTurn().Matches(rewriteRequest).Select(m => m.Groups["text"].Value.Trim());
        var followUp = FollowUp().Match(rewriteRequest) is { Success: true } f ? f.Groups["text"].Value.Trim() : rewriteRequest;
        return string.Join(' ', userTurns.Append(followUp));
    }

    [GeneratedRegex(@"^User: (?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex UserTurn();

    [GeneratedRegex(@"<follow_up>\s*(?<text>.*?)\s*</follow_up>", RegexOptions.Singleline)]
    private static partial Regex FollowUp();

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
