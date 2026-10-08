using System.Text.Json;
using AISupportOps.Application.Agents;
using AISupportOps.Application.Agents.Tools;
using AISupportOps.Domain.Tenants;
using AISupportOps.Domain.Tickets;

namespace AISupportOps.UnitTests.Agents;

public class AgentToolTests
{
    // Schema and binding are static, so tools can be constructed with null dependencies here.
    private static readonly CreateSupportTicketTool Create = new(null!, null!);
    private static readonly UpdateSupportTicketTool Update = new(null!, null!);
    private static readonly SearchKnowledgeBaseTool Search = new(null!);

    [Fact]
    public void Schema_is_generated_from_the_argument_type_with_snake_case_required_and_descriptions()
    {
        var schema = Create.ParametersSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("customer_email", out _));
        Assert.Equal("Short summary of the issue.", properties.GetProperty("title").GetProperty("description").GetString());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("title", required);
        Assert.DoesNotContain("customer_email", required);
        Assert.Contains("critical", properties.GetProperty("priority").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_arguments_bind_to_typed_record()
    {
        var error = Create.TryBind("""{"title":"Invoice failed","description":"Card declined","priority":"high"}""", out var args);

        Assert.Null(error);
        var typed = Assert.IsType<CreateSupportTicketArgs>(args);
        Assert.Equal(TicketPriority.High, typed.Priority);
    }

    [Theory]
    [InlineData("""{"title":"ok title","description":"d","is_admin":true}""", "is_admin")]          // smuggled field
    [InlineData("""{"title":"x","description":"d"}""", "Title")]                                     // too short
    [InlineData("""{"description":"d"}""", "title")]                                                 // missing required
    [InlineData("""{"title":"ok title","description":"d","priority":"apocalyptic"}""", "priority")] // bad enum
    [InlineData("""{"title":"ok title","description":"d","customer_email":"not-an-email"}""", "CustomerEmail")]
    [InlineData("not json at all", "not valid")]
    public void Invalid_arguments_are_rejected_with_a_message_the_model_can_act_on(string json, string expectedFragment)
    {
        var error = Create.TryBind(json, out var args);

        Assert.Null(args);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_offers_only_tools_permitted_for_the_role()
    {
        var registry = new ToolRegistry([Create, Update, Search]);

        Assert.Equal(["search_knowledge_base"], registry.PermittedFor(TenantRole.Viewer).Select(t => t.Name));
        Assert.Equal(3, registry.PermittedFor(TenantRole.Agent).Count);
        Assert.Null(registry.Find("drop_database"));
    }

    [Fact]
    public void Write_tools_are_flagged()
    {
        Assert.True(Create.IsWrite);
        Assert.True(Update.IsWrite);
        Assert.False(Search.IsWrite);
        Assert.Equal(JsonValueKind.Object, Update.ParametersSchema.ValueKind);
    }
}
