using System.Text.Json;
using InvoiceExceptions.Tests.Application;
using InvoiceExceptions.Tests.Web;
using ModelContextProtocol.Client;

namespace InvoiceExceptions.Tests.Mcp;

/// <summary>
/// EVALUATION "Отдельное ревью" 8: "Ошибочный вход даёт понятную ошибку валидации без падения" - over MCP as well,
/// with the input revision of the case unchanged.
/// </summary>
public sealed class McpErrorTests
{
    private static async Task<int> RevisionAsync(McpClient agent, string caseId) =>
        (await agent.OkAsync("get_history", ("case_id", caseId))).GetProperty("input_revision").GetInt32();

    private static List<(string Path, string Message)> AssertValidationError(JsonElement error)
    {
        Assert.Equal("VALIDATION_ERROR", error.Str("code"));
        var details = error.GetProperty("details").EnumerateArray().Select(d => (d.Str("path")!, d.Str("message")!)).ToList();
        Assert.NotEmpty(details);
        Assert.All(details, d => Assert.False(string.IsNullOrWhiteSpace(d.Item2)));
        return details;
    }

    [Fact]
    public async Task Facts_with_quantity_zero_are_rejected_with_a_path_into_the_facts_and_nothing_is_stored()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.OkAsync("open_case", ("case_id", "case-004"));
        var revision = await RevisionAsync(agent, "case-004");

        var facts = Dataset.Extraction("case-004");
        facts["lines"]![0]!["quantity"] = "0";
        var error = await agent.ErrorAsync("submit_document_facts", ("case_id", "case-004"), ("facts", facts));

        var details = AssertValidationError(error);
        Assert.Contains(details, d => d.Path.StartsWith("/facts/lines/0/quantity", StringComparison.Ordinal));
        Assert.Equal(revision, await RevisionAsync(agent, "case-004"));
        Assert.Empty((await agent.OkAsync("get_history", ("case_id", "case-004"))).GetProperty("documents").EnumerateArray());
    }

    [Theory]
    [InlineData("ORD-999", null, null)]
    [InlineData("ORD-004", "L01", "OL-004-02")]
    public async Task A_proposal_contradicting_the_deterministic_match_is_rejected(string orderId, string? documentLine, string? orderLine)
    {
        // R2: "Предложение, противоречащее детерминированному соответствию, отклоняется ошибкой валидации при регистрации."
        // Case 004's only order is ORD-004 and it is matched by the PO reference.
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.OpenAndSubmitAsync("case-004");
        var revision = await RevisionAsync(agent, "case-004");

        var lines = documentLine is null ? Agent.LineMatches() : Agent.LineMatches((documentLine, orderLine!));
        var error = await agent.ErrorAsync(
            "propose_match",
            ("case_id", "case-004"),
            ("document_id", "DOC-004"),
            ("order_id", orderId),
            ("line_matches", lines),
            ("rationale", "Trying to move the invoice to another order."));

        AssertValidationError(error);
        Assert.Equal(revision, await RevisionAsync(agent, "case-004"));
        Assert.Equal("needs_review", (await agent.OkAsync("run_check", ("case_id", "case-004"))).GetProperty("result").Str("decision"));
    }

    [Theory]
    [InlineData("run_check")]
    [InlineData("get_history")]
    [InlineData("get_case_context")]
    [InlineData("open_case")]
    public async Task An_unknown_case_is_case_not_found(string tool)
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();

        var error = await agent.ErrorAsync(tool, ("case_id", "case-999"));

        Assert.Equal("CASE_NOT_FOUND", error.Str("code"));
    }

    [Fact]
    public async Task Facts_for_a_document_that_is_not_an_input_of_the_case_are_rejected()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.OkAsync("open_case", ("case_id", "case-004"));
        var revision = await RevisionAsync(agent, "case-004");

        var facts = Dataset.Extraction("case-004");
        facts["document_id"] = "DOC-999";
        foreach (var evidence in facts["evidence"]!.AsArray())
        {
            evidence!["document_id"] = "DOC-999";
        }

        var error = await agent.ErrorAsync("submit_document_facts", ("case_id", "case-004"), ("facts", facts));

        Assert.Contains(AssertValidationError(error), d => d.Path.StartsWith("/facts/document_id", StringComparison.Ordinal));
        Assert.Equal(revision, await RevisionAsync(agent, "case-004"));
    }
}
