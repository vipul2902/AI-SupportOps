using System.Text.Json;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Knowledge;

namespace AISupportOps.Application.Evaluation;

public sealed record GroundednessVerdict(double? Score, string? Notes);

/// <summary>
/// LLM-as-judge for faithfulness: asks a model whether each claim in the answer is supported by the
/// sources the answer was generated from. Useful for spotting regressions at scale, but it is itself
/// a model: it can be wrong, inconsistent, or lenient. Treat scores as a signal, not a proof.
/// </summary>
public sealed class GroundednessJudge(IAiChatService llm)
{
    public const string PromptId = "eval-groundedness.v1";

    public async Task<GroundednessVerdict> JudgeAsync(RagPreparation preparation, string answer, CancellationToken ct)
    {
        var sources = string.Join("\n\n", preparation.Sources.Select(s => $"[{s.Number}] {RagContextBuilder.EscapeContent(s.Chunk.Content)}"));
        var request = new LlmRequest(
            PromptLibrary.Get(PromptId),
            [new LlmMessage(LlmRole.User, $"<sources>\n{sources}\n</sources>\n\n<answer>\n{RagContextBuilder.EscapeContent(answer)}\n</answer>")],
            MaxOutputTokens: 300,
            Temperature: 0f);

        string text;
        try
        {
            text = (await llm.CompleteAsync(request, ct)).Text;
        }
        catch (AiUnavailableException ex)
        {
            return new GroundednessVerdict(null, $"Judge unavailable: {ex.Message}");
        }

        return Parse(text);
    }

    /// <summary>Tolerates prose around the JSON; a malformed verdict yields no score rather than a guess.</summary>
    public static GroundednessVerdict Parse(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return new GroundednessVerdict(null, "Judge returned no JSON verdict.");
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("score", out var scoreElement) || !scoreElement.TryGetDouble(out var score) || score is < 1 or > 5)
            {
                return new GroundednessVerdict(null, "Judge verdict had no valid score.");
            }

            var claims = root.TryGetProperty("unsupported_claims", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                : [];
            var notes = claims.Count == 0 ? null : "Unsupported: " + string.Join("; ", claims);
            return new GroundednessVerdict(Math.Round((score - 1) / 4, 3), notes);
        }
        catch (JsonException)
        {
            return new GroundednessVerdict(null, "Judge returned malformed JSON.");
        }
    }
}
