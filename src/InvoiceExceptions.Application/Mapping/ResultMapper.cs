using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Application.Mapping;

/// <summary>Turns the engine's outcome into the result contract shared by storage, the CLI, MCP and the case page.</summary>
internal static class ResultMapper
{
    public static ResultDto ToResult(Evaluation evaluation, string documentId, string contextVersion, int inputRevision)
    {
        var assessment = evaluation.Assessment;
        return new ResultDto(
            PolicyVersion: CoreV1.PolicyVersion,
            ContextVersion: contextVersion,
            InputRevision: inputRevision,
            DocumentId: documentId,
            ImportDisposition: Wire.Name(evaluation.Disposition),
            OriginalDocumentId: evaluation.OriginalDocumentId,
            ExistingAssessmentRef: evaluation.ExistingAssessmentRef,
            MatchedOrderId: assessment?.MatchedOrderId,
            OrderMatchSource: assessment?.OrderMatchSource is { } source ? Wire.Name(source) : null,
            Lines: assessment is null ? [] : [.. assessment.Lines.Select(Line)],
            Totals: assessment is null
                ? null
                : new TotalsDto(
                    Wire.Amount(assessment.Totals.CalculatedSubtotal),
                    Wire.Amount(assessment.Totals.CalculatedTaxTotal),
                    Wire.Amount(assessment.Totals.CalculatedTotal)),
            Checks: assessment is null ? [] : [.. assessment.Checks.Select(Check)],
            Decision: assessment is null ? null : Wire.Name(assessment.Decision),
            Questions: assessment is null
                ? []
                : [.. assessment.Questions.Select(q => new QuestionDto(Wire.Name(q.RequiredFact), q.TargetObjectIds, q.Text))]);
    }

    /// <summary>A duplicate has no assessment; its result is nothing but the pointer to the original.</summary>
    public static ResultDto DuplicateResult(
        string documentId, string contextVersion, int inputRevision, string originalDocumentId, string? existingAssessmentRef) =>
        ToResult(Evaluation.Duplicate(originalDocumentId, existingAssessmentRef), documentId, contextVersion, inputRevision);

    private static LineResultDto Line(LineResult line) => new(
        line.DocumentLineId,
        line.OrderLineId,
        line.MatchSource is { } source ? Wire.Name(source) : null,
        Wire.Amount(line.CalculatedNet),
        Wire.Amount(line.CalculatedTax),
        Wire.Number(line.Ordered),
        Wire.Number(line.Received),
        Wire.Number(line.PreviouslyInvoiced),
        Wire.Number(line.AvailableToInvoice),
        Wire.Number(line.RemainingOnOrder));

    private static CheckDto Check(CheckResult check) => new(
        Wire.Name(check.Code),
        check.ObjectId,
        Wire.Name(check.Status),
        check.Severity is { } severity ? Wire.Name(severity) : null,
        check.DependsOn is { } dependsOn ? Wire.Name(dependsOn) : null,
        check.Actual,
        check.Expected,
        check.EvidenceRefs)
    {
        Explanation = check.Explanation,
    };
}
