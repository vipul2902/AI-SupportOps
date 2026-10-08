using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Infrastructure.Ai;

public enum AiProvider
{
    /// <summary>OpenAI API (or any OpenAI-compatible endpoint, e.g. Azure OpenAI's v1 endpoint).</summary>
    OpenAI,

    /// <summary>
    /// Deterministic local stand-in for tests and offline development. Not semantically smart:
    /// it matches on shared words, not meaning.
    /// </summary>
    Fake,
}

public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.OpenAI;

    public OpenAISettings OpenAI { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider == AiProvider.OpenAI && string.IsNullOrWhiteSpace(OpenAI.ApiKey))
        {
            yield return new ValidationResult(
                "Ai:OpenAI:ApiKey is required when Ai:Provider is OpenAI. Set it via user-secrets or the OPENAI_API_KEY environment variable, or use Ai:Provider=Fake for offline development.",
                [nameof(OpenAI)]);
        }
    }
}

public sealed class OpenAISettings
{
    /// <summary>Secret. Never in appsettings: user-secrets locally, Key Vault / Container App secret in Azure.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional custom endpoint (Azure OpenAI v1 endpoint or a proxy). Null = api.openai.com.</summary>
    public Uri? Endpoint { get; set; }

    [Required]
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}
