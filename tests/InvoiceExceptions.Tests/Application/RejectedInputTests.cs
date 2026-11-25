using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Runner;
using InvoiceExceptions.Application.Validation;

namespace InvoiceExceptions.Tests.Application;

/// <summary>
/// BUSINESS_RULES "Валидация входа": an invalid input is rejected with a validation error and no conclusion is created.
/// EVALUATION: exit code 2 and {"error": {"code": "VALIDATION_ERROR", "details": [...]}}. Beyond that, a rejected input
/// must leave the store exactly as it was.
/// </summary>
public sealed class RejectedInputTests
{
    private const string FortyDigits = "1234567890123456789012345678901234567890";

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Asserts the adapter's validation-error shape and returns the details.</summary>
    private static List<(string Path, string Message)> AssertRejected(int exit, JsonElement output)
    {
        Assert.True(exit == StepRunner.ExitValidationError, $"expected exit code 2, got {exit}: {output}");
        Assert.Empty(OutputSchema.Problems(output));
        var error = output.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        var details = error.GetProperty("details").EnumerateArray()
            .Select(d => (d.GetProperty("path").GetString()!, d.GetProperty("message").GetString()!))
            .ToList();
        Assert.NotEmpty(details);
        Assert.All(details, d => Assert.False(string.IsNullOrWhiteSpace(d.Item2)));
        return details;
    }

    private static void AssertSomePathStartsWith(List<(string Path, string Message)> details, string prefix) =>
        Assert.True(
            details.Any(d => d.Path.StartsWith(prefix, StringComparison.Ordinal)),
            $"no detail path starts with {prefix}: {string.Join("; ", details.Select(d => $"{d.Path}: {d.Message}"))}");

    /// <summary>A document-only step re-submitting case-001's facts (valid as it stands).</summary>
    private static JsonObject Case001Resubmission(int step = 2)
    {
        var input = Dataset.Step("case-001", 1);
        input.Remove("context");
        input["step"] = step;
        return input;
    }

    private static JsonObject Facts(JsonObject step) => step["document"]!["facts"]!.AsObject();

    private static JsonObject Envelope(string caseId, int step) => new()
    {
        ["schema_version"] = "1.0",
        ["case_id"] = caseId,
        ["step"] = step,
        ["as_of"] = "2026-11-02",
        ["policy_ref"] = "core-v1",
    };

    private static JsonObject ProposalStep(string caseId, int step, string proposalId, string documentId, string orderId, params (string Doc, string Order)[] lines)
    {
        var input = Envelope(caseId, step);
        input["proposals"] = new JsonArray(new JsonObject
        {
            ["proposal_id"] = proposalId,
            ["actor"] = "agent",
            ["document_id"] = documentId,
            ["order_id"] = orderId,
            ["line_matches"] = new JsonArray([.. lines.Select(l => (JsonNode)new JsonObject { ["document_line_id"] = l.Doc, ["order_line_id"] = l.Order })]),
        });
        return input;
    }

    private static JsonObject ConfirmationStep(string caseId, int step, string proposalId)
    {
        var input = Envelope(caseId, step);
        input["confirmations"] = new JsonArray(new JsonObject { ["proposal_id"] = proposalId, ["actor"] = "human", ["confirmed_by"] = "reviewer" });
        return input;
    }

    // ---- invalid facts: one mutation of an otherwise valid document --------------------------------------------------

    public static TheoryData<string, string> FactMutations => new()
    {
        { "quantity zero", "/document/facts/lines/0/quantity" },
        { "negative unit price", "/document/facts/lines/0/unit_net_price" },
        { "unknown property", "/document/facts" },
        { "missing required field", "/document/facts" },
        { "decimal that overflows", "/document/facts" },
        { "duplicate line ids", "/document/facts/lines" },
    };

