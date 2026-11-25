using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Model;

public enum CheckCode
{
    DocumentConflict,
    SupplierIdentity,
    BuyerIdentity,
    OrderMatch,
    CurrencyMatch,
    LineMatch,
    QtyReceived,
    QtyOrderRemaining,
    UnitPrice,
    LineArithmetic,
    TotalsArithmetic,
    BankAccount,
}

public enum CheckStatus { Pass, Fail, Unknown, NotApplicable }

public enum Severity { Blocked, NeedsReview }

/// <summary>Declared in order of strictness (clear &lt; needs_review &lt; needs_information &lt; blocked).</summary>
public enum Decision { Clear, NeedsReview, NeedsInformation, Blocked }

public enum MatchSource { Deterministic, AgentProposal, HumanConfirmation }

public enum ImportDisposition { New, Duplicate, Conflict }

public enum RequiredFact
{
    SupplierRegistrationId,
    BuyerRegistrationId,
    DocumentCurrency,
    OrderReference,
    LineMatch,
    GoodsReceipts,
    BankAccount,
}

/// <summary>
/// Outcome of one check for one object (the document or one of its lines).
/// The factory methods are the only way to build one, which keeps the status/severity/depends_on invariants in one place.
/// </summary>
public sealed record CheckResult
{
    private static readonly IReadOnlyDictionary<string, string?> NoValues = new Dictionary<string, string?>();

    private CheckResult(CheckCode code, string objectId, CheckStatus status, Severity? severity, CheckCode? dependsOn, string explanation)
    {
        Code = code;
        ObjectId = objectId;
        Status = status;
        Severity = severity;
        DependsOn = dependsOn;
        Explanation = explanation;
    }

    public CheckCode Code { get; }
    public string ObjectId { get; }
    public CheckStatus Status { get; }

    /// <summary>Set if and only if <see cref="Status"/> is <see cref="CheckStatus.Fail"/>.</summary>
    public Severity? Severity { get; }

    /// <summary>Set only for an unknown caused by another unresolved check; such a check asks no question of its own.</summary>
    public CheckCode? DependsOn { get; }

    /// <summary>Deterministic, human-readable reason. Shown to people and to the agent; never used for the decision.</summary>
    public string Explanation { get; }

    public IReadOnlyDictionary<string, string?> Actual { get; init; } = NoValues;
    public IReadOnlyDictionary<string, string?> Expected { get; init; } = NoValues;

    /// <summary>JSON Pointers into the document facts this check looked at.</summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    public bool IsRootUnknown => Status == CheckStatus.Unknown && DependsOn is null;

    public static CheckResult Pass(CheckCode code, string objectId, string explanation) =>
        new(code, objectId, CheckStatus.Pass, null, null, explanation);

    /// <summary>The severity of a failure is a property of the check, not of the call site.</summary>
    public static CheckResult Fail(CheckCode code, string objectId, string explanation) =>
        new(code, objectId, CheckStatus.Fail, CoreV1.FailSeverity(code), null, explanation);

    /// <summary>An input fact is missing. Produces exactly one question.</summary>
    public static CheckResult UnknownMissingFact(CheckCode code, string objectId, string explanation)
    {
        if (CoreV1.RequiredFactOf(code) is null)
        {
            throw new InvalidOperationException($"{code} has no required fact and therefore cannot be a root unknown.");
        }

        return new(code, objectId, CheckStatus.Unknown, null, null, explanation);
    }

    /// <summary>Cannot be evaluated until <paramref name="blockedBy"/> is resolved. Produces no question.</summary>
    public static CheckResult UnknownDependsOn(CheckCode code, string objectId, CheckCode blockedBy, string explanation) =>
        new(code, objectId, CheckStatus.Unknown, null, blockedBy, explanation);

    public static CheckResult NotApplicable(CheckCode code, string objectId, string explanation) =>
        new(code, objectId, CheckStatus.NotApplicable, null, null, explanation);
}

/// <summary>Per-line calculations. Quantity figures are null while the order line is not resolved.</summary>
public sealed record LineResult(
    string DocumentLineId,
    string? OrderLineId,
    MatchSource? MatchSource,
    decimal CalculatedNet,
    decimal CalculatedTax,
    decimal? Ordered,
    decimal? Received,
    decimal? PreviouslyInvoiced,
    decimal? AvailableToInvoice,
    decimal? RemainingOnOrder);

public sealed record Totals(decimal CalculatedSubtotal, decimal CalculatedTaxTotal, decimal CalculatedTotal);

public sealed record Question(RequiredFact RequiredFact, IReadOnlyList<string> TargetObjectIds, string Text);

/// <summary>The conclusion for one document at one input revision.</summary>
public sealed record Assessment(
    string? MatchedOrderId,
    MatchSource? OrderMatchSource,
    IReadOnlyList<LineResult> Lines,
    Totals Totals,
    IReadOnlyList<CheckResult> Checks,
    Decision Decision,
    IReadOnlyList<Question> Questions);

/// <summary>
/// What the engine says about a document: how its import is classified and, unless it is a duplicate, its assessment.
/// </summary>
public sealed record Evaluation
{
    private Evaluation(ImportDisposition disposition, string? originalDocumentId, string? existingAssessmentRef, Assessment? assessment)
    {
        Disposition = disposition;
        OriginalDocumentId = originalDocumentId;
        ExistingAssessmentRef = existingAssessmentRef;
        Assessment = assessment;
    }

    public ImportDisposition Disposition { get; }
    public string? OriginalDocumentId { get; }
    public string? ExistingAssessmentRef { get; }

    /// <summary>Null if and only if the document is a duplicate: a duplicate gets no assessment and no decision.</summary>
    public Assessment? Assessment { get; }

    public static Evaluation Duplicate(string originalDocumentId, string? existingAssessmentRef) =>
        new(ImportDisposition.Duplicate, originalDocumentId, existingAssessmentRef, null);

    public static Evaluation Assessed(ImportDisposition disposition, string? originalDocumentId, Assessment assessment)
    {
        if (disposition == ImportDisposition.Duplicate)
        {
            throw new ArgumentException("A duplicate has no assessment.", nameof(disposition));
        }

        if ((disposition == ImportDisposition.Conflict) != (originalDocumentId is not null))
        {
            throw new ArgumentException("The original document is named for a conflict and only for a conflict.", nameof(originalDocumentId));
        }

        return new(disposition, originalDocumentId, null, assessment);
    }
}
