using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;
using InvoiceExceptions.Domain.Rules;

namespace InvoiceExceptions.Domain;

/// <summary>
/// The rules engine of policy core-v1: a pure function from facts, context and proposals to a conclusion.
/// It reads no clock, no file and no network, and it never sees who is asking - an agent cannot talk it into anything.
/// </summary>
public static class InvoiceEvaluator
{
    public static Evaluation Evaluate(EvaluationInput input)
    {
        var document = input.Document;
        var context = input.Context;

        // R1: who is on the document. Everything below builds on these two lookups.
        var supplier = Parties.ResolveSupplier(document, context);
        var buyer = Parties.ResolveBuyer(document, context);

        // R6 comes first: a duplicate is not assessed again, the result just points at the original.
        var conflict = Duplicates.Classify(document, supplier, buyer, context, input.EarlierDocuments);
        if (conflict.Disposition == ImportDisposition.Duplicate)
        {
            return Evaluation.Duplicate(conflict.OriginalDocumentId!, conflict.ExistingAssessmentRef);
        }

        // R2: which order, which lines. With an unconfirmed proposal the order is still used for the remaining
        // checks, so the human sees what confirming it would lead to.
        var proposals = input.Proposals.Where(p => p.DocumentId == document.DocumentId).ToList();
        var order = OrderMatching.Select(document, context, supplier, buyer, proposals);
        var links = order.Order is null
            ? new Dictionary<string, LineLink>()
            : OrderMatching.LinkLines(document, order.Order, proposals);

        // R3 + R4 per line.
        var lineResults = new List<LineResult>(document.Lines.Count);
        var lineChecks = CoreV1.LineChecks.ToDictionary(code => code, _ => new List<CheckResult>(document.Lines.Count));
        var amounts = new List<LineAmounts>(document.Lines.Count);
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            var link = links.GetValueOrDefault(line.LineId);
            var figures = link is null ? null : Quantities.For(order.Order!, link.OrderLine, context, conflict.Key);
            var calculated = Arithmetic.Amounts(line);
            amounts.Add(calculated);

            lineResults.Add(new LineResult(
                line.LineId,
                link?.OrderLine.LineId,
                link?.Source,
                calculated.Net,
                calculated.Tax,
                figures?.Ordered,
                figures?.Received,
                figures?.PreviouslyInvoiced,
                figures?.AvailableToInvoice,
                figures?.RemainingOnOrder));

            lineChecks[CheckCode.LineMatch].Add(OrderMatching.LineMatch(line, index, order, link));
            lineChecks[CheckCode.QtyReceived].Add(Quantities.Received(line, index, order, link, figures));
            lineChecks[CheckCode.QtyOrderRemaining].Add(Quantities.OrderRemaining(line, index, order, link, figures));
            lineChecks[CheckCode.UnitPrice].Add(Arithmetic.UnitPrice(line, index, order, link));
            lineChecks[CheckCode.LineArithmetic].Add(Arithmetic.LineArithmetic(line, index, calculated));
        }

        var totals = Arithmetic.Sum(amounts);
        var documentChecks = new Dictionary<CheckCode, CheckResult>
        {
            [CheckCode.DocumentConflict] = conflict.Check!,
            [CheckCode.SupplierIdentity] = Parties.SupplierIdentity(document, supplier, order),
            [CheckCode.BuyerIdentity] = Parties.BuyerIdentity(document, buyer, order),
            [CheckCode.OrderMatch] = order.Check,
            [CheckCode.CurrencyMatch] = Parties.CurrencyMatch(document, order),
            [CheckCode.TotalsArithmetic] = Arithmetic.TotalsArithmetic(document, totals),
            [CheckCode.BankAccount] = BankAccount.Check(document, supplier),
        };

        // The set of checks is fixed: 7 for the document and 5 for every line, whatever the outcome.
        var checks = new List<CheckResult>(CoreV1.DocumentChecks.Count + CoreV1.LineChecks.Count * document.Lines.Count);
        foreach (var code in CoreV1.EvaluationOrder)
        {
            if (documentChecks.TryGetValue(code, out var documentCheck))
            {
                checks.Add(documentCheck);
            }
            else
            {
                checks.AddRange(lineChecks[code]);
            }
        }

        var assessment = new Assessment(
            order.Order?.OrderId,
            order.Source,
            lineResults,
            totals,
            checks,
            DecisionPolicy.Decide(checks),
            DecisionPolicy.Questions(checks));

        return Evaluation.Assessed(conflict.Disposition, conflict.OriginalDocumentId, assessment);
    }
}