    private static void Mutate(JsonObject step, string mutation)
    {
        var facts = Facts(step);
        var lines = facts["lines"]!.AsArray();
        switch (mutation)
        {
            case "quantity zero":
                lines[0]!["quantity"] = "0";
                break;
            case "negative unit price":
                lines[0]!["unit_net_price"] = "-8.75";
                break;
            case "unknown property":
                facts["approved_by_buyer"] = true;
                break;
            case "missing required field":
                facts.Remove("number");
                break;
            case "decimal that overflows":
                // Fits the published money pattern ^[0-9]+\.[0-9]{2}$ but not any decimal type.
                lines[0]!["declared_net_amount"] = FortyDigits + ".00";
                facts["declared_subtotal"] = FortyDigits + ".00";
                break;
            case "duplicate line ids":
                lines[1]!["line_id"] = lines[0]!["line_id"]!.GetValue<string>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    [Theory]
    [MemberData(nameof(FactMutations))]
    public void Invalid_facts_as_the_first_step_create_no_case(string mutation, string pathPrefix)
    {
        using var workspace = new CaseWorkspace();
        var step = Dataset.Step("case-001", 1);
        Mutate(step, mutation);

        var (exit, output) = workspace.Run(step);

        AssertSomePathStartsWith(AssertRejected(exit, output), pathPrefix);
        Assert.Empty(workspace.Cases.ListCaseIds());
        Assert.Null(workspace.Snapshot("case-001"));
    }

    [Theory]
    [MemberData(nameof(FactMutations))]
    public void Invalid_facts_for_an_existing_case_leave_the_store_unchanged(string mutation, string pathPrefix)
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var before = workspace.Snapshot("case-001");

        var step = Case001Resubmission();
        Mutate(step, mutation);
        var (exit, output) = workspace.Run(step);

        AssertSomePathStartsWith(AssertRejected(exit, output), pathPrefix);
        Assert.Equal(before, workspace.Snapshot("case-001"));
        Assert.Equal(1, workspace.Cases.Get("case-001")!.InputRevision);
    }

    [Fact]
    public void The_unmutated_resubmission_used_above_is_itself_accepted()
    {
        // Without this the tests above could pass because the base input was already invalid.
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var output = workspace.RunOk(Case001Resubmission());
        Assert.Equal(2, output.GetProperty("input_revision").GetInt32());
    }

    // ---- invalid step shapes ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"case_id\": \"case-001\",")]
    [InlineData("[1, 2, 3]")]
    public void Input_that_is_not_a_step_creates_nothing(string text)
    {
        using var workspace = new CaseWorkspace();
        var (exit, output) = workspace.Run(text);

        AssertRejected(exit, output);
        Assert.Empty(workspace.Cases.ListCaseIds());
    }

    [Fact]
    public void A_step_with_none_of_context_document_proposals_or_confirmations_is_rejected()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var before = workspace.Snapshot("case-001");

        var (exit, output) = workspace.Run(Envelope("case-001", 2));

        AssertRejected(exit, output);
        Assert.Equal(before, workspace.Snapshot("case-001"));
    }

    [Fact]
    public void The_first_step_of_a_case_without_context_is_rejected()
    {
        using var workspace = new CaseWorkspace();
        var step = Case001Resubmission(step: 1);

        var (exit, output) = workspace.Run(step);

        AssertSomePathStartsWith(AssertRejected(exit, output), "/context");
        Assert.Empty(workspace.Cases.ListCaseIds());
    }

    [Fact]
    public void A_context_only_step_on_a_case_without_a_document_is_rejected()
    {
        // The adapter must answer with the result for the case's document; with no document there is none.
        using var workspace = new CaseWorkspace();
        var step = Dataset.Step("case-001", 1);
        step.Remove("document");

        var (exit, output) = workspace.Run(step);

        AssertRejected(exit, output);
        Assert.Empty(workspace.Cases.ListCaseIds());
    }

    // ---- proposals and confirmations ------------------------------------------------------------------------------

    public static TheoryData<string> BadProposals => new()
    {
        "unknown document", "unknown order", "unknown order line", "unknown document line",
    };

    [Theory]
    [MemberData(nameof(BadProposals))]
    public void A_proposal_referring_to_something_that_does_not_exist_is_rejected(string kind)
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-010", 1);
        var before = workspace.Snapshot("case-010");

        var step = kind switch
        {
            "unknown document" => ProposalStep("case-010", 2, "P-1", "DOC-NOPE", "ORD-010", ("L01", "OL-010-01")),
            "unknown order" => ProposalStep("case-010", 2, "P-1", "DOC-010", "ORD-NOPE", ("L01", "OL-010-01")),
            "unknown order line" => ProposalStep("case-010", 2, "P-1", "DOC-010", "ORD-010", ("L01", "OL-NOPE")),
            "unknown document line" => ProposalStep("case-010", 2, "P-1", "DOC-010", "ORD-010", ("L99", "OL-010-01")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var (exit, output) = workspace.Run(step);

        AssertSomePathStartsWith(AssertRejected(exit, output), "/proposals/0");
        Assert.Equal(before, workspace.Snapshot("case-010"));
    }

    [Fact]
    public void A_proposal_that_maps_a_deterministically_matched_line_elsewhere_is_rejected()
    {
        // R2: "Предложение, противоречащее детерминированному соответствию, отклоняется ошибкой валидации при регистрации."
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var before = workspace.Snapshot("case-001");

        var (exit, output) = workspace.Run(ProposalStep("case-001", 2, "P-1", "DOC-001", "ORD-001", ("L01", "OL-001-02")));

        AssertSomePathStartsWith(AssertRejected(exit, output), "/proposals/0");
        Assert.Equal(before, workspace.Snapshot("case-001"));
    }

    [Fact]
    public void A_proposal_of_another_order_than_the_deterministic_one_is_rejected()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);

