using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>
/// R6 for a document that is sent again. An earlier registered version of the very same document is the same
/// invoice by definition (my reading of "re-import of the same document gives duplicate, of a modified one -
/// conflict"); what it is compared on is everything R6 looks at, the business key included.
/// </summary>
public class EarlierVersionTests
{
    private static Evaluation EvaluateAgainstEarlierVersion(Func<InvoiceDocument, InvoiceDocument> earlierReading, string? assessmentRef = "ASM-1")
    {
        var scenario = Scenario.HappyPath();
        var current = scenario.BuiltDocument();
        var input = scenario.Input();
        return InvoiceEvaluator.Evaluate(input with
        {
            EarlierDocuments = [new EarlierDocument(earlierReading(current), assessmentRef, SameDocument: true)],
        });
    }

    [Fact]
    public void Sent_again_unchanged_is_a_duplicate_of_itself_pointing_at_its_conclusion()
    {
        var evaluation = EvaluateAgainstEarlierVersion(d => d);

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Equal(Scenario.HappyPath().BuiltDocument().DocumentId, evaluation.OriginalDocumentId);
        Assert.Equal("ASM-1", evaluation.ExistingAssessmentRef);
        Assert.Null(evaluation.Assessment);
    }

    [Fact]
    public void Sent_again_with_only_a_description_changed_is_still_a_duplicate()
    {
        // Descriptions are not among the facts R6 compares.
        var evaluation = EvaluateAgainstEarlierVersion(d => d with
        {
            Lines = [.. d.Lines.Select(l => l with { Description = l.Description + " (revised wording)" })],
        });

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
    }

    [Fact]
    public void Sent_again_with_a_compared_fact_changed_is_a_conflict_that_blocks()
    {
        var evaluation = EvaluateAgainstEarlierVersion(d => d with { DueDate = "2031-01-01" });

        Assert.Equal(ImportDisposition.Conflict, evaluation.Disposition);
        Assert.Equal(Scenario.HappyPath().BuiltDocument().DocumentId, evaluation.OriginalDocumentId);
        var conflict = evaluation.Assessment!.Checks.Single(c => c.Code == CheckCode.DocumentConflict);
        Assert.Equal(CheckStatus.Fail, conflict.Status);
        Assert.Equal(Severity.Blocked, conflict.Severity);
        Assert.Equal(Decision.Blocked, evaluation.Assessment.Decision);
    }

    [Fact]
    public void Sent_again_under_another_invoice_number_is_a_conflict_not_a_new_invoice()
    {
        // Between two different documents another number means another invoice. Between two readings of one
        // document it means the number was read differently - which is exactly what must not pass unnoticed.
        var evaluation = EvaluateAgainstEarlierVersion(d => d with { Number = d.Number + "-A" });

        Assert.Equal(ImportDisposition.Conflict, evaluation.Disposition);
        Assert.Equal(Decision.Blocked, evaluation.Assessment!.Decision);
    }

    [Fact]
    public void Another_document_with_another_number_is_simply_a_new_invoice()
    {
        var scenario = Scenario.HappyPath();
        var other = scenario.BuiltDocument() with { DocumentId = "DOC-OTHER", Number = "INV-OTHER-1" };

        var evaluation = InvoiceEvaluator.Evaluate(scenario.Input() with
        {
            EarlierDocuments = [new EarlierDocument(other, "ASM-9")],
        });

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
    }
}
