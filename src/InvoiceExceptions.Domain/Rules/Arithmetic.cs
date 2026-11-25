using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

internal sealed record LineAmounts(decimal Net, decimal Tax);

/// <summary>R4. Unit price against the order, and the document's own arithmetic.</summary>
internal static class Arithmetic
{
    /// <summary>Recomputed from the document's own quantity, price and rate. Whether the rate is the right one is not checked.</summary>
    public static LineAmounts Amounts(InvoiceLine line)
    {
        var net = Money.NetAmount(line.Quantity, line.UnitNetPrice);
        return new LineAmounts(net, Money.TaxAmount(net, line.TaxRate));
    }

    public static Totals Sum(IEnumerable<LineAmounts> lines)
    {
        var subtotal = 0.00m;
        var taxTotal = 0.00m;
        foreach (var line in lines)
        {
            subtotal += line.Net;
            taxTotal += line.Tax;
        }

        return new Totals(subtotal, taxTotal, subtotal + taxTotal);
    }

    public static CheckResult UnitPrice(InvoiceLine line, int index, OrderSelection order, LineLink? link)
    {
        const CheckCode code = CheckCode.UnitPrice;
        if (Quantities.Unresolved(code, line, index, order, link) is { } unresolved)
        {
            return unresolved;
        }

        string[] refs = [$"/lines/{index}/unit_net_price"];
        var orderPrice = link!.OrderLine.UnitNetPrice;
        var difference = Math.Abs(line.UnitNetPrice - orderPrice);
        var actual = new Dictionary<string, string?> { ["unit_net_price"] = Wire.Amount(line.UnitNetPrice) };
        var expected = new Dictionary<string, string?>
        {
            ["unit_net_price"] = Wire.Amount(orderPrice),
            ["tolerance"] = Wire.Amount(CoreV1.UnitPriceTolerance),
        };

        // The boundary is inclusive and the tolerance symmetric: cheaper than ordered is reviewed just like dearer.
        return difference <= CoreV1.UnitPriceTolerance
            ? CheckResult.Pass(code, line.LineId, $"Unit price {Wire.Amount(line.UnitNetPrice)} is within {Wire.Amount(CoreV1.UnitPriceTolerance)} of the order price {Wire.Amount(orderPrice)}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, line.LineId, $"Unit price {Wire.Amount(line.UnitNetPrice)} differs from the order price {Wire.Amount(orderPrice)} by {Wire.Amount(difference)}; the tolerance is {Wire.Amount(CoreV1.UnitPriceTolerance)}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    public static CheckResult LineArithmetic(InvoiceLine line, int index, LineAmounts calculated)
    {
        const CheckCode code = CheckCode.LineArithmetic;
        string[] refs =
        [
            $"/lines/{index}/quantity", $"/lines/{index}/unit_net_price", $"/lines/{index}/declared_net_amount",
            $"/lines/{index}/tax_rate", $"/lines/{index}/declared_tax_amount",
        ];
        var actual = new Dictionary<string, string?>
        {
            ["declared_net_amount"] = Wire.Amount(line.DeclaredNetAmount),
            ["declared_tax_amount"] = line.DeclaredTaxAmount is { } declaredTax ? Wire.Amount(declaredTax) : null,
        };
        var expected = new Dictionary<string, string?>
        {
            ["calculated_net"] = Wire.Amount(calculated.Net),
            ["calculated_tax"] = Wire.Amount(calculated.Tax),
        };

        var problems = new List<string>();
        if (calculated.Net != line.DeclaredNetAmount)
        {
            problems.Add($"net: {Wire.Number(line.Quantity)} x {Wire.Amount(line.UnitNetPrice)} = {Wire.Amount(calculated.Net)}, declared {Wire.Amount(line.DeclaredNetAmount)}");
        }

        // A line may legitimately not print its tax amount; then only the net amount is checked.
        if (line.DeclaredTaxAmount is { } tax && calculated.Tax != tax)
        {
            problems.Add($"tax: {Wire.Amount(calculated.Net)} x {Wire.Number(line.TaxRate)} = {Wire.Amount(calculated.Tax)}, declared {Wire.Amount(tax)}");
        }

        return problems.Count == 0
            ? CheckResult.Pass(code, line.LineId, $"{Wire.Number(line.Quantity)} x {Wire.Amount(line.UnitNetPrice)} = {Wire.Amount(calculated.Net)} net, {Wire.Amount(calculated.Tax)} tax, as declared.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, line.LineId, $"The line does not add up - {string.Join("; ", problems)}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };
    }

    public static CheckResult TotalsArithmetic(InvoiceDocument document, Totals calculated)
    {
        const CheckCode code = CheckCode.TotalsArithmetic;
        string[] refs = ["/declared_subtotal", "/declared_tax_total", "/declared_total"];
        var actual = new Dictionary<string, string?>
        {
            ["declared_subtotal"] = Wire.Amount(document.DeclaredSubtotal),
            ["declared_tax_total"] = Wire.Amount(document.DeclaredTaxTotal),
            ["declared_total"] = Wire.Amount(document.DeclaredTotal),
        };
        var expected = new Dictionary<string, string?>
        {
            ["calculated_subtotal"] = Wire.Amount(calculated.CalculatedSubtotal),
            ["calculated_tax_total"] = Wire.Amount(calculated.CalculatedTaxTotal),
            ["calculated_total"] = Wire.Amount(calculated.CalculatedTotal),
        };

        var problems = new List<string>();
        Compare("subtotal", calculated.CalculatedSubtotal, document.DeclaredSubtotal);
        Compare("tax total", calculated.CalculatedTaxTotal, document.DeclaredTaxTotal);
        Compare("total", calculated.CalculatedTotal, document.DeclaredTotal);

        return problems.Count == 0
            ? CheckResult.Pass(code, document.DocumentId, $"Totals add up: {Wire.Amount(calculated.CalculatedSubtotal)} + {Wire.Amount(calculated.CalculatedTaxTotal)} = {Wire.Amount(calculated.CalculatedTotal)}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs }
            : CheckResult.Fail(code, document.DocumentId, $"The totals do not add up - {string.Join("; ", problems)}.")
                with { Actual = actual, Expected = expected, EvidenceRefs = refs };

        void Compare(string name, decimal calculatedValue, decimal declaredValue)
        {
            if (calculatedValue != declaredValue)
            {
                problems.Add($"{name}: calculated {Wire.Amount(calculatedValue)}, declared {Wire.Amount(declaredValue)}");
            }
        }
    }
}
