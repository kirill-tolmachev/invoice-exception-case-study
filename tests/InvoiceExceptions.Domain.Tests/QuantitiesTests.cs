using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>R3 QTY_RECEIVED / QTY_ORDER_REMAINING and the per-line quantity figures.</summary>
public class QuantitiesTests
{
    [Fact]
    public void Several_receipts_are_summed()
    {
        // case-002: order 100, receipts 30 + 30, invoice 60.
        var a = Scenario.HappyPath()
            .WithReceipts(
                new GoodsReceipt("GR-1", Scenario.Order1, "OL-1", 30m, true),
                new GoodsReceipt("GR-1B", Scenario.Order1, "OL-1", 30m, true),
                new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .WithLine("L1", l => l.WithQuantity(60m))
            .Assess();

        Assert.Equal(60m, a.Line("L1").Received);
        Assert.Equal(60m, a.Line("L1").AvailableToInvoice);
        Expect.Pass(a.Check(CheckCode.QtyReceived, "L1"));
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Receipts_that_are_not_accepted_are_not_counted()
    {
        var a = Scenario.HappyPath()
            .WithReceipts(
                new GoodsReceipt("GR-1", Scenario.Order1, "OL-1", 30m, true),
                new GoodsReceipt("GR-1X", Scenario.Order1, "OL-1", 70m, false),
                new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .Assess();

        Assert.Equal(30m, a.Line("L1").Received);
        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
    }

    [Fact]
    public void Receipts_of_other_order_lines_are_not_counted()
    {
        var a = Scenario.HappyPath()
            .WithReceipts(new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .Assess();

        Assert.Equal(0m, a.Line("L1").Received);
        Assert.Equal(50m, a.Line("L2").Received);
    }

    [Fact]
    public void Partial_invoice_passes()
    {
        var a = Scenario.HappyPath().Assess();

        Assert.Equal(new LineResult("L1", "OL-1", MatchSource.Deterministic, 100.00m, 20.00m, 100m, 100m, 0m, 100m, 100m), a.Line("L1"));
        Expect.Pass(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.Pass(a.Check(CheckCode.QtyOrderRemaining, "L1"));
    }

    [Fact]
    public void Invoicing_exactly_what_is_available_passes()
    {
        var a = Scenario.HappyPath()
            .AddCountedHistory("HIST-1", "INV-0900", Scenario.Order1, ("OL-1", 40m))
            .WithLine("L1", l => l.WithQuantity(60m))
            .Assess();

        Assert.Equal(60m, a.Line("L1").AvailableToInvoice);
        Assert.Equal(60m, a.Line("L1").RemainingOnOrder);
        Expect.Pass(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.Pass(a.Check(CheckCode.QtyOrderRemaining, "L1"));
    }

    [Fact]
    public void Over_invoicing_fails_both_quantity_checks_blocked()
    {
        // case-003: order and receipt 100, previously counted 40, invoice 70.
        var a = Scenario.HappyPath()
            .AddCountedHistory("HIST-1", "INV-0900", Scenario.Order1, ("OL-1", 40m))
            .WithLine("L1", l => l.WithQuantity(70m))
            .Assess();

        Assert.Equal(new LineResult("L1", "OL-1", MatchSource.Deterministic, 175.00m, 35.00m, 100m, 100m, 40m, 60m, 60m), a.Line("L1"));
        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
        Expect.Fail(a.Check(CheckCode.QtyOrderRemaining, "L1"), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.QtyReceived, "L1"), (CheckCode.QtyOrderRemaining, "L1"));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Quantity_beyond_receipts_but_within_order_fails_only_qty_received()
    {
        var a = Scenario.HappyPath()
            .WithReceipts(new GoodsReceipt("GR-1", Scenario.Order1, "OL-1", 30m, true), new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .Assess();

        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
        Expect.Pass(a.Check(CheckCode.QtyOrderRemaining, "L1"));
    }

    [Fact]
    public void Quantity_within_receipts_but_beyond_order_fails_only_qty_order_remaining()
    {
        var a = Scenario.HappyPath()
            .WithReceipts(new GoodsReceipt("GR-1", Scenario.Order1, "OL-1", 120m, true), new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .WithLine("L1", l => l.WithQuantity(110m))
            .Assess();

        Assert.Equal(120m, a.Line("L1").AvailableToInvoice);
        Assert.Equal(100m, a.Line("L1").RemainingOnOrder);
        Expect.Pass(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.Fail(a.Check(CheckCode.QtyOrderRemaining, "L1"), Severity.Blocked);
    }

    [Fact]
    public void Fractional_quantities_are_compared_exactly()
    {
        var a = Scenario.HappyPath()
            .WithReceipts(new GoodsReceipt("GR-1", Scenario.Order1, "OL-1", 40.125m, true), new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .WithLine("L1", l => l.WithQuantity(40.126m))
            .Assess();

        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
    }

    [Fact]
    public void History_of_other_order_lines_other_orders_and_uncounted_documents_is_ignored()
    {
        var s = Scenario.HappyPath()
            .AddCountedHistory("HIST-1", "INV-0901", Scenario.Order1, ("OL-2", 30m))
            .AddCountedHistory("HIST-2", "INV-0902", "ORD-OTHER", ("OL-1", 30m));
        s.AddHistory(Scenario.HistoryFor(s.Document with { DocumentId = "HIST-3", Number = "INV-0903" }, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, counted: false, ("OL-1", 30m)));

        var a = s.Assess();

        Assert.Equal(0m, a.Line("L1").PreviouslyInvoiced);
        Assert.Equal(30m, a.Line("L2").PreviouslyInvoiced);
        Assert.Equal(20m, a.Line("L2").RemainingOnOrder);
    }

    [Fact]
    public void Several_history_documents_are_summed()
    {
        var a = Scenario.HappyPath()
            .AddCountedHistory("HIST-1", "INV-0901", Scenario.Order1, ("OL-1", 10m))
            .AddCountedHistory("HIST-2", "INV-0902", Scenario.Order1, ("OL-1", 15.5m), ("OL-2", 1m))
            .Assess();

        Assert.Equal(25.5m, a.Line("L1").PreviouslyInvoiced);
        Assert.Equal(74.5m, a.Line("L1").AvailableToInvoice);
        Assert.Equal(74.5m, a.Line("L1").RemainingOnOrder);
    }

    [Theory]
    [InlineData("INV-1001")]
    [InlineData(" inv-1001 ")]
    public void History_with_the_same_business_key_is_excluded_from_previously_invoiced(string historyNumber)
    {
        // case-009: same key, different facts -> conflict; its quantities must not be counted against itself.
        var s = Scenario.HappyPath();
        var original = s.Document with { DocumentId = "HIST-ORIG", Number = historyNumber, IssueDate = "2026-09-30" };
        s.AddHistory(Scenario.HistoryFor(original, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, counted: true, ("OL-1", 70m), ("OL-2", 45m)));

        var evaluation = s.Evaluate();

        Assert.Equal(ImportDisposition.Conflict, evaluation.Disposition);
        var a = Assert.IsType<Assessment>(evaluation.Assessment);
        Assert.Equal(0m, a.Line("L1").PreviouslyInvoiced);
        Assert.Equal(0m, a.Line("L2").PreviouslyInvoiced);
        Expect.Pass(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.Pass(a.Check(CheckCode.QtyOrderRemaining, "L2"));
    }

    [Fact]
    public void Receipts_unavailable_makes_qty_received_a_root_unknown_while_qty_order_remaining_is_computed()
    {
        var a = Scenario.HappyPath()
            .WithReceiptsUnavailable()
            .AddCountedHistory("HIST-1", "INV-0900", Scenario.Order1, ("OL-2", 45m))
            .Assess();

        Expect.RootUnknown(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.RootUnknown(a.Check(CheckCode.QtyReceived, "L2"));
        Expect.Pass(a.Check(CheckCode.QtyOrderRemaining, "L1"));
        Expect.Fail(a.Check(CheckCode.QtyOrderRemaining, "L2"), Severity.Blocked);
        Assert.Equal(new LineResult("L1", "OL-1", MatchSource.Deterministic, 100.00m, 20.00m, 100m, null, 0m, null, 100m), a.Line("L1"));
        Assert.Equal(new LineResult("L2", "OL-2", MatchSource.Deterministic, 120.00m, 24.00m, 50m, null, 45m, null, 5m), a.Line("L2"));
        Assert.Equal(
            Expect.Questions((RequiredFact.GoodsReceipts, "L1"), (RequiredFact.GoodsReceipts, "L2")),
            Expect.Questions(a));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Receipts_unavailable_alone_needs_information()
    {
        var a = Scenario.HappyPath().WithReceiptsUnavailable().Assess();

        Expect.AllOthersPass(a, (CheckCode.QtyReceived, "L1"), (CheckCode.QtyReceived, "L2"));
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Receipts_unavailable_does_not_override_an_unresolved_line()
    {
        var a = Scenario.HappyPath()
            .WithReceiptsUnavailable()
            .WithLine("L1", l => l with { Sku = null, Description = "Unrelated" })
            .Assess();

        Expect.DependsOn(a.Check(CheckCode.QtyReceived, "L1"), CheckCode.LineMatch);
        Expect.RootUnknown(a.Check(CheckCode.QtyReceived, "L2"));
    }

    [Fact]
    public void Receipts_unavailable_does_not_override_unit_mismatch()
    {
        var a = Scenario.HappyPath().WithReceiptsUnavailable().WithLine("L1", l => l with { Unit = "kg" }).Assess();

        Expect.NotApplicable(a.Check(CheckCode.QtyReceived, "L1"));
    }

    [Fact]
    public void Empty_receipts_with_availability_available_means_received_zero()
    {
        var a = Scenario.HappyPath().WithReceipts().Assess();

        Assert.Equal(0m, a.Line("L1").Received);
        Assert.Equal(0m, a.Line("L1").AvailableToInvoice);
        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
        Expect.Fail(a.Check(CheckCode.QtyReceived, "L2"), Severity.Blocked);
        Expect.Pass(a.Check(CheckCode.QtyOrderRemaining, "L1"));
        Assert.DoesNotContain(a.Questions, q => q.RequiredFact == RequiredFact.GoodsReceipts);
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Quantities_use_the_order_line_selected_by_an_unconfirmed_proposal()
    {
        var a = Scenario.HappyPath()
            .WithLine("L1", l => (l with { Sku = null, Description = "Unrelated" }).WithQuantity(150m))
            .Propose("P1", Scenario.Order1, confirmed: false, ("L1", "OL-1"))
            .Assess();

        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
        Expect.Fail(a.Check(CheckCode.QtyOrderRemaining, "L1"), Severity.Blocked);
        Assert.Equal(Decision.Blocked, a.Decision);
    }
}
