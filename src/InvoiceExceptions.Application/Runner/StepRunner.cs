using System.Text.Json;
using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Validation;

namespace InvoiceExceptions.Application.Runner;

/// <summary>What the adapter process should do: write <paramref name="OutputJson"/> to stdout and exit with <paramref name="ExitCode"/>.</summary>
public sealed record StepRunResult(int ExitCode, string OutputJson);

/// <summary>
/// The adapter to the acceptance-test contract (docs/EVALUATION.md): one JSON step in, one JSON result out.
/// It adds nothing of its own - it calls <see cref="CaseService.Apply"/> and reshapes the answer.
/// </summary>
public sealed class StepRunner(CaseService cases)
{
    public const int ExitResult = 0;
    public const int ExitValidationError = 2;

    public StepRunResult Run(string inputJson)
    {
        JsonElement input;
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            input = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return Rejected([new ValidationIssue(string.Empty, $"The input is not a JSON document: {e.Message}")]);
        }

        try
        {
            var view = cases.Apply(input, Actor.Runner, requireDocument: true);
            var step = WireJson.Deserialize<StepInputDto>(input);
            var result = Subject(view, step).Current.Result;

            return new StepRunResult(ExitResult, WireJson.Serialize(new StepOutputDto(
                SchemaVersion: step.SchemaVersion,
                CaseId: step.CaseId,
                Step: step.Step,
                PolicyVersion: result.PolicyVersion,
                ContextVersion: result.ContextVersion,
                InputRevision: result.InputRevision,
                DocumentId: result.DocumentId,
                ImportDisposition: result.ImportDisposition,
                OriginalDocumentId: result.OriginalDocumentId,
                ExistingAssessmentRef: result.ExistingAssessmentRef,
                MatchedOrderId: result.MatchedOrderId,
                OrderMatchSource: result.OrderMatchSource,
                Lines: result.Lines,
                Totals: result.Totals,
                // The runner schema allows no extra properties on a check.
                Checks: [.. result.Checks.Select(check => check with { Explanation = null })],
                Decision: result.Decision,
                Questions: result.Questions)));
        }
        catch (InputValidationException e)
        {
            return Rejected(e.Issues);
        }
    }

    /// <summary>
    /// The document the step is about: the one it imports, else the one its proposals or confirmations refer to,
    /// else the document imported last.
    /// </summary>
    private static DocumentView Subject(CaseView view, StepInputDto step)
    {
        var documentId = step.Document?.Facts.DocumentId
            ?? step.Confirmations?
                .Select(c => view.Documents.FirstOrDefault(d => d.Proposals.Any(p => p.ProposalId == c.ProposalId))?.DocumentId)
                .LastOrDefault(id => id is not null)
            ?? step.Proposals?.LastOrDefault()?.DocumentId;

        return (documentId is null ? null : view.Document(documentId)) ?? view.Documents[^1];
    }

    private static StepRunResult Rejected(IReadOnlyList<ValidationIssue> issues) =>
        new(ExitValidationError, WireJson.Serialize(new ValidationErrorDto(new ValidationErrorBodyDto(
            "VALIDATION_ERROR",
            [.. issues.Select(issue => new ValidationDetailDto(issue.Path, issue.Message))]))));
}
