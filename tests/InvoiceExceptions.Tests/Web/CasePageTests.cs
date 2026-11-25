using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Cases;

namespace InvoiceExceptions.Tests.Web;

public sealed class CasePageTests
{
    [Fact]
    public async Task Unknown_case_is_404()
    {
        using var app = new AppFixture();
        var page = await new CasePageClient(app).GetAsync("/cases/case-404");

        Assert.Equal(HttpStatusCode.NotFound, page.Status);
    }

    [Fact]
    public async Task Index_lists_opened_cases_with_their_outcome_and_the_cases_not_opened_yet()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-004", 1);
        client.Replay("case-008", 1);

        var page = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var row = page.Html.QuerySelector("tr[data-case='case-004']")!;
        Assert.Contains("Needs review", row.TextContent);
        Assert.Contains("DOC-004", row.TextContent);
        Assert.Equal("/cases/case-004", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Duplicate of ORIGINAL-008", page.Html.QuerySelector("tr[data-case='case-008']")!.TextContent);
        Assert.Contains("open_case", page.Text);
        Assert.Contains(page.Html.QuerySelectorAll(".plain-list li"), li => li.TextContent == "case-001");
        Assert.DoesNotContain(page.Html.QuerySelectorAll(".plain-list li"), li => li.TextContent == "case-004");
    }

    [Fact]
    public async Task Case_010_step_1_needs_information_and_asks_for_the_order_reference()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-010", 1);

