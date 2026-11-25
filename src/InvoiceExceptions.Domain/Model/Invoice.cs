namespace InvoiceExceptions.Domain.Model;

/// <summary>
/// Facts of one invoice as printed in the document. Absent values are null; nothing is filled in from the directories.
/// Only the fields the rules need are kept here; evidence and free text stay in the stored payload.
/// </summary>
public sealed record InvoiceDocument(
    string DocumentId,
    string Number,
    string IssueDate,
    string? DueDate,
    string? Currency,
    string? PurchaseOrderReference,
    Party Supplier,
    Party Buyer,
    string? Iban,
    IReadOnlyList<InvoiceLine> Lines,
    decimal DeclaredSubtotal,
    decimal DeclaredTaxTotal,
    decimal DeclaredTotal);

public sealed record Party(string? LegalName, string? RegistrationId);

public sealed record InvoiceLine(
    string LineId,
    string Description,
    string? Sku,
    decimal Quantity,
    string Unit,
    decimal UnitNetPrice,
    decimal DeclaredNetAmount,
    decimal TaxRate,
    decimal? DeclaredTaxAmount);

/// <summary>
/// An order/line correspondence suggested by the agent. It never decides anything on its own:
/// until a human confirms it, the checks that rest on it fail with needs_review.
/// </summary>
public sealed record MatchProposal(
    string ProposalId,
    string DocumentId,
    string OrderId,
    IReadOnlyList<LineMatch> LineMatches,
    bool ConfirmedByHuman);

public sealed record LineMatch(string DocumentLineId, string OrderLineId);

/// <summary>A document imported into the same case before the one being evaluated (R6 looks at these too).</summary>
/// <param name="AssessmentRef">Reference to the conclusion that stands for that document, if it has one.</param>
/// <param name="SameDocument">
/// True when this is an earlier registered version of the very document being evaluated. Registered facts are never
/// replaced: a later reading of the same document is the same invoice by definition, whatever number it now carries.
/// </param>
public sealed record EarlierDocument(InvoiceDocument Document, string? AssessmentRef, bool SameDocument = false);

/// <summary>
/// Everything the engine is allowed to look at. Same input, same result.
/// <paramref name="Proposals"/> are in registration order, oldest first.
/// </summary>
public sealed record EvaluationInput(
    ContextSnapshot Context,
    InvoiceDocument Document,
    IReadOnlyList<EarlierDocument> EarlierDocuments,
    IReadOnlyList<MatchProposal> Proposals);
