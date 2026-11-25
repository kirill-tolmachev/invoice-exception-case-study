using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Rules;

namespace InvoiceExceptions.Domain;

/// <summary>A reason a proposal cannot be registered. <paramref name="Path"/> is a JSON Pointer relative to the proposal.</summary>
public sealed record ProposalIssue(string Path, string Message);

/// <summary>
/// What an agent is allowed to propose. A proposal may only fill a gap the deterministic rules leave open;
/// it can never override what the data already says.
/// </summary>
public static class ProposalRules
{
    public static IReadOnlyList<ProposalIssue> Validate(ContextSnapshot context, InvoiceDocument document, MatchProposal proposal)
    {
        var issues = new List<ProposalIssue>();

        var order = context.Orders.FirstOrDefault(o => o.OrderId == proposal.OrderId);
        if (order is null)
        {
            issues.Add(new("/order_id", $"Order '{proposal.OrderId}' does not exist in context {context.ContextVersion}."));
            return issues;
        }

        var supplier = Parties.ResolveSupplier(document, context);
        var buyer = Parties.ResolveBuyer(document, context);
        if (OrderMatching.DeterministicOrder(document, context, supplier, buyer) is { } deterministic
            && deterministic.OrderId != order.OrderId)
        {
            issues.Add(new("/order_id", $"The proposal contradicts the deterministic match: the document resolves to order '{deterministic.OrderId}'."));
        }

        var documentLineIds = document.Lines.Select(l => l.LineId).ToHashSet();
        var orderLineIds = order.Lines.Select(l => l.LineId).ToHashSet();
        var deterministicLines = OrderMatching.DeterministicLines(document.Lines, order);
        var takenDeterministically = deterministicLines.ToDictionary(pair => pair.Value.LineId, pair => pair.Key);
        var seenDocumentLines = new HashSet<string>();
        var seenOrderLines = new HashSet<string>();

        for (var i = 0; i < proposal.LineMatches.Count; i++)
        {
            var match = proposal.LineMatches[i];
            var path = $"/line_matches/{i}";

            if (!documentLineIds.Contains(match.DocumentLineId))
            {
                issues.Add(new($"{path}/document_line_id", $"Document {document.DocumentId} has no line '{match.DocumentLineId}'."));
            }
            else if (!seenDocumentLines.Add(match.DocumentLineId))
            {
                issues.Add(new($"{path}/document_line_id", $"Line '{match.DocumentLineId}' is matched more than once in this proposal."));
            }

            if (!orderLineIds.Contains(match.OrderLineId))
            {
                issues.Add(new($"{path}/order_line_id", $"Order {order.OrderId} has no line '{match.OrderLineId}'."));
                continue;
            }

            if (!seenOrderLines.Add(match.OrderLineId))
            {
                issues.Add(new($"{path}/order_line_id", $"Order line '{match.OrderLineId}' is used more than once in this proposal; matching is one-to-one."));
            }

            if (deterministicLines.TryGetValue(match.DocumentLineId, out var matched))
            {
                if (matched.LineId != match.OrderLineId)
                {
                    issues.Add(new($"{path}/order_line_id", $"The proposal contradicts the deterministic match: line '{match.DocumentLineId}' matches order line '{matched.LineId}'."));
                }
            }
            else if (takenDeterministically.TryGetValue(match.OrderLineId, out var owner))
            {
                issues.Add(new($"{path}/order_line_id", $"The proposal contradicts the deterministic match: order line '{match.OrderLineId}' already belongs to document line '{owner}'."));
            }
        }

        return issues;
    }
}