        // A second, otherwise identical open order exists, but the document's PO reference determines ORD-001.
        var contextStep = Dataset.Step("case-001", 1);
        contextStep.Remove("document");
        contextStep["step"] = 2;
        var context = contextStep["context"]!.AsObject();
        context["context_version"] = "CTX-001-v2";
        var orders = context["purchase_orders"]!["orders"]!.AsArray();
        var other = orders[0]!.DeepClone().AsObject();
        other["order_id"] = "ORD-001-B";
        other["reference"] = "PO-2026-7999";
        foreach (var line in other["lines"]!.AsArray())
        {
            line!["line_id"] = line["line_id"]!.GetValue<string>().Replace("OL-001", "OL-001-B", StringComparison.Ordinal);
        }

        orders.Add(other);
        var accepted = workspace.RunOk(contextStep);
        Assert.Equal("ORD-001", accepted.GetProperty("matched_order_id").GetString());
        var before = workspace.Snapshot("case-001");

        var (exit, output) = workspace.Run(ProposalStep("case-001", 3, "P-1", "DOC-001", "ORD-001-B"));

        AssertSomePathStartsWith(AssertRejected(exit, output), "/proposals/0");
        Assert.Equal(before, workspace.Snapshot("case-001"));
    }

    [Fact]
    public void A_proposal_id_cannot_be_registered_twice()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-010", 1);
        workspace.RunOk("case-010", 2);
        var before = workspace.Snapshot("case-010");

        var again = Dataset.Step("case-010", 2);
        again["step"] = 3;
        var (exit, output) = workspace.Run(again);

        AssertSomePathStartsWith(AssertRejected(exit, output), "/proposals/0");
        Assert.Equal(before, workspace.Snapshot("case-010"));
    }

    [Fact]
    public void A_confirmation_of_an_unknown_proposal_is_rejected()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-010", 1);
        workspace.RunOk("case-010", 2);
        var before = workspace.Snapshot("case-010");

        var (exit, output) = workspace.Run(ConfirmationStep("case-010", 3, "PROP-NOPE"));

        AssertSomePathStartsWith(AssertRejected(exit, output), "/confirmations/0");
        Assert.Equal(before, workspace.Snapshot("case-010"));
    }

    [Fact]
    public void The_same_proposal_cannot_be_confirmed_twice()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-010", 1);
        workspace.RunOk("case-010", 2);
        workspace.RunOk("case-010", 3);
        var before = workspace.Snapshot("case-010");

        var again = Dataset.Step("case-010", 3);
        again["step"] = 4;
        var (exit, output) = workspace.Run(again);

        AssertSomePathStartsWith(AssertRejected(exit, output), "/confirmations/0");
        Assert.Equal(before, workspace.Snapshot("case-010"));
    }

    // ---- the agent cannot confirm ---------------------------------------------------------------------------------

    [Fact]
    public void The_agent_cannot_register_a_confirmation_but_a_human_can()
    {
        // README: "MCP не предоставляет операцию подтверждения"; R2: "Человек подтверждает ... только через интерфейс человека."
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-010", 1);
        workspace.RunOk("case-010", 2);
        var before = workspace.Snapshot("case-010");
        var step = Dataset.Step("case-010", 3);
        step["confirmations"]![0]!["confirmed_by"] = "Jane Reviewer";
        var confirmation = Dataset.ToElement(step);

        var rejected = Assert.Throws<InputValidationException>(() => workspace.Cases.Apply(confirmation, Actor.Agent));

        Assert.NotEmpty(rejected.Issues);
        Assert.Equal(before, workspace.Snapshot("case-010"));
        Assert.Empty(workspace.Store.Load("case-010")!.Confirmations);

        var view = workspace.Cases.Apply(confirmation, Actor.Human("Jane Reviewer"));

        Assert.Equal(3, view.InputRevision);
        var document = view.Document("DOC-010")!;
        Assert.Equal("clear", document.Current.Result.Decision);
        Assert.Equal("Jane Reviewer", Assert.Single(document.Proposals).Confirmation?.ConfirmedBy);
        var revision = view.Revisions[^1];
        Assert.Equal(("web", "human", "Jane Reviewer"), (revision.Channel, revision.Actor, revision.ActorName));
    }
}