        var page = await client.GetAsync("/cases/case-010");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Needs information", page.Outcome);
        Assert.Contains("order_reference", page.Html.QuerySelector("#doc0-questions + table")!.TextContent);
        Assert.Equal("Unknown", page.Check("ORDER_MATCH", "DOC-010")!.QuerySelector(".badge")!.TextContent);
        // Figures that depend on the order are not known yet - and are not shown as 0.
        Assert.Equal("not known yet", page.LineField("L01", "ordered"));
        Assert.Empty(page.Html.QuerySelectorAll("form.confirm"));
    }

    [Fact]
    public async Task Case_010_step_2_needs_review_and_offers_the_proposal_for_confirmation()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-010", 1, 2);

        var page = await client.GetAsync("/cases/case-010");

        Assert.Contains("Needs review", page.Outcome);
        var proposal = page.Html.QuerySelector("article[data-proposal='PROP-010-01']")!;
        Assert.Contains("Awaiting confirmation", proposal.TextContent);
        Assert.Contains("ORD-010", proposal.TextContent);
        // The reviewer sees the invoice wording next to the order wording.
        Assert.Contains("Galvanised wall plug 10mm", proposal.TextContent);
        Assert.Contains("Wall plug galv. 10 mm", proposal.TextContent);
        Assert.Contains("already computed against this proposed order", proposal.TextContent);
        Assert.Contains("agent cannot take this step", proposal.TextContent);

        var form = page.ConfirmationForm("PROP-010-01")!;
        Assert.Equal("post", form.GetAttribute("method"), ignoreCase: true);
        Assert.NotNull(form.QuerySelector("input[name='__RequestVerificationToken']"));
        Assert.NotNull(form.QuerySelector("input[name='confirmedBy'][required]"));
        Assert.Equal("Confirm this proposal", form.QuerySelector("button[type=submit]")!.TextContent.Trim());
        Assert.Equal("Fail", page.Check("ORDER_MATCH", "DOC-010")!.QuerySelector(".badge")!.TextContent);
    }

    [Fact]
    public async Task Confirming_on_the_page_records_the_decision_and_keeps_earlier_conclusions_viewable()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-010", 1, 2);
        var before = await client.GetAsync("/cases/case-010");

        var response = await client.SubmitConfirmationAsync(before, "PROP-010-01", "  Ana Reviewer  ");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/cases/case-010", location);
        Assert.DoesNotContain("handler", location, StringComparison.OrdinalIgnoreCase);

        var page = await client.GetAsync(location);
        Assert.Contains("Clear", page.Outcome);
        Assert.Contains("confirmed by Ana Reviewer", page.Html.QuerySelector(".notice-success")!.TextContent);
        var proposal = page.Html.QuerySelector("article[data-proposal='PROP-010-01']")!;
        Assert.Equal("Ana Reviewer", proposal.QuerySelector(".confirmed-by")!.TextContent);
        Assert.Null(page.ConfirmationForm("PROP-010-01"));
        Assert.Equal("Pass", page.Check("ORDER_MATCH", "DOC-010")!.QuerySelector(".badge")!.TextContent);
        Assert.Contains("Confirmed by a person", page.Html.QuerySelector("table.kv")!.ParentElement!.TextContent);

        var stored = client.Cases.Get("case-010")!;
        Assert.Equal(3, stored.InputRevision);
        Assert.Equal("human_confirmation", stored.Documents[0].Current.Result.OrderMatchSource);
        Assert.Equal("Ana Reviewer", stored.Revisions[^1].ActorName);
        Assert.Equal("web", stored.Revisions[^1].Channel);

        var history = page.Html.QuerySelectorAll("table.history tbody tr");
        Assert.Equal(["3", "2", "1"], history.Select(r => r.GetAttribute("data-revision")));
        Assert.Contains("Clear", history[0].TextContent);
        Assert.Contains("Needs review", history[1].TextContent);
        Assert.Contains("Needs information", history[2].TextContent);

        var earlierLink = history[2].QuerySelector("a.view-revision")!.GetAttribute("href")!;
        var earlier = await client.GetAsync(WebUtility.HtmlDecode(earlierLink));
        Assert.Equal(HttpStatusCode.OK, earlier.Status);
        Assert.Contains("Needs information", earlier.Outcome);
        Assert.Contains("not the current one", earlier.Html.QuerySelector(".notice-history")!.TextContent);
        Assert.Empty(earlier.Html.QuerySelectorAll("form.confirm"));

        var middle = await client.GetAsync("/cases/case-010?revision=2&document=DOC-010");
        Assert.Contains("Needs review", middle.Outcome);
        Assert.Empty(middle.Html.QuerySelectorAll("form.confirm"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/cases/case-010?revision=9")).Status);
    }

    [Fact]
    public async Task A_post_without_the_antiforgery_token_is_rejected_and_changes_nothing()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-010", 1, 2);
        var page = await client.GetAsync("/cases/case-010");

        var response = await client.SubmitConfirmationAsync(page, "PROP-010-01", "Mallory", withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, client.Cases.Get("case-010")!.InputRevision);
        Assert.Null(client.Cases.Get("case-010")!.Documents[0].Proposals[0].Confirmation);
    }

    [Fact]
    public async Task A_blank_name_is_rejected_and_changes_nothing()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-010", 1, 2);
        var page = await client.GetAsync("/cases/case-010");

        var response = await client.SubmitConfirmationAsync(page, "PROP-010-01", "   ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var shown = await Page.From(response);
        Assert.Contains("Enter your name", shown.Html.QuerySelector(".notice-error")!.TextContent);
        Assert.NotNull(shown.ConfirmationForm("PROP-010-01"));
        Assert.Equal(2, client.Cases.Get("case-010")!.InputRevision);
    }

    [Fact]
    public async Task Confirming_an_already_confirmed_proposal_shows_the_reason_and_creates_no_revision()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-010", 1, 2);
        var page = await client.GetAsync("/cases/case-010");
        client.Replay("case-010", 3); // someone else confirmed it in the meantime

        var response = await client.SubmitConfirmationAsync(page, "PROP-010-01", "Second Reviewer");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var shown = await Page.From(response);
        Assert.Contains("already confirmed by fixture-reviewer", shown.Html.QuerySelector(".notice-error")!.TextContent);
        Assert.Equal(3, client.Cases.Get("case-010")!.InputRevision);
    }

    [Fact]
    public async Task Case_004_shows_the_unit_price_failure_with_its_explanation_and_source()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-004", 1);
        var expected = client.Cases.Get("case-004")!.Documents[0];
        var explanation = expected.Current.Result.Checks.Single(c => c.Code == "UNIT_PRICE" && c.ObjectId == "L01").Explanation;
        var excerpt = expected.Facts.Evidence.Single(e => e.FieldPath == "/lines/0/unit_net_price").Excerpt;

        var page = await client.GetAsync("/cases/case-004");

        Assert.Contains("Needs review", page.Outcome);
        var row = page.Check("UNIT_PRICE", "L01")!;
        Assert.Equal("Fail", row.QuerySelector(".badge")!.TextContent);
        Assert.Contains("needs_review", row.TextContent);
        Assert.False(string.IsNullOrWhiteSpace(explanation));
        Assert.Equal(explanation, row.QuerySelector("td.explanation")!.TextContent.Trim());
        var source = row.QuerySelector("td.sources")!;
        Assert.Contains("/lines/0/unit_net_price", source.TextContent);
        Assert.Contains(excerpt, source.QuerySelectorAll("q.excerpt").Select(q => q.TextContent));
    }

    [Fact]
    public async Task Case_003_shows_the_quantity_figures()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-003", 1);

        var page = await client.GetAsync("/cases/case-003");

        Assert.Contains("Blocked", page.Outcome);
        Assert.Equal("100", page.LineField("L01", "ordered"));
        Assert.Equal("100", page.LineField("L01", "received"));
        Assert.Equal("40", page.LineField("L01", "previously-invoiced"));
        Assert.Equal("60", page.LineField("L01", "available"));
        Assert.Equal("60", page.LineField("L01", "remaining"));
        Assert.Equal("Fail", page.Check("QTY_RECEIVED", "L01")!.QuerySelector(".badge")!.TextContent);
    }

    [Fact]
    public async Task Case_008_is_shown_as_a_duplicate_without_checks()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-008", 1);

        var page = await client.GetAsync("/cases/case-008");

        Assert.Contains("Duplicate of ORIGINAL-008", page.Outcome);
        Assert.Empty(page.Html.QuerySelectorAll("table.checks"));
        Assert.Empty(page.Html.QuerySelectorAll("tr[data-check]"));
    }

    [Fact]
    public async Task Case_009_shows_the_conflict()
    {
        using var app = new AppFixture();
        var client = new CasePageClient(app);
        client.Replay("case-009", 1);

        var page = await client.GetAsync("/cases/case-009");

        Assert.Contains("Blocked", page.Outcome);
        Assert.Contains("ORIGINAL-009", page.Html.QuerySelector(".outcome .conflict")!.TextContent);
        Assert.Equal("Fail", page.Check("DOCUMENT_CONFLICT", "DOC-009")!.QuerySelector(".badge")!.TextContent);
    }

    [Fact]
    public async Task Untrusted_text_from_the_document_and_the_agent_is_encoded()
    {
        const string Payload = "<script>alert(1)</script>";
        using var app = new AppFixture();
        var client = new CasePageClient(app);

        var step = JsonNode.Parse(File.ReadAllText(CasePageClient.StepPath("case-010", 1)))!;
        var facts = step["document"]!["facts"]!;
        facts["lines"]![0]!["description"] = "Wall plug " + Payload;
        foreach (var evidence in facts["evidence"]!.AsArray())
        {
            if ((string?)evidence!["field_path"] == "/lines/0/description")
            {
                evidence["excerpt"] = "Wall plug " + Payload;
            }
        }

        client.Cases.Apply(JsonDocument.Parse(step.ToJsonString()).RootElement, Actor.Runner);
        client.Cases.RegisterProposal(
            "case-010", "DOC-010", "ORD-010",
            [new("L01", "OL-010-01")],
            "Descriptions match. " + Payload,
            Actor.Agent);

        var page = await client.GetAsync("/cases/case-010");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page.Body);
        Assert.DoesNotContain(Payload, page.Body);
        Assert.Empty(page.Html.QuerySelectorAll("script"));
        Assert.Contains(Payload, page.Html.QuerySelector(".rationale")!.TextContent);
    }
}
