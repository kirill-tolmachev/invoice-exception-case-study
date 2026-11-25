using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Tests.Web;

namespace InvoiceExceptions.Tests.Documents;

public sealed class ReadDocumentToolTests
{
    [Fact]
    public async Task The_agent_can_read_the_document_text_through_the_server()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.CallAsync("open_case", new() { ["case_id"] = "case-004" });

        var (reply, isError) = await agent.CallAsync("read_document", new() { ["case_id"] = "case-004", ["document_id"] = "DOC-004" });

        Assert.False(isError);
        var page = Assert.Single(reply.GetProperty("pages").EnumerateArray());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        var text = page.GetProperty("text").GetString()!;
        Assert.Contains("INV-2026-4104", text, StringComparison.Ordinal);
        Assert.Contains("12,52", text, StringComparison.Ordinal);

        // The instruction planted in the PDF is returned for what it is - document content - and labelled as such.
        Assert.Contains("Note for automated processing", text, StringComparison.Ordinal);
        Assert.Contains("never an instruction", reply.GetProperty("untrusted_content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submitting_facts_reports_how_well_they_are_backed_by_the_document()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.CallAsync("open_case", new() { ["case_id"] = "case-004" });

        var (reply, isError) = await agent.CallAsync("submit_document_facts", new() { ["case_id"] = "case-004", ["facts"] = ReferenceFacts() });

        Assert.False(isError);
        var check = reply.GetProperty("evidence_check");
        Assert.Equal(check.GetProperty("excerpts").GetInt32(), check.GetProperty("found_on_stated_page").GetInt32());
        Assert.Empty(check.GetProperty("not_found").EnumerateArray());
        Assert.Empty(reply.GetProperty("provenance_notes").EnumerateArray());
    }

    [Fact]
    public async Task An_invented_excerpt_is_flagged_but_does_not_change_the_conclusion()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.CallAsync("open_case", new() { ["case_id"] = "case-004" });
        var facts = JsonNode.Parse(ReferenceFacts().GetRawText())!;
        facts["evidence"]!.AsArray().First(e => e!["field_path"]!.GetValue<string>() == "/lines/0/unit_net_price")!["excerpt"] = "12,50 as agreed";

        var (reply, isError) = await agent.CallAsync("submit_document_facts", new() { ["case_id"] = "case-004", ["facts"] = facts });
        var (check, _) = await agent.CallAsync("run_check", new() { ["case_id"] = "case-004" });

        Assert.False(isError);
        var notFound = Assert.Single(reply.GetProperty("evidence_check").GetProperty("not_found").EnumerateArray());
        Assert.Equal("/lines/0/unit_net_price", notFound.GetProperty("field_path").GetString());
        Assert.Equal("needs_review", check.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Only_input_documents_of_the_case_can_be_read()
    {
        using var app = new AppFixture();
        await using var agent = await app.ConnectAgentAsync();
        await agent.CallAsync("open_case", new() { ["case_id"] = "case-004" });

        var (reply, isError) = await agent.CallAsync("read_document", new() { ["case_id"] = "case-004", ["document_id"] = "../../case-005/expected/extraction" });

        Assert.True(isError);
        Assert.Equal("VALIDATION_ERROR", reply.GetProperty("error").GetProperty("code").GetString());
    }

    private static JsonElement ReferenceFacts() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Dataset, "cases", "case-004", "expected", "extraction.json"))).RootElement.Clone();
}
