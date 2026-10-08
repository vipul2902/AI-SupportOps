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
        if (options?.Tools is { Count: > 0 } tools)
        {
            return Task.FromResult(AgentTurn(list, tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal)));
        }

        var system = list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var text = system.Contains("standalone question", StringComparison.OrdinalIgnoreCase) ? Rewrite(lastUser)
            : system.Contains("running summary", StringComparison.OrdinalIgnoreCase) ? Summarize(lastUser)
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

    /// <summary>
    /// Fake agent policy, for offline development only: one tool per user message, chosen by keywords,
    /// and only among the tools actually offered (so role-based tool filtering is visible offline too).
    /// After a tool result arrives, it reports the result.
    /// </summary>
    private static ChatResponse AgentTurn(List<ChatMessage> messages, HashSet<string> offered)
    {
        var last = messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            var result = last.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.Result?.ToString() ?? string.Empty;
            return Reply($"Done. Tool result: {(result.Length > 400 ? result[..400] + "…" : result)}");
        }

        var text = last.Text;
        var lower = text.ToUpperInvariant();
        var ticketNumber = TicketRef().Match(text) is { Success: true } t ? int.Parse(t.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        var email = EmailRef().Match(text) is { Success: true } e ? e.Value : null;

        (string Tool, Dictionary<string, object?> Args)? call =
            lower.Contains("CREATE", StringComparison.Ordinal) && lower.Contains("TICKET", StringComparison.Ordinal)
                ? ("create_support_ticket", new() { ["title"] = text.Length > 80 ? text[..80] : text, ["description"] = text, ["priority"] = lower.Contains("URGENT", StringComparison.Ordinal) ? "high" : "medium", ["customer_email"] = email })
            : ticketNumber is { } n && (lower.Contains("RESOLVE", StringComparison.Ordinal) || lower.Contains("CLOSE", StringComparison.Ordinal) || lower.Contains("PROGRESS", StringComparison.Ordinal))
                ? ("update_support_ticket", new() { ["ticket_number"] = n, ["status"] = lower.Contains("RESOLVE", StringComparison.Ordinal) ? "resolved" : lower.Contains("CLOSE", StringComparison.Ordinal) ? "closed" : "in_progress" })
            : ticketNumber is { } m
                ? ("get_support_ticket", new() { ["ticket_number"] = m })
            : email is not null
                ? ("get_customer_information", new() { ["email"] = email })
            : ("search_knowledge_base", new() { ["query"] = text });

        if (!offered.Contains(call.Value.Tool))
        {
            return Reply("I'm not permitted to do that with your current role.");
        }

        foreach (var key in call.Value.Args.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
        {
            call.Value.Args.Remove(key);
        }

        var message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", call.Value.Tool, call.Value.Args)]);
        return new ChatResponse(message) { ModelId = ModelId, Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 10 } };
    }

    private static ChatResponse Reply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = ModelId, Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = text.Length / 4 } };

    [GeneratedRegex(@"#(?<n>\d{1,9})")]
    private static partial Regex TicketRef();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex EmailRef();

    /// <summary>Fake summary: the user's turns from the new messages, appended to the previous summary.</summary>
    public static string Summarize(string summaryRequest)
    {
        var previous = PreviousSummary().Match(summaryRequest) is { Success: true } p ? p.Groups["text"].Value.Trim() : "(none)";
        var userTurns = string.Join(" | ", UserTurn().Matches(summaryRequest).Select(m => m.Groups["text"].Value.Trim()));
        return previous == "(none)" ? $"The user asked: {userTurns}" : $"{previous} | {userTurns}";
    }

    [GeneratedRegex(@"<previous_summary>\s*(?<text>.*?)\s*</previous_summary>", RegexOptions.Singleline)]
    private static partial Regex PreviousSummary();

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
