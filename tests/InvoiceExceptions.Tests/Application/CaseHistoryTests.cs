using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Contracts;

namespace InvoiceExceptions.Tests.Application;

/// <summary>
/// BUSINESS_RULES "Вычисляемые значения": a conclusion refers to policy_version, context_version and input_revision;
/// input_revision grows on any change of facts, proposals, confirmations or context; earlier conclusions are kept.
/// README §3: a new conclusion is stored separately, the earlier one is not overwritten; data survives a restart.
/// </summary>
public sealed class CaseHistoryTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value, WireJson.Options);

    private static EvaluationView Current(CaseWorkspace workspace, string caseId, string documentId) =>
        workspace.Cases.Get(caseId)!.Document(documentId)!.Current;

    // ---- 3. history is never rewritten ----------------------------------------------------------------------------

    [Fact]
    public void Case_010_keeps_all_three_conclusions_exactly_as_they_were_drawn()
    {
        using var workspace = new CaseWorkspace();
        var readAtTheTime = new List<string>();
        for (var step = 1; step <= 3; step++)
        {
            workspace.RunOk("case-010", step);
            readAtTheTime.Add(Json(Current(workspace, "case-010", "DOC-010")));
        }

        var view = workspace.Cases.Get("case-010")!;
        Assert.Equal(3, view.InputRevision);
        Assert.Equal([1, 2, 3], view.Revisions.Select(r => r.Revision));

        var history = view.Document("DOC-010")!.History;
        Assert.Equal(3, history.Count);
        Assert.Equal(["needs_information", "needs_review", "clear"], history.Select(e => e.Result.Decision));
        Assert.Equal(3, history.Select(e => e.AssessmentId).Distinct().Count());
        Assert.All(history, e => Assert.False(string.IsNullOrEmpty(e.AssessmentId)));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(readAtTheTime[i], Json(history[i]));
            Assert.Equal(i + 1, history[i].InputRevision);
            Assert.Equal(i + 1, history[i].Result.InputRevision);
            Assert.Equal("CTX-010-v1", history[i].Result.ContextVersion);
            Assert.Equal("core-v1", history[i].Result.PolicyVersion);
        }

        // The same, straight from the store: three assessments, each bound to its revision and context version.
        var stored = workspace.Store.Load("case-010")!.Evaluations.Where(e => e.DocumentId == "DOC-010").ToList();
        Assert.Equal([1, 2, 3], stored.Select(e => e.Revision));
        Assert.All(stored, e =>
        {
            Assert.NotNull(e.Assessment);
            Assert.Equal("CTX-010-v1", e.Assessment!.ContextVersion);
            Assert.Equal(e.Revision, JsonDocument.Parse(e.Assessment.ResultJson).RootElement.GetProperty("input_revision").GetInt32());
        });
        Assert.Equal(["needs_information", "needs_review", "clear"], stored.Select(e => e.Assessment!.Decision));
    }

    // ---- 4. new context -> new revision and new conclusion ------------------------------------------------------------

    [Fact]
    public void A_new_context_gives_a_new_revision_and_a_new_conclusion_and_keeps_the_first()
    {
        using var workspace = new CaseWorkspace();
        var first = workspace.RunOk("case-001", 1);
        Assert.Equal("clear", first.GetProperty("decision").GetString());
        var firstConclusion = Json(Current(workspace, "case-001", "DOC-001"));

        var step = Dataset.Step("case-001", 1);
        step.Remove("document");
        step["step"] = 2;
        var context = step["context"]!.AsObject();
        context["context_version"] = "CTX-001-v2";
        context["goods_receipts"]!["availability"] = "unavailable";
        context["goods_receipts"]!["receipts"] = new JsonArray();

        var output = workspace.RunOk(step);

        Assert.Empty(OutputSchema.Problems(output));
        Assert.Equal("CTX-001-v2", output.GetProperty("context_version").GetString());
        Assert.True(output.GetProperty("input_revision").GetInt32() > first.GetProperty("input_revision").GetInt32());
        Assert.Equal("needs_information", output.GetProperty("decision").GetString());

        var lineIds = output.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("document_line_id").GetString()!).Order().ToList();
        Assert.Equal(["L01", "L02", "L03", "L04"], lineIds);
        var questions = output.GetProperty("questions").EnumerateArray().ToList();
        Assert.All(questions, q => Assert.Equal("goods_receipts", q.GetProperty("required_fact").GetString()));
        Assert.Equal(
            lineIds,
            questions.Select(q => Assert.Single(q.GetProperty("target_object_ids").EnumerateArray()).GetString()!).Order().ToList());

        foreach (var line in output.GetProperty("lines").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty("received").ValueKind);
            Assert.Equal(JsonValueKind.Null, line.GetProperty("available_to_invoice").ValueKind);
        }

        Assert.All(
            output.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("code").GetString() == "QTY_RECEIVED"),
            c =>
            {
                Assert.Equal("unknown", c.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, c.GetProperty("depends_on").ValueKind);
            });

        var history = workspace.Cases.Get("case-001")!.Document("DOC-001")!.History;
        Assert.Equal(2, history.Count);
        Assert.Equal(firstConclusion, Json(history[0]));
        Assert.Equal("CTX-001-v1", history[0].Result.ContextVersion);
        Assert.Equal("CTX-001-v2", history[1].Result.ContextVersion);
        Assert.NotEqual(history[0].AssessmentId, history[1].AssessmentId);
    }

    // ---- 5. corrected facts ----------------------------------------------------------------------------------------

    [Fact]
    public void The_same_document_sent_again_unchanged_is_a_duplicate_and_changes_nothing()
    {
        using var workspace = new CaseWorkspace();
        var first = workspace.RunOk("case-001", 1);
        var before = Json(workspace.Store.Load("case-001")!.Documents);
        var conclusionsBefore = Json(workspace.Store.Load("case-001")!.Evaluations);

        // Exactly the same step again: same context, same document.
        var again = Dataset.Step("case-001", 1);
        again["step"] = 2;
        var output = workspace.RunOk(again);

        Assert.Equal("duplicate", output.GetProperty("import_disposition").GetString());
        Assert.Equal("DOC-001", output.GetProperty("original_document_id").GetString());
        Assert.Equal("case-001/DOC-001/r1", output.GetProperty("existing_assessment_ref").GetString());
        Assert.Equal(JsonValueKind.Null, output.GetProperty("decision").ValueKind);
        Assert.Empty(output.GetProperty("checks").EnumerateArray());
        Assert.True(output.GetProperty("input_revision").GetInt32() > first.GetProperty("input_revision").GetInt32());

        // "No new conclusion is created, history does not change": nothing about the document was stored.
        var stored = workspace.Store.Load("case-001")!;
        Assert.Equal(before, Json(stored.Documents));
        Assert.Equal(conclusionsBefore, Json(stored.Evaluations));
        Assert.Single(stored.Contexts);
        Assert.Equal("clear", workspace.Cases.Get("case-001")!.Document("DOC-001")!.Current.Result.Decision);
    }

    [Fact]
    public void The_same_document_sent_again_with_different_facts_is_a_conflict_and_the_original_is_kept()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var firstConclusion = Json(Current(workspace, "case-001", "DOC-001"));

        var step = Dataset.Step("case-001", 1);
        step.Remove("context");
        step["step"] = 2;
        step["document"]!["facts"]!["due_date"] = "2026-11-30";

        var output = workspace.RunOk(step);

        // Registered facts are never replaced: a different reading of the same document is a conflict, not a correction.
        Assert.Equal(2, output.GetProperty("input_revision").GetInt32());
        Assert.Equal("DOC-001", output.GetProperty("document_id").GetString());
        Assert.Equal("conflict", output.GetProperty("import_disposition").GetString());
        Assert.Equal("DOC-001", output.GetProperty("original_document_id").GetString());
        var conflict = output.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "DOCUMENT_CONFLICT");
        Assert.Equal("fail", conflict.GetProperty("status").GetString());
        Assert.Equal("blocked", conflict.GetProperty("severity").GetString());
        Assert.Equal("blocked", output.GetProperty("decision").GetString());

        // The original version and its conclusion are still there, untouched.
        var document = workspace.Cases.Get("case-001")!.Document("DOC-001")!;
        Assert.Single(workspace.Cases.Get("case-001")!.Documents);
        Assert.Equal(2, document.History.Count);
        Assert.Equal(firstConclusion, Json(document.History[0]));
        Assert.Equal("clear", document.History[0].Result.Decision);

        var stored = workspace.Store.Load("case-001")!.Documents.Where(d => d.DocumentId == "DOC-001").ToList();
        Assert.Equal([1, 2], stored.Select(d => d.Revision));
        Assert.Contains("2026-11-25", stored[0].FactsJson, StringComparison.Ordinal);
        Assert.Contains("2026-11-30", stored[1].FactsJson, StringComparison.Ordinal);

        // The conflict does not go away by itself: a later change re-evaluates the new version against the original.
        var third = Dataset.Step("case-001", 1);
        third.Remove("document");
        third["step"] = 3;
        third["context"]!["context_version"] = "CTX-001-v2";
        Assert.Equal("conflict", workspace.RunOk(third).GetProperty("import_disposition").GetString());
    }

    [Fact]
    public void A_reading_that_changes_the_invoice_number_of_a_registered_document_is_still_a_conflict()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-004", 1);

        // Another number would be another business key - but it is the same document, so it cannot become a new invoice.
        var step = Dataset.Step("case-004", 1);
        step.Remove("context");
        step["step"] = 2;
        step["document"]!["facts"]!["number"] = "INV-2026-4104-A";
        step["document"]!["facts"]!["lines"]![0]!["unit_net_price"] = "12.50";
        step["document"]!["facts"]!["lines"]![0]!["declared_net_amount"] = "1000.00";
        step["document"]!["facts"]!["lines"]![0]!["declared_tax_amount"] = "200.00";
        step["document"]!["facts"]!["declared_subtotal"] = "1115.20";
        step["document"]!["facts"]!["declared_tax_total"] = "223.04";
        step["document"]!["facts"]!["declared_total"] = "1338.24";

        var output = workspace.RunOk(step);

        Assert.Equal("conflict", output.GetProperty("import_disposition").GetString());
        Assert.Equal("DOC-004", output.GetProperty("original_document_id").GetString());
        Assert.Equal("blocked", output.GetProperty("decision").GetString());
    }

    // ---- 6. duplicate and conflict inside one case ---------------------------------------------------------------------

    /// <summary>Case-001's document under another document_id (evidence included), as a document-only step.</summary>
    private static JsonObject Case001DocumentAs(string documentId, int step)
    {
        var input = Dataset.Step("case-001", 1);
        input.Remove("context");
        input["step"] = step;
        var facts = input["document"]!["facts"]!.AsObject();
        facts["document_id"] = documentId;
        foreach (var evidence in facts["evidence"]!.AsArray())
        {
            evidence!["document_id"] = documentId;
        }

        return input;
    }

    [Fact]
    public void The_same_document_under_another_id_is_a_duplicate_and_a_changed_one_a_conflict()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var firstConclusion = Json(Current(workspace, "case-001", "DOC-001"));

        // The same facts again under another document_id: duplicate.
        var duplicate = workspace.RunOk(Case001DocumentAs("DOC-001-COPY", 2));

        Assert.Empty(OutputSchema.Problems(duplicate));
        Assert.Equal("DOC-001-COPY", duplicate.GetProperty("document_id").GetString());
        Assert.Equal("duplicate", duplicate.GetProperty("import_disposition").GetString());
        Assert.Equal("DOC-001", duplicate.GetProperty("original_document_id").GetString());
        Assert.Equal(JsonValueKind.Null, duplicate.GetProperty("decision").ValueKind);
        Assert.Empty(duplicate.GetProperty("checks").EnumerateArray());
        Assert.Empty(duplicate.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, duplicate.GetProperty("totals").ValueKind);

        var reference = duplicate.GetProperty("existing_assessment_ref").GetString();
        var original = workspace.Cases.Get("case-001")!.Document("DOC-001")!;
        Assert.Contains(reference, original.History.Select(e => e.AssessmentId));
        Assert.Null(workspace.Cases.Get("case-001")!.Document("DOC-001-COPY")!.Current.AssessmentId);

        // Same number, one line changed (11 instead of 12 drill bits, arithmetic kept consistent): conflict.
        var changed = Case001DocumentAs("DOC-001-V2", 3);
        var facts = changed["document"]!["facts"]!.AsObject();
        var line = facts["lines"]![3]!;
        line["quantity"] = "11";
        line["declared_net_amount"] = "80.85";
        line["declared_tax_amount"] = "16.17";
        facts["declared_subtotal"] = "1837.85";
        facts["declared_tax_total"] = "367.57";
        facts["declared_total"] = "2205.42";

        var conflict = workspace.RunOk(changed);

        Assert.Empty(OutputSchema.Problems(conflict));
        Assert.Equal("DOC-001-V2", conflict.GetProperty("document_id").GetString());
        Assert.Equal("conflict", conflict.GetProperty("import_disposition").GetString());
        Assert.Equal("DOC-001", conflict.GetProperty("original_document_id").GetString());
        Assert.Equal("blocked", conflict.GetProperty("decision").GetString());
        var conflictCheck = conflict.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "DOCUMENT_CONFLICT");
        Assert.Equal(("fail", "blocked"), (conflictCheck.GetProperty("status").GetString(), conflictCheck.GetProperty("severity").GetString()));
        Assert.Equal(7 + 5 * 4, conflict.GetProperty("checks").GetArrayLength());
        // Everything else about the changed document is in order: only the conflict blocks it.
        Assert.All(
            conflict.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("code").GetString() != "DOCUMENT_CONFLICT"),
            c => Assert.Equal("pass", c.GetProperty("status").GetString()));

        // The original is neither overwritten nor reclassified.
        var view = workspace.Cases.Get("case-001")!;
        var first = view.Document("DOC-001")!;
        Assert.Equal(firstConclusion, Json(first.History[0]));
        Assert.All(first.History, e =>
        {
            Assert.Equal("new", e.Result.ImportDisposition);
            Assert.Equal("clear", e.Result.Decision);
        });
        Assert.Equal("12", first.Facts.Lines[3].Quantity);
        Assert.Equal(3, view.Documents.Count);
    }

    [Fact]
    public void A_duplicate_import_creates_no_new_conclusion_and_refers_to_one_that_already_existed()
    {
        // R6: "Ключ совпал, факты совпали | duplicate | original_document_id и ссылка на существующее заключение,
        // если оно есть. Новое заключение не создаётся, история не меняется."
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-001", 1);
        var assessmentsBefore = workspace.Store.Load("case-001")!.Evaluations
            .Where(e => e.Assessment is not null).Select(e => e.Assessment!.AssessmentId).ToList();

        var duplicate = workspace.RunOk(Case001DocumentAs("DOC-001-COPY", 2));
        Assert.Equal("duplicate", duplicate.GetProperty("import_disposition").GetString());

        var assessmentsAfter = workspace.Store.Load("case-001")!.Evaluations
            .Where(e => e.Assessment is not null).Select(e => e.Assessment!.AssessmentId).ToList();
        Assert.Contains(duplicate.GetProperty("existing_assessment_ref").GetString(), assessmentsBefore);
        Assert.Equal(assessmentsBefore, assessmentsAfter);
    }

    // ---- 7. survives a restart -------------------------------------------------------------------------------------

    [Fact]
    public void Everything_written_is_visible_after_a_restart_and_the_revision_sequence_continues()
    {
        using var workspace = new CaseWorkspace();
        workspace.RunOk("case-010", 1);
        workspace.RunOk("case-010", 2);
        var before = Json(workspace.Cases.Get("case-010")!);
        var storedBefore = workspace.Snapshot("case-010");

        using var restarted = workspace.Reopen();

        Assert.Equal(["case-010"], restarted.Cases.ListCaseIds());
        Assert.Equal(before, Json(restarted.Cases.Get("case-010")!));
        Assert.Equal(storedBefore, restarted.Snapshot("case-010"));
        var proposal = Assert.Single(restarted.Cases.Get("case-010")!.Document("DOC-010")!.Proposals);
        Assert.Equal("PROP-010-01", proposal.ProposalId);
        Assert.Null(proposal.Confirmation);

        var output = restarted.RunOk("case-010", 3);

        Assert.Equal(3, output.GetProperty("input_revision").GetInt32());
        Assert.Equal("clear", output.GetProperty("decision").GetString());
        Assert.Empty(StepComparer.Compare(Dataset.Expected("case-010", 3), output));
        var history = restarted.Cases.Get("case-010")!.Document("DOC-010")!.History;
        Assert.Equal(["needs_information", "needs_review", "clear"], history.Select(e => e.Result.Decision));
    }
}
