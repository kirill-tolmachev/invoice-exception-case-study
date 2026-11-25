using InvoiceExceptions.Application.Contracts;

namespace InvoiceExceptions.Application.Cases;

/// <summary>Everything a reader - the agent, the case page, the runner adapter - may want to know about a case.</summary>
public sealed record CaseView(
    string CaseId,
    string PolicyVersion,
    string AsOf,
    int InputRevision,
    string? ContextVersion,
    ContextDto? Context,
    IReadOnlyList<DocumentView> Documents,
    IReadOnlyList<RevisionView> Revisions)
{
    public DocumentView? Document(string documentId) => Documents.FirstOrDefault(d => d.DocumentId == documentId);
}

/// <param name="Current">The outcome at the current input revision.</param>
/// <param name="History">Every outcome ever recorded for the document, oldest first; the last one is <paramref name="Current"/>.</param>
public sealed record DocumentView(
    string DocumentId,
    string ContentSha256,
    string? SourcePath,
    int ImportedAtRevision,
    int FactsRevision,
    FactsDto Facts,
    EvaluationView Current,
    IReadOnlyList<EvaluationView> History,
    IReadOnlyList<ProposalView> Proposals);

/// <param name="AssessmentId">Null for a duplicate: a duplicate is recorded, but it gets no assessment.</param>
public sealed record EvaluationView(int InputRevision, string? AssessmentId, ResultDto Result);

public sealed record ProposalView(
    string ProposalId,
    string DocumentId,
    string OrderId,
    IReadOnlyList<LineMatchDto> LineMatches,
    string? Rationale,
    int RegisteredAtRevision,
    ConfirmationView? Confirmation);

public sealed record ConfirmationView(string ConfirmedBy, int Revision);

public sealed record RevisionView(
    int Revision,
    string Channel,
    string Actor,
    string? ActorName,
    string Summary,
    DateTimeOffset RecordedAt);
