using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

/// <summary>The order the document is reconciled against and how it was chosen. No order means ORDER_MATCH is unknown.</summary>
internal sealed record OrderSelection(PurchaseOrder? Order, MatchSource? Source, CheckResult Check);

/// <summary>A document line tied to an order line, and where that tie comes from.</summary>
internal sealed record LineLink(OrderLine OrderLine, MatchSource Source);

/// <summary>R2. Choosing the order and matching lines.</summary>
internal static class OrderMatching
{
    /// <summary>
    /// The order that follows from the document and the context alone, without any proposal.
    /// With a PO reference: the order carrying exactly that reference, whatever its status or supplier
    /// (a foreign supplier is SUPPLIER_IDENTITY's finding, not a reason to look elsewhere).
    /// Without one: the only open order of the document's supplier and buyer whose lines cover every document line.
    /// </summary>
    public static PurchaseOrder? DeterministicOrder(
        InvoiceDocument document, ContextSnapshot context, Resolution<Supplier> supplier, Resolution<Buyer> buyer)
    {
        if (document.PurchaseOrderReference is not null)
        {
            var reference = Normalize.Identifier(document.PurchaseOrderReference);
            var byReference = context.Orders.Where(o => Normalize.Identifier(o.Reference) == reference).ToList();
            return byReference.Count == 1 ? byReference[0] : null;
        }

        var candidates = CandidateOrders(document, context, supplier, buyer);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public static OrderSelection Select(
        InvoiceDocument document,
        ContextSnapshot context,
        Resolution<Supplier> supplier,
        Resolution<Buyer> buyer,
        IReadOnlyList<MatchProposal> proposals)
    {
        const CheckCode code = CheckCode.OrderMatch;
        var id = document.DocumentId;
        string[] refs = ["/purchase_order_reference"];
        var actual = new Dictionary<string, string?> { ["purchase_order_reference"] = document.PurchaseOrderReference };

        // 1. A deterministic match always wins over proposals.
        if (DeterministicOrder(document, context, supplier, buyer) is { } deterministic)
        {
            var how = document.PurchaseOrderReference is not null
                ? $"PO reference '{document.PurchaseOrderReference}' identifies order {deterministic.OrderId}."
                : $"No PO reference; order {deterministic.OrderId} is the only open order of this supplier and buyer whose lines match every document line.";
            return new(deterministic, MatchSource.Deterministic,
                CheckResult.Pass(code, id, how) with { Actual = actual, Expected = Expected(deterministic), EvidenceRefs = refs });
        }

        // 2. Otherwise a registered proposal: confirmed ones outrank pending ones, the most recent of each wins.
        var ordersById = context.Orders.ToDictionary(o => o.OrderId);
        var usable = proposals.Where(p => ordersById.ContainsKey(p.OrderId)).ToList();

        if (usable.LastOrDefault(p => p.ConfirmedByHuman) is { } confirmed)
        {
            var order = ordersById[confirmed.OrderId];
            return new(order, MatchSource.HumanConfirmation,
                CheckResult.Pass(code, id, $"Order {order.OrderId} was proposed by the agent (proposal {confirmed.ProposalId}) and confirmed by a human.")
                    with { Actual = actual, Expected = Expected(order), EvidenceRefs = refs });
        }

        if (usable.LastOrDefault(p => !p.ConfirmedByHuman) is { } pending)
        {
            var order = ordersById[pending.OrderId];
            return new(order, MatchSource.AgentProposal,
                CheckResult.Fail(code, id, $"Order {order.OrderId} is an agent proposal ({pending.ProposalId}) that no human has confirmed yet. The other checks are computed against it to show the consequences.")
                    with { Actual = actual, Expected = Expected(order), EvidenceRefs = refs });
        }

        // 3. Nothing to go on.
        if (document.PurchaseOrderReference is not null)
        {
            return new(null, null,
                CheckResult.UnknownMissingFact(code, id, $"PO reference '{document.PurchaseOrderReference}' does not identify exactly one order in the context.")
                    with { Actual = actual, EvidenceRefs = refs });
        }

        if (!supplier.IsFound)
        {
            return new(null, null,
                CheckResult.UnknownDependsOn(code, id, CheckCode.SupplierIdentity, "No PO reference, and without a resolved supplier the candidate orders cannot be listed.")
                    with { Actual = actual, EvidenceRefs = refs });
        }

        var candidateCount = CandidateOrders(document, context, supplier, buyer).Count;
        return new(null, null,
            CheckResult.UnknownMissingFact(code, id, $"No PO reference, and {candidateCount} open orders of this supplier match every document line deterministically; exactly one is required.")
                with { Actual = actual, EvidenceRefs = refs });
    }

    /// <summary>
    /// Document lines that match exactly one line of the order by SKU, explicit alias or description.
    /// Matching is one-to-one: if several document lines point at the same order line, none of them is matched,
    /// because nothing in the data says which one it is.
    /// </summary>
    public static IReadOnlyDictionary<string, OrderLine> DeterministicLines(IReadOnlyList<InvoiceLine> documentLines, PurchaseOrder order)
    {
        var single = new Dictionary<string, OrderLine>();
        foreach (var line in documentLines)
        {
            var candidates = order.Lines.Where(orderLine => Matches(line, orderLine)).ToList();
            if (candidates.Count == 1)
            {
                single[line.LineId] = candidates[0];
            }
        }

        var contested = single.Values.GroupBy(o => o.LineId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return single.Where(pair => !contested.Contains(pair.Value.LineId)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>
    /// Ties every document line it can to a line of the selected order. Precedence: deterministic match,
    /// then human-confirmed proposals, then pending proposals; within a rank the most recent proposal wins.
    /// An order line is never given to two document lines.
    /// </summary>
    public static IReadOnlyDictionary<string, LineLink> LinkLines(
        InvoiceDocument document, PurchaseOrder order, IReadOnlyList<MatchProposal> proposals)
    {
        var links = new Dictionary<string, LineLink>();
        var taken = new HashSet<string>();
        foreach (var (documentLineId, orderLine) in DeterministicLines(document.Lines, order))
        {
            links[documentLineId] = new LineLink(orderLine, MatchSource.Deterministic);
            taken.Add(orderLine.LineId);
        }

        var documentLineIds = document.Lines.Select(l => l.LineId).ToHashSet();
        var orderLines = order.Lines.ToDictionary(l => l.LineId);
        var newestFirst = proposals.Where(p => p.OrderId == order.OrderId).Reverse().ToList();

        foreach (var confirmed in new[] { true, false })
        {
            foreach (var proposal in newestFirst.Where(p => p.ConfirmedByHuman == confirmed))
            {
                foreach (var match in proposal.LineMatches)
                {
                    if (links.ContainsKey(match.DocumentLineId)
                        || !documentLineIds.Contains(match.DocumentLineId)
                        || !orderLines.TryGetValue(match.OrderLineId, out var orderLine)
                        || !taken.Add(orderLine.LineId))
                    {
                        continue;
                    }

                    links[match.DocumentLineId] = new LineLink(
                        orderLine, confirmed ? MatchSource.HumanConfirmation : MatchSource.AgentProposal);
                }
            }
        }

        return links;
    }

    public static CheckResult LineMatch(InvoiceLine line, int index, OrderSelection order, LineLink? link)
    {
        const CheckCode code = CheckCode.LineMatch;
        string[] refs = [$"/lines/{index}/sku", $"/lines/{index}/description", $"/lines/{index}/unit"];
        var actual = new Dictionary<string, string?> { ["sku"] = line.Sku, ["description"] = line.Description, ["unit"] = line.Unit };

        if (order.Order is null)
        {
            return CheckResult.UnknownDependsOn(code, line.LineId, CheckCode.OrderMatch, "No order is selected, so there are no order lines to match against.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        if (link is null)
        {
            return CheckResult.UnknownMissingFact(code, line.LineId, $"The line does not match exactly one free line of order {order.Order.OrderId} by SKU, explicit alias or description.")
                with { Actual = actual, EvidenceRefs = refs };
        }

        var expected = new Dictionary<string, string?>
        {
            ["order_line_id"] = link.OrderLine.LineId,
            ["sku"] = link.OrderLine.Sku,
            ["description"] = link.OrderLine.Description,
            ["unit"] = link.OrderLine.Unit,
            ["match_source"] = Wire.Name(link.Source),
        };

        if (link.Source == MatchSource.AgentProposal)
        {
            return CheckResult.Fail(code, line.LineId, $"Order line {link.OrderLine.LineId} is an agent proposal that no human has confirmed yet.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
        }

        if (!SameUnit(line, link.OrderLine))
        {
            return CheckResult.Fail(code, line.LineId, $"The line is invoiced in '{line.Unit}', order line {link.OrderLine.LineId} is in '{link.OrderLine.Unit}'. Units are not converted, so quantity and price cannot be compared.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
        }

        var how = link.Source == MatchSource.Deterministic ? "matched deterministically" : "proposed by the agent and confirmed by a human";
        return CheckResult.Pass(code, line.LineId, $"Order line {link.OrderLine.LineId}, {how}.")
            with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    /// <summary>Units are compared exactly as written: the policy defines no normalization or conversion for them.</summary>
    public static bool SameUnit(InvoiceLine line, OrderLine orderLine) => string.Equals(line.Unit, orderLine.Unit, StringComparison.Ordinal);

    private static List<PurchaseOrder> CandidateOrders(
        InvoiceDocument document, ContextSnapshot context, Resolution<Supplier> supplier, Resolution<Buyer> buyer)
    {
        if (!supplier.IsFound)
        {
            return [];
        }

        return context.Orders
            .Where(o => o.Status == OrderStatus.Open
                && o.SupplierId == supplier.Value!.SupplierId
                && (!buyer.IsFound || o.BuyerId == buyer.Value!.BuyerId)
                && DeterministicLines(document.Lines, o).Count == document.Lines.Count)
            .ToList();
    }

    private static bool Matches(InvoiceLine line, OrderLine orderLine)
    {
        if (line.Sku is not null && orderLine.Sku is not null
            && Normalize.Identifier(line.Sku) == Normalize.Identifier(orderLine.Sku))
        {
            return true;
        }

        var description = Normalize.Name(line.Description);
        return orderLine.ExplicitAliases.Any(alias => Normalize.Name(alias) == description)
            || Normalize.Name(orderLine.Description) == description;
    }

    private static Dictionary<string, string?> Expected(PurchaseOrder order) =>
        new() { ["order_id"] = order.OrderId, ["reference"] = order.Reference };
}
