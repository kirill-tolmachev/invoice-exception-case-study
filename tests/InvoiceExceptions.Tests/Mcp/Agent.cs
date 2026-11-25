using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Tests.Application;
using InvoiceExceptions.Tests.Web;
using ModelContextProtocol.Client;

namespace InvoiceExceptions.Tests.Mcp;

/// <summary>Tool calls as an agent would make them, with arguments passed as JSON.</summary>
internal static class Agent
{
    public static Dictionary<string, object?> Args(params (string Name, object? Value)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => a.Value switch
        {
            null => (object?)null,
            string s => s,
            JsonElement e => e,
            JsonNode n => JsonSerializer.SerializeToElement(n),
            var other => JsonSerializer.SerializeToElement(other),
        });

    /// <summary>Calls a tool that must succeed and returns its JSON reply.</summary>
    public static async Task<JsonElement> OkAsync(this McpClient agent, string tool, params (string Name, object? Value)[] arguments)
    {
        var (json, isError) = await agent.CallAsync(tool, Args(arguments));
        Assert.False(isError, $"{tool} failed: {json}");
        return json;
    }

    /// <summary>Calls a tool that must fail and returns its JSON error object.</summary>
    public static async Task<JsonElement> ErrorAsync(this McpClient agent, string tool, params (string Name, object? Value)[] arguments)
    {
        var (json, isError) = await agent.CallAsync(tool, Args(arguments));
        Assert.True(isError, $"{tool} was expected to fail but answered: {json}");
        return json.GetProperty("error");
    }

    public static JsonArray LineMatches(params (string Doc, string Order)[] lines) =>
        new([.. lines.Select(l => (JsonNode)new JsonObject { ["document_line_id"] = l.Doc, ["order_line_id"] = l.Order })]);

    /// <summary>open_case, then submit the reference extraction of the case as the agent's reading of the PDF.</summary>
    public static async Task<JsonElement> OpenAndSubmitAsync(this McpClient agent, string caseId, JsonObject? facts = null)
    {
        await agent.OkAsync("open_case", ("case_id", caseId));
        return await agent.OkAsync("submit_document_facts", ("case_id", caseId), ("facts", facts ?? Dataset.Extraction(caseId)));
    }

    public static JsonElement Check(JsonElement result, string code, string objectId) =>
        result.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == code && c.GetProperty("object_id").GetString() == objectId);

    public static string? Str(this JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    /// <summary>The fields the MCP result does not carry or legitimately reports differently from the runner.</summary>
    public static readonly string[] NotComparedOverMcp = ["case_id", "step", "context_version"];
}
