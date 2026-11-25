using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

/// <summary>How far a party named on the document could be traced to the directory.</summary>
internal sealed record Resolution<T> where T : class
{
    private Resolution(T? value, bool missingFact)
    {
        Value = value;
        IsMissingFact = missingFact;
    }

    /// <summary>The directory entry; non-null if and only if <see cref="IsFound"/>.</summary>
    public T? Value { get; }

    public bool IsFound => Value is not null;

    /// <summary>The document does not carry the fact needed to look the party up.</summary>
    public bool IsMissingFact { get; }

    /// <summary>The document states an identifier, but the directory does not know it.</summary>
    public bool IsNotFound => !IsFound && !IsMissingFact;

    public static Resolution<T> Found(T value) => new(value, false);
    public static Resolution<T> MissingFact { get; } = new(null, true);
    public static Resolution<T> NotFound { get; } = new(null, false);
}

/// <summary>R1. Parties and currency.</summary>
internal static class Parties
{
    /// <summary>Suppliers are identified by registration ID only. Names and aliases are never used: similar names are a fraud pattern.</summary>
    public static Resolution<Supplier> ResolveSupplier(InvoiceDocument document, ContextSnapshot context)
    {
        if (document.Supplier.RegistrationId is null)
        {
            return Resolution<Supplier>.MissingFact;
        }

        var registrationId = Normalize.Identifier(document.Supplier.RegistrationId);
        var matches = context.Suppliers.Where(s => Normalize.Identifier(s.RegistrationId) == registrationId).ToList();
        return matches.Count == 1 ? Resolution<Supplier>.Found(matches[0]) : Resolution<Supplier>.NotFound;
    }

    /// <summary>Buyers are our own legal entities: registration ID first, otherwise an exact name or explicit alias of exactly one buyer.</summary>
    public static Resolution<Buyer> ResolveBuyer(InvoiceDocument document, ContextSnapshot context)
    {
        if (document.Buyer.RegistrationId is not null)
        {
            var registrationId = Normalize.Identifier(document.Buyer.RegistrationId);
            var matches = context.Buyers.Where(b => Normalize.Identifier(b.RegistrationId) == registrationId).ToList();
            return matches.Count == 1 ? Resolution<Buyer>.Found(matches[0]) : Resolution<Buyer>.NotFound;
        }

        if (document.Buyer.LegalName is null)
        {
            return Resolution<Buyer>.MissingFact;
        }

        var name = Normalize.Name(document.Buyer.LegalName);
        var byName = context.Buyers
            .Where(b => Normalize.Name(b.LegalName) == name || b.ExplicitAliases.Any(alias => Normalize.Name(alias) == name))
            .ToList();
        return byName.Count == 1 ? Resolution<Buyer>.Found(byName[0]) : Resolution<Buyer>.MissingFact;
    }

    public static CheckResult SupplierIdentity(InvoiceDocument document, Resolution<Supplier> supplier, OrderSelection order)
    {
        const CheckCode code = CheckCode.SupplierIdentity;
        var id = document.DocumentId;
        string[] refs = ["/supplier/registration_id"];
        var actual = new Dictionary<string, string?> { ["registration_id"] = document.Supplier.RegistrationId, ["supplier_id"] = supplier.Value?.SupplierId };

        if (supplier.IsMissingFact)
        {
            return CheckResult.UnknownMissingFact(code, id, "The document does not state a supplier registration ID.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (supplier.IsNotFound)
        {
            return CheckResult.Fail(code, id, $"Registration ID '{document.Supplier.RegistrationId}' does not identify a supplier in the directory.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (order.Order is null)
        {
            return CheckResult.UnknownDependsOn(code, id, CheckCode.OrderMatch, "The supplier is in the directory, but no order is selected to compare it with.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        var expected = new Dictionary<string, string?> { ["order_id"] = order.Order.OrderId, ["supplier_id"] = order.Order.SupplierId };
        return supplier.Value!.SupplierId == order.Order.SupplierId
            ? CheckResult.Pass(code, id, $"The document supplier {supplier.Value.SupplierId} is the supplier of order {order.Order.OrderId}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, id, $"The document supplier is {supplier.Value.SupplierId} ({supplier.Value.LegalName}), but order {order.Order.OrderId} belongs to supplier {order.Order.SupplierId}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    public static CheckResult BuyerIdentity(InvoiceDocument document, Resolution<Buyer> buyer, OrderSelection order)
    {
        const CheckCode code = CheckCode.BuyerIdentity;
        var id = document.DocumentId;
        string[] refs = ["/buyer/registration_id", "/buyer/legal_name"];
        var actual = new Dictionary<string, string?>
        {
            ["registration_id"] = document.Buyer.RegistrationId,
            ["legal_name"] = document.Buyer.LegalName,
            ["buyer_id"] = buyer.Value?.BuyerId,
        };

        if (buyer.IsMissingFact)
        {
            return CheckResult.UnknownMissingFact(code, id, "The document states no buyer registration ID and the buyer name does not identify exactly one buyer.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (buyer.IsNotFound)
        {
            return CheckResult.Fail(code, id, $"Registration ID '{document.Buyer.RegistrationId}' does not identify one of our buyer entities.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (order.Order is null)
        {
            return CheckResult.UnknownDependsOn(code, id, CheckCode.OrderMatch, "The buyer is in the directory, but no order is selected to compare it with.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        var expected = new Dictionary<string, string?> { ["order_id"] = order.Order.OrderId, ["buyer_id"] = order.Order.BuyerId };
        return buyer.Value!.BuyerId == order.Order.BuyerId
            ? CheckResult.Pass(code, id, $"The document buyer {buyer.Value.BuyerId} is the buyer of order {order.Order.OrderId}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, id, $"The document buyer is {buyer.Value.BuyerId} ({buyer.Value.LegalName}), but order {order.Order.OrderId} was placed by buyer {order.Order.BuyerId}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    public static CheckResult CurrencyMatch(InvoiceDocument document, OrderSelection order)
    {
        const CheckCode code = CheckCode.CurrencyMatch;
        var id = document.DocumentId;
        string[] refs = ["/currency"];
        var actual = new Dictionary<string, string?> { ["currency"] = document.Currency };

        if (document.Currency is null)
        {
            return CheckResult.UnknownMissingFact(code, id, "The document does not state a currency.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (order.Order is null)
        {
            return CheckResult.UnknownDependsOn(code, id, CheckCode.OrderMatch, "No order is selected to compare the currency with.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        var expected = new Dictionary<string, string?> { ["order_id"] = order.Order.OrderId, ["currency"] = order.Order.Currency };
        return document.Currency == order.Order.Currency
            ? CheckResult.Pass(code, id, $"Document and order {order.Order.OrderId} are both in {order.Order.Currency}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, id, $"The document is in {document.Currency}, order {order.Order.OrderId} is in {order.Order.Currency}; currencies are not converted.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }
}
