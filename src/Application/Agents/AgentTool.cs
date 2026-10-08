using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AISupportOps.Domain.Tenants;

namespace AISupportOps.Application.Agents;

/// <summary>Outcome of a tool's own logic, returned to the model as JSON.</summary>
public sealed record ToolResult(bool Success, object? Data, string? Error)
{
    public static ToolResult Ok(object data) => new(true, data, null);

    public static ToolResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// A capability the agent may use. Adding a tool = adding one class; the agent loop, registry,
/// authorization, validation, and logging need no changes.
/// </summary>
public interface IAgentTool
{
    /// <summary>Function name shown to the model (snake_case, as LLM providers expect).</summary>
    string Name { get; }

    /// <summary>Tells the model when and how to use the tool. Part of the prompt surface.</summary>
    string Description { get; }

    /// <summary>Least privilege: the minimum role whose agent may even see this tool.</summary>
    TenantRole MinimumRole { get; }

    /// <summary>Write tools change data; they are budgeted per run and audited as AI actions.</summary>
    bool IsWrite { get; }

    JsonElement ParametersSchema { get; }

    /// <summary>Parses and validates untrusted arguments. Returns an error message for the model, or null.</summary>
    string? TryBind(string argumentsJson, out object? arguments);

    Task<ToolResult> ExecuteAsync(object arguments, CancellationToken ct);
}

/// <summary>
/// Base class with typed arguments. The JSON schema the model sees is generated from
/// <typeparamref name="TArgs"/>, and the same type validates the model's output, so the advertised
/// contract and the enforced contract cannot drift apart.
/// </summary>
public abstract class AgentTool<TArgs> : IAgentTool
    where TArgs : class
{
    private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web)
    {
        // Reject fields the schema does not declare: a model (or injected text) cannot smuggle extra parameters.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(), // required by the JSON schema exporter
    };

    private static readonly Lazy<JsonElement> Schema = new(BuildSchema);

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract TenantRole MinimumRole { get; }

    public abstract bool IsWrite { get; }

    public JsonElement ParametersSchema => Schema.Value;

    public string? TryBind(string argumentsJson, out object? arguments)
    {
        arguments = null;
        TArgs? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TArgs>(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson, ArgumentOptions);
        }
        catch (JsonException ex)
        {
            return $"Arguments are not valid for {Name}: {ex.Message}";
        }

        if (parsed is null)
        {
            return $"Arguments for {Name} are required.";
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(parsed, new ValidationContext(parsed), results, validateAllProperties: true))
        {
            return $"Invalid arguments for {Name}: {string.Join(" ", results.Select(r => r.ErrorMessage))}";
        }

        arguments = parsed;
        return null;
    }

    public Task<ToolResult> ExecuteAsync(object arguments, CancellationToken ct) => ExecuteAsync((TArgs)arguments, ct);

    protected abstract Task<ToolResult> ExecuteAsync(TArgs arguments, CancellationToken ct);

    private static JsonElement BuildSchema()
    {
        var node = ArgumentOptions.GetJsonSchemaAsNode(typeof(TArgs), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
            {
                // Surface [Description] attributes so the model knows what each parameter means.
                var description = context.PropertyInfo?.AttributeProvider?
                    .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                    .OfType<DescriptionAttribute>().FirstOrDefault()?.Description;
                if (description is not null && schema is JsonObject obj)
                {
                    obj.Insert(0, "description", description);
                }

                return schema;
            },
        });

        if (node is JsonObject root)
        {
            root["additionalProperties"] = false;
        }

        return JsonSerializer.SerializeToElement(node);
    }
}
