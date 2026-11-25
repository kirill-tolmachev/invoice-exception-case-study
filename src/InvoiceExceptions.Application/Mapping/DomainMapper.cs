using System.Globalization;
using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Validation;
using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Application.Mapping;

/// <summary>
/// Turns schema-valid payloads into the domain model. This is where decimals are parsed, and where the few
/// integrity rules a JSON Schema cannot express are checked: identifiers the engine addresses objects by must be unique.
/// Problems are collected as validation issues with the path of the offending field.
/// </summary>
internal sealed class DomainMapper(List<ValidationIssue> issues)
{
    public ContextSnapshot Context(ContextDto dto, string path)
    {
        RequireUnique(dto.PurchaseOrders.Orders, o => o.OrderId, i => $"{path}/purchase_orders/orders/{i}/order_id", "order_id");
        RequireUnique(dto.Suppliers.Suppliers, s => s.SupplierId, i => $"{path}/suppliers/suppliers/{i}/supplier_id", "supplier_id");
        RequireUnique(dto.Buyers.Buyers, b => b.BuyerId, i => $"{path}/buyers/buyers/{i}/buyer_id", "buyer_id");

        var orders = dto.PurchaseOrders.Orders.Select((order, i) =>
        {
            var orderPath = $"{path}/purchase_orders/orders/{i}";
            RequireUnique(order.Lines, l => l.LineId, j => $"{orderPath}/lines/{j}/line_id", "line_id");
            return new PurchaseOrder(
                order.OrderId,
                order.Reference,
                order.Status == "open" ? OrderStatus.Open : OrderStatus.Closed,
                order.SupplierId,
                order.BuyerId,
                order.Currency,
                [.. order.Lines.Select((line, j) => new OrderLine(
                    line.LineId,
                    line.Sku,
                    line.Description,
                    line.ExplicitAliases,
                    Number(line.Quantity, $"{orderPath}/lines/{j}/quantity"),
                    line.Unit,
                    Number(line.UnitNetPrice, $"{orderPath}/lines/{j}/unit_net_price"),
                    Number(line.TaxRate, $"{orderPath}/lines/{j}/tax_rate")))]);
        }).ToList();

        var receipts = new GoodsReceipts(
            dto.GoodsReceipts.Availability == "available",
            [.. dto.GoodsReceipts.Receipts.Select((receipt, i) => new GoodsReceipt(
                receipt.ReceiptId,
                receipt.OrderId,
                receipt.OrderLineId,
                Number(receipt.AcceptedQuantity, $"{path}/goods_receipts/receipts/{i}/accepted_quantity"),
                receipt.Status == "accepted"))]);

        var suppliers = dto.Suppliers.Suppliers
            .Select(s => new Supplier(s.SupplierId, s.LegalName, s.RegistrationId, [.. s.ApprovedBankAccounts.Select(a => a.Iban)]))
            .ToList();

        var buyers = dto.Buyers.Buyers
            .Select(b => new Buyer(b.BuyerId, b.LegalName, b.RegistrationId, b.ExplicitAliases))
            .ToList();

        var history = dto.InvoiceHistory.Documents.Select((document, i) =>
        {
            var documentPath = $"{path}/invoice_history/documents/{i}";
            var facts = document.NormalizedFacts;
            return new HistoryDocument(
                document.DocumentId,
                document.SupplierId,
                document.BuyerId,
                document.DocumentNumber,
                document.Status == "counted",
                document.MatchedOrderId,
                [.. document.Lines.Select((line, j) => new HistoryLine(line.OrderLineId, Number(line.Quantity, $"{documentPath}/lines/{j}/quantity")))],
                new ComparableFacts(
                    facts.IssueDate,
                    facts.DueDate,
                    facts.Currency,
                    facts.PurchaseOrderReference,
                    facts.Iban,
                    Number(facts.DeclaredSubtotal, $"{documentPath}/normalized_facts/declared_subtotal"),
                    Number(facts.DeclaredTaxTotal, $"{documentPath}/normalized_facts/declared_tax_total"),
                    Number(facts.DeclaredTotal, $"{documentPath}/normalized_facts/declared_total"),
                    [.. facts.Lines.Select((line, j) =>
                    {
                        var linePath = $"{documentPath}/normalized_facts/lines/{j}";
                        return new ComparableLine(
                            line.Sku,
                            Number(line.Quantity, $"{linePath}/quantity"),
                            line.Unit,
                            Number(line.UnitNetPrice, $"{linePath}/unit_net_price"),
                            Number(line.TaxRate, $"{linePath}/tax_rate"),
                            Number(line.DeclaredNetAmount, $"{linePath}/declared_net_amount"),
                            OptionalNumber(line.DeclaredTaxAmount, $"{linePath}/declared_tax_amount"));
                    })]));
        }).ToList();

        return new ContextSnapshot(dto.ContextVersion, orders, receipts, suppliers, buyers, history);
    }

    public InvoiceDocument Document(FactsDto dto, string path)
    {
        RequireUnique(dto.Lines, l => l.LineId, i => $"{path}/lines/{i}/line_id", "line_id");

        // A fact carries the id of the document it was read from; evidence borrowed from another document is not evidence.
        for (var i = 0; i < dto.Evidence.Count; i++)
        {
            if (dto.Evidence[i].DocumentId != dto.DocumentId)
            {
                issues.Add(new($"{path}/evidence/{i}/document_id", $"Evidence must come from the document itself ('{dto.DocumentId}'), not from '{dto.Evidence[i].DocumentId}'."));
            }
        }

        return new InvoiceDocument(
            dto.DocumentId,
            dto.Number,
            dto.IssueDate,
            dto.DueDate,
            dto.Currency,
            dto.PurchaseOrderReference,
            new Party(dto.Supplier.LegalName, dto.Supplier.RegistrationId),
            new Party(dto.Buyer.LegalName, dto.Buyer.RegistrationId),
            dto.Bank.Iban,
            [.. dto.Lines.Select((line, i) => new InvoiceLine(
                line.LineId,
                line.Description,
                line.Sku,
                Number(line.Quantity, $"{path}/lines/{i}/quantity"),
                line.Unit,
                Number(line.UnitNetPrice, $"{path}/lines/{i}/unit_net_price"),
                Number(line.DeclaredNetAmount, $"{path}/lines/{i}/declared_net_amount"),
                Number(line.TaxRate, $"{path}/lines/{i}/tax_rate"),
                OptionalNumber(line.DeclaredTaxAmount, $"{path}/lines/{i}/declared_tax_amount")))],
            Number(dto.DeclaredSubtotal, $"{path}/declared_subtotal"),
            Number(dto.DeclaredTaxTotal, $"{path}/declared_tax_total"),
            Number(dto.DeclaredTotal, $"{path}/declared_total"));
    }

    private decimal? OptionalNumber(string? text, string path) => text is null ? null : Number(text, path);

    /// <summary>
    /// The schema has already fixed the format (digits, one dot, no sign, no exponent); what is left to catch here
    /// is a number too large for exact decimal arithmetic.
    /// </summary>
    private decimal Number(string text, string path)
    {
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        issues.Add(new(path, $"'{text}' is not a decimal number within the supported range (up to 28 significant digits)."));
        return 0m;
    }

    private void RequireUnique<T>(IReadOnlyList<T> items, Func<T, string> key, Func<int, string> pathOf, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (!seen.Add(key(items[i])))
            {
                issues.Add(new(pathOf(i), $"Duplicate {name} '{key(items[i])}': results refer to objects by id, so ids must be unique."));
            }
        }
    }
}
