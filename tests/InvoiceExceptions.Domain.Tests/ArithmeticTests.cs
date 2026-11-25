using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;
using static InvoiceExceptions.Domain.Tests.TestData.SpecMath;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>R4 LINE_ARITHMETIC / TOTALS_ARITHMETIC and the "Нормализация и арифметика" section.</summary>
public class ArithmeticTests
{
    private static Scenario SingleLine(InvoiceLine line) =>
        Scenario.HappyPath().WithLines(line);

    [Theory]
    // quantity, price, rate, calculated_net, calculated_tax
    [InlineData("1.5", "0.03", "0.10", "0.05", "0.01")]     // net 0.045 -> 0.05 (tie up); tax 0.005 -> 0.01 (tie up)
    [InlineData("2.5", "1.13", "0.20", "2.83", "0.57")]     // net 2.825 -> 2.83 (tie, banker's would give 2.82)
    [InlineData("0.125", "10.10", "0.20", "1.26", "0.25")]  // 3-decimal quantity: 1.2625 -> 1.26
    [InlineData("1.255", "2.00", "0.20", "2.51", "0.50")]   // 3-decimal quantity: 2.51; tax 0.502 -> 0.50
    [InlineData("0.375", "0.10", "0.20", "0.04", "0.01")]   // 0.0375 -> 0.04; 0.008 -> 0.01
    [InlineData("3", "0.35", "0.15", "1.05", "0.16")]       // tax 0.1575 -> 0.16
    [InlineData("10.5", "0.05", "0.10", "0.53", "0.05")]    // net 0.525 -> 0.53 (tie, banker's would give 0.52)
    [InlineData("100", "0.35", "0.20", "35.00", "7.00")]    // case-010 L01
    public void Line_amounts_round_half_up_to_two_decimals(string quantity, string price, string rate, string net, string tax)
    {
        var line = new InvoiceLine("L1", "Steel bolt M8", "SKU-A", D(quantity), "pcs", D(price), D(net), D(rate), D(tax));
        var a = SingleLine(line).Assess();

        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L1"));
        Assert.Equal(D(net), a.Line("L1").CalculatedNet);
        Assert.Equal(D(tax), a.Line("L1").CalculatedTax);
    }

    [Theory]
    [InlineData("2.5", "1.13", "2.82")]  // banker's rounding of 2.825
    [InlineData("1.5", "0.03", "0.04")]  // banker's rounding of 0.045
    [InlineData("10.5", "0.05", "0.52")] // banker's rounding of 0.525
    [InlineData("1.5", "0.03", "0.045")] // unrounded value is not the calculated net either
    public void Declared_net_that_used_another_rounding_fails_blocked(string quantity, string price, string declaredNet)
    {
        var q = D(quantity);
        var p = D(price);
        var line = new InvoiceLine("L1", "Steel bolt M8", "SKU-A", q, "pcs", p, D(declaredNet), 0.20m, null);
        var a = SingleLine(line).Assess();

        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L1"), Severity.Blocked);
        Assert.Equal(Net(q, p), a.Line("L1").CalculatedNet);
    }

    [Theory]
    [InlineData("0.01", true)]   // round(round(0.045) x 0.10) = round(0.005) = 0.01
    [InlineData("0.00", false)]  // round(0.045 x 0.10) = round(0.0045) = 0.00 would be tax on the unrounded net
    public void Tax_is_computed_from_the_rounded_net(string declaredTax, bool passes)
    {
        var line = new InvoiceLine("L1", "Steel bolt M8", "SKU-A", 1.5m, "pcs", 0.03m, 0.05m, 0.10m, D(declaredTax));
        var a = SingleLine(line).Assess();

        Assert.Equal(0.01m, a.Line("L1").CalculatedTax);
        var check = a.Check(CheckCode.LineArithmetic, "L1");
        if (passes)
        {
            Expect.Pass(check);
        }
        else
        {
            Expect.Fail(check, Severity.Blocked);
        }
    }

