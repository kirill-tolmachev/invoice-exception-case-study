using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

/// <summary>R5. The IBAN on the invoice must be one we already approved for that supplier.</summary>
internal static class BankAccount
{
    /// <summary>
    /// Checked against the supplier the document itself names (by registration ID), not against the supplier of the order:
    /// whether those two are the same company is SUPPLIER_IDENTITY's question. The directory is never updated from a document.
    /// </summary>
    public static CheckResult Check(InvoiceDocument document, Resolution<Supplier> supplier)
    {
        const CheckCode code = CheckCode.BankAccount;
        var id = document.DocumentId;
        string[] refs = ["/bank/iban"];
        var actual = new Dictionary<string, string?> { ["iban"] = Normalize.OptionalIban(document.Iban) };

        if (document.Iban is null)
        {
            return CheckResult.UnknownMissingFact(code, id, "The document does not state an IBAN.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (!supplier.IsFound)
        {
            return CheckResult.UnknownDependsOn(code, id, CheckCode.SupplierIdentity, "The supplier is not resolved, so there is no list of approved accounts to check the IBAN against.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        var approved = supplier.Value!.ApprovedIbans.Select(Normalize.Iban).ToList();
        var expected = new Dictionary<string, string?>
        {
            ["supplier_id"] = supplier.Value.SupplierId,
            ["approved_ibans"] = string.Join(", ", approved),
        };
        return approved.Contains(Normalize.Iban(document.Iban))
            ? CheckResult.Pass(code, id, $"The IBAN is an approved account of supplier {supplier.Value.SupplierId}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, id, $"IBAN {Normalize.Iban(document.Iban)} is not among the approved accounts of supplier {supplier.Value.SupplierId}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }
}
