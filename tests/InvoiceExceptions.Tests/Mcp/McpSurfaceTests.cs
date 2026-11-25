using System.Text.Json;
using InvoiceExceptions.Tests.Application;
using InvoiceExceptions.Tests.Web;

namespace InvoiceExceptions.Tests.Mcp;

/// <summary>
/// README: "агент приносит факты и предложения, но не выносит решение. MCP не предоставляет операцию подтверждения и
/// действие «сделать clear»".
/// </summary>
public sealed class McpSurfaceTests
{
    private static readonly string[] ExpectedTools =
    [
        "get_case_context", "get_history", "get_questions", "get_result", "list_cases",
        "open_case", "propose_match", "read_document", "run_check", "submit_document_facts",
    ];

    [Fact]
    public async Task The_agent_sees_exactly_the_published_tools_and_none_confirms_approves_or_decides()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();

        var tools = await agent.ListToolsAsync();

        Assert.Equal(ExpectedTools, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            foreach (var forbidden in new[] { "confirm", "approve", "decide", "clear" })
            {
                Assert.DoesNotContain(forbidden, tool.Name, StringComparison.OrdinalIgnoreCase);
            }

            // No argument of any tool lets the caller state a decision, a confirmation or an approval either.
            var arguments = tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(p => p.Name).ToList()
                : [];
            Assert.DoesNotContain(arguments, a =>
                a.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("approv", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("decision", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Submit_document_facts_publishes_the_extraction_schema_inline()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();

        var tool = (await agent.ListToolsAsync()).Single(t => t.Name == "submit_document_facts");
        var schema = tool.ProtocolTool.InputSchema;

        Assert.Contains("facts", schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        var facts = schema.GetProperty("properties").GetProperty("facts");
        var price = facts.GetProperty("properties").GetProperty("lines").GetProperty("items").GetProperty("properties").GetProperty("unit_net_price");
        Assert.Equal("^[0-9]+\\.[0-9]{2}$", price.GetProperty("pattern").GetString());
        Assert.False(facts.GetProperty("additionalProperties").GetBoolean());

        var published = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Dataset, "schemas", "extraction.schema.json"))).RootElement;
        Assert.Equal(
            published.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order(),
            facts.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order());
        Assert.Empty(References(facts));
    }

    private static IEnumerable<string> References(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => p.Name == "$ref" ? [p.Value.ToString()] : References(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(References),
        _ => [],
    };

    [Fact]
    public async Task List_cases_lists_the_dataset()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();

        var listed = await agent.OkAsync("list_cases");

        var ids = listed.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("case_id").GetString()!).ToList();
        Assert.Equal(Dataset.CaseIds(), ids);
    }
}