    [Fact]
    public void Without_declared_tax_only_the_net_is_checked_and_tax_is_still_calculated()
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { DeclaredTaxAmount = null }).Assess();

        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L1"));
        Assert.Equal(20.00m, a.Line("L1").CalculatedTax);
        Assert.Equal(44.00m, a.Totals.CalculatedTaxTotal);
        Expect.Pass(a.Doc(CheckCode.TotalsArithmetic));
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Without_declared_tax_a_wrong_net_still_fails()
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { DeclaredTaxAmount = null, DeclaredNetAmount = 100.01m }).Assess();

        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L1"), Severity.Blocked);
    }

    [Fact]
    public void Wrong_declared_line_tax_fails_blocked()
    {
        var a = Scenario.HappyPath().WithLine("L2", l => l with { DeclaredTaxAmount = 24.01m }).Assess();

        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L2"), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.LineArithmetic, "L2"));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Wrong_declared_line_net_fails_blocked_and_calculated_values_are_still_reported()
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { DeclaredNetAmount = 999.99m, DeclaredTaxAmount = 199.99m }).Assess();

        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L1"), Severity.Blocked);
        Assert.Equal(100.00m, a.Line("L1").CalculatedNet);
        Assert.Equal(20.00m, a.Line("L1").CalculatedTax);
        Assert.Equal(new Totals(220.00m, 44.00m, 264.00m), a.Totals);
    }

    [Fact]
    public void Totals_are_sums_of_calculated_not_declared_line_amounts()
    {
        // L1 declares 100.01 (wrong); the document totals are declared from the calculated values (220.00 / 44.00 / 264.00).
        var a = Scenario.HappyPath().WithLine("L1", l => l with { DeclaredNetAmount = 100.01m }).Assess();

        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L1"), Severity.Blocked);
        Expect.Pass(a.Doc(CheckCode.TotalsArithmetic));
    }

    [Fact]
    public void Tax_total_is_the_sum_of_rounded_line_taxes()
    {
        // Two lines with net 0.05 and rate 0.10: each tax is 0.01, so the tax total is 0.02 (not round(0.10 x 0.10) = 0.01).
        var s = Scenario.HappyPath().WithLines(
            Scenario.Line("L1", "Steel bolt M8", "SKU-A", 1.5m, "pcs", 0.03m, 0.10m),
            Scenario.Line("L2", "Wood screw 4x40", "SKU-B", 1.5m, "box", 0.03m, 0.10m));
        s.AutoTotals = false;
        s.WithDocument(d => d with { DeclaredSubtotal = 0.10m, DeclaredTaxTotal = 0.02m, DeclaredTotal = 0.12m });

        var a = s.Assess();

        Assert.Equal(new Totals(0.10m, 0.02m, 0.12m), a.Totals);
        Expect.Pass(a.Doc(CheckCode.TotalsArithmetic));
    }

    [Theory]
    [InlineData("subtotal")]
    [InlineData("tax_total")]
    [InlineData("total")]
    public void Each_declared_total_fails_on_its_own(string which)
    {
        var s = Scenario.HappyPath();
        s.AutoTotals = false;
        s.WithDocument(d => which switch
        {
            "subtotal" => d with { DeclaredSubtotal = 220.01m },
            "tax_total" => d with { DeclaredTaxTotal = 43.99m },
            _ => d with { DeclaredTotal = 264.01m },
        });

        var a = s.Assess();

        Expect.Fail(a.Doc(CheckCode.TotalsArithmetic), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.TotalsArithmetic, Scenario.DocumentId));
        Assert.Equal(Decision.Blocked, a.Decision);
        Assert.Equal(new Totals(220.00m, 44.00m, 264.00m), a.Totals);
    }

    [Fact]
    public void Total_that_is_not_subtotal_plus_tax_fails_even_when_subtotal_and_tax_are_right()
    {
        // case-005: lines and subtotal right, declared total != subtotal + VAT total.
        var s = Scenario.HappyPath();
        s.AutoTotals = false;
        s.WithDocument(d => d with { DeclaredTotal = 254.00m });

        var a = s.Assess();

        Expect.Fail(a.Doc(CheckCode.TotalsArithmetic), Severity.Blocked);
        Assert.Equal(264.00m, a.Totals.CalculatedTotal);
    }

    [Fact]
    public void Declared_amounts_with_more_trailing_zeros_compare_as_decimals()
    {
        var s = Scenario.HappyPath();
        s.AutoTotals = false;
        s.WithLine("L1", l => l with { DeclaredNetAmount = 100.000m, DeclaredTaxAmount = 20.0m });
        s.WithDocument(d => d with { DeclaredSubtotal = 220m, DeclaredTaxTotal = 44.000m, DeclaredTotal = 264.0m });

        var a = s.Assess();

        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L1"));
        Expect.Pass(a.Doc(CheckCode.TotalsArithmetic));
    }

    [Fact]
    public void Line_arithmetic_uses_the_document_price_and_rate_not_the_order_ones()
    {
        // Price 2.51 (within tolerance) and rate 0.10 (order says 0.20): the line is right with its own figures.
        var a = Scenario.HappyPath()
            .WithLine("L1", l => (l with { TaxRate = 0.10m }).WithPrice(2.51m))
            .Assess();

        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L1"));
        Assert.Equal(100.40m, a.Line("L1").CalculatedNet);
        Assert.Equal(10.04m, a.Line("L1").CalculatedTax);
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Line_and_totals_arithmetic_are_computed_even_when_no_order_is_selected()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithLine("L1", l => l with { DeclaredNetAmount = 1m }).Assess();

        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L1"), Severity.Blocked);
        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L2"));
        Expect.Pass(a.Doc(CheckCode.TotalsArithmetic));
        Assert.Equal(100.00m, a.Line("L1").CalculatedNet);
    }
}

/// <summary>R4 UNIT_PRICE.</summary>
public class UnitPriceTests
{
    [Theory]
    [InlineData("2.50")]
    [InlineData("2.49")]
    [InlineData("2.51")]
    public void Price_within_one_cent_inclusive_passes(string price)
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l.WithPrice(SpecMath.D(price))).Assess();

        Expect.Pass(a.Check(CheckCode.UnitPrice, "L1"));
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Theory]
    [InlineData("2.48")]
    [InlineData("2.52")]
    [InlineData("3.00")]
    public void Price_two_cents_or_more_away_fails_needs_review(string price)
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l.WithPrice(SpecMath.D(price))).Assess();

        Expect.Fail(a.Check(CheckCode.UnitPrice, "L1"), Severity.NeedsReview);
        Expect.AllOthersPass(a, (CheckCode.UnitPrice, "L1"));
        Assert.Equal(Decision.NeedsReview, a.Decision);
    }

    [Fact]
    public void Tolerance_is_symmetric_for_a_higher_order_price()
    {
        // Order price above the invoice price: order 12.02, invoice 12.00 -> 0.02 away.
        var a = Scenario.HappyPath().WithOrderLine(Scenario.Order1, "OL-2", l => l with { UnitNetPrice = 12.02m }).Assess();

        Expect.Fail(a.Check(CheckCode.UnitPrice, "L2"), Severity.NeedsReview);
    }
}
