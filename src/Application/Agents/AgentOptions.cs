using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Agents;

/// <summary>Hard safety budgets for one agent run. They bound cost, latency, and blast radius.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    [Required]
    public string PromptId { get; set; } = "agent.v1";

    /// <summary>LLM round-trips per run. Stops reasoning loops.</summary>
    [Range(1, 20)]
    public int MaxIterations { get; set; } = 6;

    [Range(1, 50)]
    public int MaxToolCallsPerRun { get; set; } = 10;

    /// <summary>Data-changing calls per run: a confused or manipulated model cannot bulk-modify tickets.</summary>
    [Range(0, 20)]
    public int MaxWriteCallsPerRun { get; set; } = 3;

    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Tool output returned to the model is truncated to bound prompt growth.</summary>
    [Range(500, 50_000)]
    public int MaxToolResultChars { get; set; } = 6000;

    [Range(50, 4000)]
    public int MaxOutputTokens { get; set; } = 800;
}
