using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Model;

/// <summary>
/// The facts R6 compares to tell a duplicate from a conflict. Descriptions, payment terms, layout,
/// file name and content hash are deliberately not part of it.
/// </summary>
public sealed record ComparableFacts(
    string IssueDate,
    string? DueDate,
    string? Currency,
    string? PurchaseOrderReference,
    string? Iban,
    decimal DeclaredSubtotal,
    decimal DeclaredTaxTotal,
    decimal DeclaredTotal,
    IReadOnlyList<ComparableLine> Lines)
{
    public static ComparableFacts Of(InvoiceDocument document) => new(
        document.IssueDate,
        document.DueDate,
        document.Currency,
        document.PurchaseOrderReference,
        document.Iban,
        document.DeclaredSubtotal,
        document.DeclaredTaxTotal,
        document.DeclaredTotal,
        [.. document.Lines.Select(l => new ComparableLine(
            l.Sku, l.Quantity, l.Unit, l.UnitNetPrice, l.TaxRate, l.DeclaredNetAmount, l.DeclaredTaxAmount))]);

    /// <summary>Equality after normalization; lines are compared as a multiset, so their order does not matter.</summary>
    public bool SameAs(ComparableFacts other) =>
        IssueDate == other.IssueDate
        && DueDate == other.DueDate
        && Currency == other.Currency
        && Normalize.OptionalIdentifier(PurchaseOrderReference) == Normalize.OptionalIdentifier(other.PurchaseOrderReference)
        && Normalize.OptionalIban(Iban) == Normalize.OptionalIban(other.Iban)
        && DeclaredSubtotal == other.DeclaredSubtotal
        && DeclaredTaxTotal == other.DeclaredTaxTotal
        && DeclaredTotal == other.DeclaredTotal
        && SameMultiset(Lines, other.Lines);

    private static bool SameMultiset(IReadOnlyList<ComparableLine> left, IReadOnlyList<ComparableLine> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        // decimal equality and hashing ignore scale, so "35" and "35.000" land in the same bucket.
        var remaining = left.Select(l => l.Normalized()).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        foreach (var line in right.Select(l => l.Normalized()))
        {
            if (!remaining.TryGetValue(line, out var count) || count == 0)
            {
                return false;
            }

            remaining[line] = count - 1;
        }

        return true;
    }
}

public sealed record ComparableLine(
    string? Sku,
    decimal Quantity,
    string Unit,
    decimal UnitNetPrice,
    decimal TaxRate,
    decimal DeclaredNetAmount,
    decimal? DeclaredTaxAmount)
{
    internal ComparableLine Normalized() => this with { Sku = Normalize.OptionalIdentifier(Sku) };
}
