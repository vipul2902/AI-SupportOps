using System.Text.RegularExpressions;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Ingestion;
using AISupportOps.Domain.Chat;

namespace AISupportOps.Application.Chat;

/// <summary>
/// Short-term memory: the most recent turns that fit both a message cap and a token cap, chosen
/// newest-first and returned oldest-first. Sending unbounded history would make every turn slower and
/// more expensive than the last, and eventually overflow the model's context window.
/// </summary>
public static partial class HistoryWindow
{
    public static IReadOnlyList<LlmMessage> Select(
        IReadOnlyList<Message> chronological, int maxMessages, int maxTokens, ITokenCounter tokens)
    {
        var selected = new List<LlmMessage>();
        var used = 0;

        for (var i = chronological.Count - 1; i >= 0 && selected.Count < maxMessages; i--)
        {
            var message = chronological[i];
            if (message.Status == MessageStatus.Failed)
            {
                continue;
            }

            var content = message.Role == MessageRole.Assistant ? StripCitationMarkers(message.Content) : message.Content;
            var cost = tokens.Count(content);
            if (used + cost > maxTokens)
            {
                break;
            }

            selected.Add(new LlmMessage(message.Role == MessageRole.User ? LlmRole.User : LlmRole.Assistant, content));
            used += cost;
        }

        selected.Reverse();

        // A window must not start with an orphaned assistant reply.
        while (selected.Count > 0 && selected[0].Role == LlmRole.Assistant)
        {
            selected.RemoveAt(0);
        }

        return selected;
    }

    /// <summary>
    /// "[2]" in an old answer referred to that turn's sources. Left in, the model could confuse it
    /// with the current turn's numbering and produce wrong citations.
    /// </summary>
    public static string StripCitationMarkers(string text) => CitationMarker().Replace(text, string.Empty).Trim();

    [GeneratedRegex(@"\s?\[\d{1,3}\]")]
    private static partial Regex CitationMarker();
}
