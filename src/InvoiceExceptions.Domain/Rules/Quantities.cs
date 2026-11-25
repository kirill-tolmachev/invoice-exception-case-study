using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

/// <summary>
/// What is known about one order line. <see cref="Received"/> and <see cref="AvailableToInvoice"/> are null
/// when goods receipt data is unavailable - "we do not know" must not look like "nothing was received".
/// </summary>
internal sealed record QuantityFigures(
    decimal Ordered,
    decimal? Received,
    decimal PreviouslyInvoiced,
    decimal? AvailableToInvoice,
    decimal RemainingOnOrder);

/// <summary>R3. Quantities against goods receipts and previously counted invoices.</summary>
internal static class Quantities
{
    /// <param name="currentKey">
    /// Business key of the document being evaluated. History entries with that key are an earlier version of this
    /// very invoice and must not be counted against it.
    /// </param>
    public static QuantityFigures For(PurchaseOrder order, OrderLine orderLine, ContextSnapshot context, BusinessKey? currentKey)
    {
        var previouslyInvoiced = context.History
            .Where(h => h.Counted && h.MatchedOrderId == order.OrderId && (currentKey is null || BusinessKey.Of(h) != currentKey))
            .SelectMany(h => h.Lines)
            .Where(l => l.OrderLineId == orderLine.LineId)
            .Sum(l => l.Quantity);

        decimal? received = context.GoodsReceipts.Available
            ? context.GoodsReceipts.Receipts
                .Where(r => r.Accepted && r.OrderId == order.OrderId && r.OrderLineId == orderLine.LineId)
                .Sum(r => r.AcceptedQuantity)
            : null;

        return new QuantityFigures(
            Ordered: orderLine.Quantity,
            Received: received,
            PreviouslyInvoiced: previouslyInvoiced,
            AvailableToInvoice: received - previouslyInvoiced,
            RemainingOnOrder: orderLine.Quantity - previouslyInvoiced);
    }

    public static CheckResult Received(InvoiceLine line, int index, OrderSelection order, LineLink? link, QuantityFigures? figures)
    {
        const CheckCode code = CheckCode.QtyReceived;
        if (Unresolved(code, line, index, order, link) is { } unresolved)
        {
            return unresolved;
        }

        string[] refs = [$"/lines/{index}/quantity"];
        var actual = new Dictionary<string, string?> { ["quantity"] = Wire.Number(line.Quantity) };

        if (figures!.AvailableToInvoice is not { } available)
        {
            return CheckResult.UnknownMissingFact(code, line.LineId, "Goods receipt data is unavailable, so it is unknown how much was accepted.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        var expected = new Dictionary<string, string?>
        {
            ["available_to_invoice"] = Wire.Number(available),
            ["received"] = Wire.Number(figures.Received),
            ["previously_invoiced"] = Wire.Number(figures.PreviouslyInvoiced),
        };
        var formula = $"received {Wire.Number(figures.Received)} - previously invoiced {Wire.Number(figures.PreviouslyInvoiced)} = {Wire.Number(available)} available";
        return line.Quantity <= available
            ? CheckResult.Pass(code, line.LineId, $"Invoiced {Wire.Number(line.Quantity)} is within what was accepted and not yet invoiced: {formula}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, line.LineId, $"Invoiced {Wire.Number(line.Quantity)} exceeds what was accepted and not yet invoiced by {Wire.Number(line.Quantity - available)}: {formula}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    public static CheckResult OrderRemaining(InvoiceLine line, int index, OrderSelection order, LineLink? link, QuantityFigures? figures)
    {
        const CheckCode code = CheckCode.QtyOrderRemaining;
        if (Unresolved(code, line, index, order, link) is { } unresolved)
        {
            return unresolved;
        }

        string[] refs = [$"/lines/{index}/quantity"];
        var actual = new Dictionary<string, string?> { ["quantity"] = Wire.Number(line.Quantity) };
        var remaining = figures!.RemainingOnOrder;
        var expected = new Dictionary<string, string?>
        {
            ["remaining_on_order"] = Wire.Number(remaining),
            ["ordered"] = Wire.Number(figures.Ordered),
            ["previously_invoiced"] = Wire.Number(figures.PreviouslyInvoiced),
        };
        var formula = $"ordered {Wire.Number(figures.Ordered)} - previously invoiced {Wire.Number(figures.PreviouslyInvoiced)} = {Wire.Number(remaining)} remaining";
        return line.Quantity <= remaining
            ? CheckResult.Pass(code, line.LineId, $"Invoiced {Wire.Number(line.Quantity)} is within what remains on the order: {formula}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, line.LineId, $"Invoiced {Wire.Number(line.Quantity)} exceeds what remains on the order by {Wire.Number(line.Quantity - remaining)}: {formula}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    /// <summary>
    /// The three outcomes shared by every check that compares a document line with its order line
    /// (QTY_RECEIVED, QTY_ORDER_REMAINING, UNIT_PRICE): no order, no line, or units that cannot be compared.
    /// Returns null when the comparison can go ahead.
    /// </summary>
    public static CheckResult? Unresolved(CheckCode code, InvoiceLine line, int index, OrderSelection order, LineLink? link)
    {
        if (order.Order is null)
        {
            return CheckResult.UnknownDependsOn(code, line.LineId, CheckCode.OrderMatch, "No order is selected.");
        }

        if (link is null)
        {
            return CheckResult.UnknownDependsOn(code, line.LineId, CheckCode.LineMatch, $"The line is not matched to a line of order {order.Order.OrderId}.");
        }

        if (!OrderMatching.SameUnit(line, link.OrderLine))
        {
            return CheckResult.NotApplicable(code, line.LineId, $"Invoice unit '{line.Unit}' and order unit '{link.OrderLine.Unit}' differ; without a conversion the comparison is meaningless.")
                with { EvidenceRefs = [$"/lines/{index}/unit"] };
        }

        return null;
    }
}
