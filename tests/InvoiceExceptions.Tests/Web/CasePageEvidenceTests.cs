using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Cases;

namespace InvoiceExceptions.Tests.Web;

/// <summary>The sources on the case page are compared with the PDF the facts were read from.</summary>
public sealed class CasePageEvidenceTests
{
    [Fact]
    public async Task Excerpts_that_are_in_the_pdf_are_marked_as_found()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-004", 1);

        var page = await client.GetAsync("/cases/case-004");

        Assert.Contains("excerpts were found on the stated page of the PDF", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("not found on page", page.Text, StringComparison.Ordinal);
        Assert.Contains("found on page", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_excerpt_that_is_not_in_the_pdf_is_flagged_to_the_reviewer()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        var step = JsonNode.Parse(File.ReadAllText(CasePageClient.StepPath("case-004", 1)))!;
        step["document"]!["facts"]!["evidence"]!.AsArray()
            .First(e => e!["field_path"]!.GetValue<string>() == "/lines/0/unit_net_price")!["excerpt"] = "12,50";
        client.Cases.Apply(step.Deserialize<JsonElement>(), Actor.Runner);

        var page = await client.GetAsync("/cases/case-004");

        Assert.Contains("1 of 34 excerpts were not found on the stated page of the PDF", page.Text, StringComparison.Ordinal);
        Assert.Contains("not found on page", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Facts_read_from_a_different_file_are_not_vouched_for()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        var step = JsonNode.Parse(File.ReadAllText(CasePageClient.StepPath("case-004", 1)))!;
        step["document"]!["content_sha256"] = new string('0', 64);
        client.Cases.Apply(step.Deserialize<JsonElement>(), Actor.Runner);

        var page = await client.GetAsync("/cases/case-004");

        Assert.Contains("could not be compared with the PDF", page.Text, StringComparison.Ordinal);
        Assert.Contains("not checked", page.Text, StringComparison.Ordinal);
    }
}
