using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Domain.Policy;

/// <summary>
/// The parameters of policy core-v1 that are not logic: scales, tolerance, severities, the fixed set of checks
/// and the question dictionary. The human-readable source is docs/BUSINESS_RULES.md and the machine-readable one is
/// dataset/policies/core-v1.json; a test pins the values below to that file so the two cannot drift apart.
/// </summary>
public static class CoreV1
{
    public const string PolicyVersion = "core-v1";

    /// <summary>Money and unit prices are rounded half-up to this many decimals.</summary>
    public const int MoneyScale = 2;

    /// <summary>R4: absolute, inclusive and symmetric tolerance between the invoice and the order unit price.</summary>
    public static readonly decimal UnitPriceTolerance = 0.01m;

    /// <summary>Checks issued once per document, in evaluation order.</summary>
    public static readonly IReadOnlyList<CheckCode> DocumentChecks =
    [
        CheckCode.DocumentConflict,
        CheckCode.SupplierIdentity,
        CheckCode.BuyerIdentity,
        CheckCode.OrderMatch,
        CheckCode.CurrencyMatch,
        CheckCode.TotalsArithmetic,
        CheckCode.BankAccount,
    ];

    /// <summary>Checks issued once per document line, in evaluation order.</summary>
    public static readonly IReadOnlyList<CheckCode> LineChecks =
    [
        CheckCode.LineMatch,
        CheckCode.QtyReceived,
        CheckCode.QtyOrderRemaining,
        CheckCode.UnitPrice,
        CheckCode.LineArithmetic,
    ];

    /// <summary>The order in which checks are evaluated and reported.</summary>
    public static readonly IReadOnlyList<CheckCode> EvaluationOrder =
    [
        CheckCode.DocumentConflict,
        CheckCode.SupplierIdentity,
        CheckCode.BuyerIdentity,
        CheckCode.OrderMatch,
        CheckCode.CurrencyMatch,
        CheckCode.LineMatch,
        CheckCode.QtyReceived,
        CheckCode.QtyOrderRemaining,
        CheckCode.UnitPrice,
        CheckCode.LineArithmetic,
        CheckCode.TotalsArithmetic,
        CheckCode.BankAccount,
    ];

    /// <summary>What a failure of the given check means: a broken rule (blocked) or a call a human has to make (needs_review).</summary>
    public static Severity FailSeverity(CheckCode code) => code switch
    {
        CheckCode.OrderMatch or CheckCode.LineMatch or CheckCode.UnitPrice => Severity.NeedsReview,
        _ => Severity.Blocked,
    };

    /// <summary>The fact a check asks for when it cannot be evaluated; null for checks that never lack input of their own.</summary>
    public static RequiredFact? RequiredFactOf(CheckCode code) => code switch
    {
        CheckCode.SupplierIdentity => RequiredFact.SupplierRegistrationId,
        CheckCode.BuyerIdentity => RequiredFact.BuyerRegistrationId,
        CheckCode.CurrencyMatch => RequiredFact.DocumentCurrency,
        CheckCode.OrderMatch => RequiredFact.OrderReference,
        CheckCode.LineMatch => RequiredFact.LineMatch,
        CheckCode.QtyReceived => RequiredFact.GoodsReceipts,
        CheckCode.BankAccount => RequiredFact.BankAccount,
        _ => null,
    };

    public static string QuestionText(RequiredFact fact) => fact switch
    {
        RequiredFact.SupplierRegistrationId => "Supplier registration ID is missing from the document.",
        RequiredFact.BuyerRegistrationId => "Buyer registration ID is missing and the buyer name does not resolve to exactly one buyer.",
        RequiredFact.DocumentCurrency => "Document currency is missing.",
        RequiredFact.OrderReference => "The purchase order cannot be determined: reference missing or unknown, and no unique deterministic candidate.",
        RequiredFact.LineMatch => "A document line cannot be matched to exactly one line of the order.",
        RequiredFact.GoodsReceipts => "Goods receipt data is unavailable.",
        RequiredFact.BankAccount => "Bank account is missing from the document.",
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, null),
    };
}
