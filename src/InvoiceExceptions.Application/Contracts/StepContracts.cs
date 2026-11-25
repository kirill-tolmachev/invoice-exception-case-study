using System.Text.Json.Serialization;

namespace InvoiceExceptions.Application.Contracts;

// One change of input, in the shape of dataset/schemas/step-input.schema.json. Every channel - the CLI adapter,
// the MCP tools and the case page - reduces its request to this contract before it reaches the use case.

public sealed record StepInputDto(
    string SchemaVersion,
    string CaseId,
    int Step,
    string AsOf,
    string PolicyRef,
    ContextDto? Context,
    DocumentInputDto? Document,
    IReadOnlyList<ProposalInputDto>? Proposals,
    IReadOnlyList<ConfirmationInputDto>? Confirmations);

public sealed record DocumentInputDto(string ContentSha256, FactsDto Facts);

public sealed record ProposalInputDto(
    string ProposalId,
    string Actor,
    string DocumentId,
    string OrderId,
    IReadOnlyList<LineMatchDto> LineMatches);

public sealed record LineMatchDto(string DocumentLineId, string OrderLineId);

public sealed record ConfirmationInputDto(string ProposalId, string Actor, string ConfirmedBy);

// The result for one document at one input revision. Stored as the assessment payload and returned by every channel.

public sealed record ResultDto(
    string PolicyVersion,
    string ContextVersion,
    int InputRevision,
    string DocumentId,
    string ImportDisposition,
    string? OriginalDocumentId,
    string? ExistingAssessmentRef,
    string? MatchedOrderId,
    string? OrderMatchSource,
    IReadOnlyList<LineResultDto> Lines,
    TotalsDto? Totals,
    IReadOnlyList<CheckDto> Checks,
    string? Decision,
    IReadOnlyList<QuestionDto> Questions);

public sealed record LineResultDto(
    string DocumentLineId,
    string? OrderLineId,
    string? MatchSource,
    string CalculatedNet,
    string CalculatedTax,
    string? Ordered,
    string? Received,
    string? PreviouslyInvoiced,
    string? AvailableToInvoice,
    string? RemainingOnOrder);

public sealed record TotalsDto(string CalculatedSubtotal, string CalculatedTaxTotal, string CalculatedTotal);

public sealed record CheckDto(
    string Code,
    string ObjectId,
    string Status,
    string? Severity,
    string? DependsOn,
    IReadOnlyDictionary<string, string?> Actual,
    IReadOnlyDictionary<string, string?> Expected,
    IReadOnlyList<string> EvidenceRefs)
{
    /// <summary>
    /// Human-readable reason. Part of the stored result and of what the agent and the page see;
    /// left out of the runner output, whose schema does not allow extra properties.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Explanation { get; init; }
}

public sealed record QuestionDto(string RequiredFact, IReadOnlyList<string> TargetObjectIds, string Text);

/// <summary>Adapter output for the acceptance runner: dataset/schemas/step-output.schema.json, "result" branch.</summary>
public sealed record StepOutputDto(
    string SchemaVersion,
    string CaseId,
    int Step,
    string PolicyVersion,
    string ContextVersion,
    int InputRevision,
    string DocumentId,
    string ImportDisposition,
    string? OriginalDocumentId,
    string? ExistingAssessmentRef,
    string? MatchedOrderId,
    string? OrderMatchSource,
    IReadOnlyList<LineResultDto> Lines,
    TotalsDto? Totals,
    IReadOnlyList<CheckDto> Checks,
    string? Decision,
    IReadOnlyList<QuestionDto> Questions);

/// <summary>Adapter output for a rejected input: dataset/schemas/step-output.schema.json, "validation_error" branch.</summary>
public sealed record ValidationErrorDto(ValidationErrorBodyDto Error);

public sealed record ValidationErrorBodyDto(string Code, IReadOnlyList<ValidationDetailDto> Details);

public sealed record ValidationDetailDto(string Path, string Message);
