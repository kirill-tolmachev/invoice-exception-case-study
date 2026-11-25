using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Domain.Policy;

/// <summary>
/// The names core-v1 uses for its vocabulary in JSON (check codes, statuses, required facts, ...).
/// Kept next to the policy so that every channel - CLI, MCP, page, storage - spells them the same way.
/// </summary>
public static class Wire
{
    public static string Name(CheckCode code) => code switch
    {
        CheckCode.DocumentConflict => "DOCUMENT_CONFLICT",
        CheckCode.SupplierIdentity => "SUPPLIER_IDENTITY",
        CheckCode.BuyerIdentity => "BUYER_IDENTITY",
        CheckCode.OrderMatch => "ORDER_MATCH",
        CheckCode.CurrencyMatch => "CURRENCY_MATCH",
        CheckCode.LineMatch => "LINE_MATCH",
        CheckCode.QtyReceived => "QTY_RECEIVED",
        CheckCode.QtyOrderRemaining => "QTY_ORDER_REMAINING",
        CheckCode.UnitPrice => "UNIT_PRICE",
        CheckCode.LineArithmetic => "LINE_ARITHMETIC",
        CheckCode.TotalsArithmetic => "TOTALS_ARITHMETIC",
        CheckCode.BankAccount => "BANK_ACCOUNT",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    public static string Name(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "pass",
        CheckStatus.Fail => "fail",
        CheckStatus.Unknown => "unknown",
        CheckStatus.NotApplicable => "not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string Name(Severity severity) => severity switch
    {
        Severity.Blocked => "blocked",
        Severity.NeedsReview => "needs_review",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    public static string Name(Decision decision) => decision switch
    {
        Decision.Clear => "clear",
        Decision.NeedsReview => "needs_review",
        Decision.NeedsInformation => "needs_information",
        Decision.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
    };

    public static string Name(MatchSource source) => source switch
    {
        MatchSource.Deterministic => "deterministic",
        MatchSource.AgentProposal => "agent_proposal",
        MatchSource.HumanConfirmation => "human_confirmation",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public static string Name(ImportDisposition disposition) => disposition switch
    {
        ImportDisposition.New => "new",
        ImportDisposition.Duplicate => "duplicate",
        ImportDisposition.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null),
    };

    public static string Name(RequiredFact fact) => fact switch
    {
        RequiredFact.SupplierRegistrationId => "supplier.registration_id",
        RequiredFact.BuyerRegistrationId => "buyer.registration_id",
        RequiredFact.DocumentCurrency => "document.currency",
        RequiredFact.OrderReference => "order_reference",
        RequiredFact.LineMatch => "line_match",
        RequiredFact.GoodsReceipts => "goods_receipts",
        RequiredFact.BankAccount => "bank_account",
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, null),
    };

    /// <summary>Decimals travel as plain strings with a dot; never exponent notation, never a locale.</summary>
    public static string Number(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string? Number(decimal? value) => value is null ? null : Number(value.Value);

    /// <summary>Monetary amounts are always shown with exactly two decimals.</summary>
    public static string Amount(decimal value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
