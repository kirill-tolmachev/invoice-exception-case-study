using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

/// <summary>What makes two documents "the same invoice": who issued it, to whom, and under which number.</summary>
internal sealed record BusinessKey(string SupplierId, string BuyerId, string DocumentNumber)
{
    public static BusinessKey Of(HistoryDocument document) =>
        new(document.SupplierId, document.BuyerId, Normalize.Identifier(document.DocumentNumber));
}

/// <summary>
/// Outcome of R6. <see cref="Check"/> is null exactly when the document is a duplicate: a duplicate has no checks at all.
/// <see cref="Key"/> is null while supplier or buyer are unresolved.
/// </summary>
internal sealed record ConflictOutcome(
    ImportDisposition Disposition,
    string? OriginalDocumentId,
    string? ExistingAssessmentRef,
    BusinessKey? Key,
    CheckResult? Check);

/// <summary>R6. Duplicates and conflicts.</summary>
internal static class Duplicates
{
    public static ConflictOutcome Classify(
        InvoiceDocument document,
        Resolution<Supplier> supplier,
        Resolution<Buyer> buyer,
        ContextSnapshot context,
        IReadOnlyList<EarlierDocument> earlierDocuments)
    {
        const CheckCode code = CheckCode.DocumentConflict;
        var id = document.DocumentId;
        string[] refs = ["/number"];

        // Without both parties there is no business key, so nothing can be said about earlier versions yet.
        if (!supplier.IsFound)
        {
            return New(null, CheckResult.UnknownDependsOn(code, id, CheckCode.SupplierIdentity, "The business key needs a resolved supplier.") with { EvidenceRefs = refs });
        }

        if (!buyer.IsFound)
        {
            return New(null, CheckResult.UnknownDependsOn(code, id, CheckCode.BuyerIdentity, "The business key needs a resolved buyer.") with { EvidenceRefs = refs });
        }

        var key = new BusinessKey(supplier.Value!.SupplierId, buyer.Value!.BuyerId, Normalize.Identifier(document.Number));
        var actual = new Dictionary<string, string?>
        {
            ["supplier_id"] = key.SupplierId,
            ["buyer_id"] = key.BuyerId,
            ["document_number"] = key.DocumentNumber,
        };

        var sameKey = new List<(string DocumentId, ComparableFacts Facts, string? AssessmentRef)>();
        foreach (var counted in context.History.Where(h => BusinessKey.Of(h) == key))
        {
            sameKey.Add((counted.DocumentId, counted.Facts, null));
        }

        foreach (var earlier in earlierDocuments)
        {
            if (KeyOf(earlier.Document, context) == key)
            {
                sameKey.Add((earlier.Document.DocumentId, ComparableFacts.Of(earlier.Document), earlier.AssessmentRef));
            }
        }

        if (sameKey.Count == 0)
        {
            return New(key, CheckResult.Pass(code, id, "No earlier document with this supplier, buyer and number.") with { Actual = actual, EvidenceRefs = refs });
        }

        var facts = ComparableFacts.Of(document);
        foreach (var known in sameKey)
        {
            if (known.Facts.SameAs(facts))
            {
                return new ConflictOutcome(ImportDisposition.Duplicate, known.DocumentId, known.AssessmentRef, key, null);
            }
        }

        var original = sameKey[0];
        var expected = new Dictionary<string, string?> { ["original_document_id"] = original.DocumentId };
        return new ConflictOutcome(ImportDisposition.Conflict, original.DocumentId, null, key,
            CheckResult.Fail(code, id, $"Document {original.DocumentId} has the same supplier, buyer and number but different amounts, dates, lines or bank details. The original is kept as it is.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs });
    }

    private static BusinessKey? KeyOf(InvoiceDocument document, ContextSnapshot context)
    {
        var supplier = Parties.ResolveSupplier(document, context);
        var buyer = Parties.ResolveBuyer(document, context);
        return supplier.IsFound && buyer.IsFound
            ? new BusinessKey(supplier.Value!.SupplierId, buyer.Value!.BuyerId, Normalize.Identifier(document.Number))
            : null;
    }

    private static ConflictOutcome New(BusinessKey? key, CheckResult check) =>
        new(ImportDisposition.New, null, null, key, check);
}
