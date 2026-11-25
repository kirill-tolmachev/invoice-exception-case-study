using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Application.Storage;

/// <summary>
/// Durable, append-only record of a case. Nothing is ever updated or deleted: a change of input is a new
/// revision, a new conclusion is a new row. The store keeps payloads verbatim and knows nothing about the rules.
/// </summary>
public interface ICaseStore
{
    /// <summary>Ids of all known cases, in ordinal order.</summary>
    IReadOnlyList<string> ListCaseIds();

    /// <summary>Everything recorded for the case, or null if there is no such case.</summary>
    CaseRecord? Load(string caseId);

    /// <summary>
    /// Appends one input revision with everything that belongs to it, atomically: either all of it is stored or none.
    /// </summary>
    /// <exception cref="RevisionConflictException">
    /// The revision is not exactly the successor of the latest stored one (someone else got there first),
    /// or the commit creates a case that already exists.
    /// </exception>
    void Append(CaseCommit commit);
}

/// <summary>One atomic unit of change: a new input revision and the conclusions drawn from it.</summary>
/// <param name="NewCase">Set if and only if this commit creates the case; its revision is then 1.</param>
/// <param name="Context">A new context snapshot, if the context changed in this revision.</param>
/// <param name="Documents">New documents, or new versions of the facts of existing ones.</param>
/// <param name="Evaluations">The engine's outcome for every document of the case at this revision.</param>
public sealed record CaseCommit(
    string CaseId,
    CaseHeader? NewCase,
    RevisionRecord Revision,
    ContextRecord? Context,
    IReadOnlyList<DocumentRecord> Documents,
    IReadOnlyList<ProposalRecord> Proposals,
    IReadOnlyList<ConfirmationRecord> Confirmations,
    IReadOnlyList<EvaluationRecord> Evaluations);

/// <summary>Everything stored for one case. All lists are ordered by revision, then by insertion order.</summary>
public sealed record CaseRecord(
    CaseHeader Header,
    IReadOnlyList<RevisionRecord> Revisions,
    IReadOnlyList<ContextRecord> Contexts,
    IReadOnlyList<DocumentRecord> Documents,
    IReadOnlyList<ProposalRecord> Proposals,
    IReadOnlyList<ConfirmationRecord> Confirmations,
    IReadOnlyList<EvaluationRecord> Evaluations);

public sealed record CaseHeader(string CaseId, string PolicyVersion, string AsOf);

/// <summary>Who changed the input, through which channel, and what changed. The timestamp is audit data only; no rule reads it.</summary>
/// <param name="Channel">"cli", "mcp" or "web".</param>
/// <param name="Actor">"agent", "human" or "runner".</param>
/// <param name="ActorName">Free text naming the actor, e.g. the reviewer who confirmed. May be null.</param>
public sealed record RevisionRecord(
    int Revision,
    string Channel,
    string Actor,
    string? ActorName,
    string Summary,
    DateTimeOffset RecordedAt);

/// <summary>A full context snapshot as received (the JSON of the step-input "context" object).</summary>
public sealed record ContextRecord(int Revision, string ContextVersion, string PayloadJson);

/// <summary>
/// One version of the facts of a document as received (the JSON of the extraction schema), including evidence:
/// document_id, page and verbatim excerpt for every fact.
/// </summary>
/// <param name="SourcePath">Where the PDF was read from, when known. Informational.</param>
public sealed record DocumentRecord(
    int Revision,
    string DocumentId,
    string ContentSha256,
    string FactsJson,
    string? SourcePath);

/// <summary>An order/line correspondence proposed by the agent.</summary>
/// <param name="Rationale">The agent's explanation for the human who has to confirm it. May be null.</param>
public sealed record ProposalRecord(
    int Revision,
    string ProposalId,
    string DocumentId,
    string OrderId,
    IReadOnlyList<LineMatch> LineMatches,
    string? Rationale);

/// <summary>A human confirmed one specific proposal.</summary>
public sealed record ConfirmationRecord(int Revision, string ProposalId, string ConfirmedBy);

/// <summary>
/// How a document was classified at a revision. A duplicate has no assessment; every other outcome has exactly one.
/// </summary>
/// <param name="ImportDisposition">"new", "duplicate" or "conflict".</param>
public sealed record EvaluationRecord(
    int Revision,
    string DocumentId,
    string ImportDisposition,
    string? OriginalDocumentId,
    string? ExistingAssessmentRef,
    AssessmentRecord? Assessment);

/// <summary>A stored conclusion. It is bound to the input revision it was computed from and is never overwritten.</summary>
/// <param name="AssessmentId">Stable reference, unique across the store.</param>
/// <param name="Decision">"clear", "needs_information", "needs_review" or "blocked".</param>
/// <param name="ResultJson">The full result: lines, totals, checks, questions.</param>
public sealed record AssessmentRecord(
    string AssessmentId,
    string PolicyVersion,
    string ContextVersion,
    string Decision,
    string ResultJson);

public sealed class RevisionConflictException(string message) : Exception(message);
