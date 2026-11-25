using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>
/// Implementation choices where BUSINESS_RULES.md and core-v1.json are silent. These tests pin the engine author's
/// decisions, not the spec: if the spec is clarified, these are the tests to revisit.
/// </summary>
public class SpecSilentChoicesTests
{
    // ---- Units of measure are compared exactly as written ----------------------------------------------------

    [Theory]
    [InlineData("PCS")]
    [InlineData(" pcs")]
    [InlineData("pcs ")]
    [InlineData("pc")]
    public void Units_are_compared_exactly_without_trimming_or_case_folding(string unit)
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { Unit = unit }).Assess();

        Expect.Fail(a.Check(CheckCode.LineMatch, "L1"), Severity.NeedsReview);
        Expect.NotApplicable(a.Check(CheckCode.UnitPrice, "L1"));
    }

    // ---- Deterministic line match is a union of SKU / alias / description ------------------------------------

    [Fact]
    public void Sku_and_description_pointing_to_the_same_order_line_match()
    {
        var a = Scenario.HappyPath().Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
    }

    [Fact]
    public void Sku_and_description_pointing_to_different_order_lines_is_ambiguous_not_sku_first()
    {
        // SKU-A -> OL-1, description "Wood screw 4x40" -> OL-2: two candidates, so no deterministic match.
        var a = Scenario.HappyPath()
            .WithLines(Scenario.Line("L1", "Wood screw 4x40", "SKU-A", 40m, "pcs", 2.50m))
            .Assess();

        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L1"));
        Assert.Null(a.Line("L1").OrderLineId);
    }

    [Fact]
    public void Sku_and_alias_pointing_to_different_order_lines_is_ambiguous()
    {
        var a = Scenario.HappyPath()
            .WithLines(Scenario.Line("L1", "Bolt M8 steel", "SKU-B", 10m, "box", 12.00m))
            .Assess();

        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L1"));
    }

    [Fact]
    public void Two_document_lines_with_the_same_single_candidate_are_both_unmatched()
    {
        var a = Scenario.HappyPath()
            .WithLines(
                Scenario.Line("L1", "Steel bolt M8", "SKU-A", 10m, "pcs", 2.50m),
                Scenario.Line("L2", "Steel bolt M8", null, 10m, "pcs", 2.50m))
            .Assess();

        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L1"));
        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L2"));
        Assert.Null(a.Line("L1").OrderLineId);
        Assert.Null(a.Line("L2").OrderLineId);
    }

    [Fact]
    public void Order_with_two_document_lines_competing_for_one_order_line_is_not_a_candidate()
    {
        var a = Scenario.HappyPath()
            .WithoutReference()
            .WithLines(
                Scenario.Line("L1", "Steel bolt M8", "SKU-A", 10m, "pcs", 2.50m),
                Scenario.Line("L2", "Steel bolt M8", null, 10m, "pcs", 2.50m))
            .Assess();

        Expect.RootUnknown(a.Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void Order_line_taken_by_another_document_line_is_not_eliminated_from_the_candidates()
    {
        // L1 -> {OL-1} by SKU; L2 -> {OL-1 by description, OL-2 by SKU}. core-v1.json says "exactly one order line matches
        // ..., not taken by another document line"; the engine reads it as "the candidate set has exactly one element"
        // (so L2 stays unmatched), not as "remove lines taken by others, then require exactly one" (which would give OL-2).
        var a = Scenario.HappyPath().WithLine("L2", l => l with { Description = "Steel bolt M8" }).Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L2"));
        Assert.Null(a.Line("L2").OrderLineId);
    }

    // ---- Receipts and history are attributed by order id AND order line id ------------------------------------

    [Fact]
    public void Receipt_with_the_same_order_line_id_but_another_order_is_not_counted()
    {
        var a = Scenario.HappyPath()
            .WithReceipts(
                new GoodsReceipt("GR-X", "ORD-OTHER", "OL-1", 100m, true),
                new GoodsReceipt("GR-2", Scenario.Order1, "OL-2", 50m, true))
            .Assess();

        Assert.Equal(0m, a.Line("L1").Received);
        Expect.Fail(a.Check(CheckCode.QtyReceived, "L1"), Severity.Blocked);
    }

    [Fact]
    public void History_line_with_the_same_order_line_id_but_another_order_is_not_counted()
    {
        var a = Scenario.HappyPath().AddCountedHistory("HIST-1", "INV-0900", "ORD-OTHER", ("OL-1", 90m)).Assess();

        Assert.Equal(0m, a.Line("L1").PreviouslyInvoiced);
    }

    // ---- Several proposals for one document --------------------------------------------------------------------

    private static Scenario TwoCandidateOrders() =>
        Scenario.HappyPath().WithoutReference().CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002");

    [Fact]
    public void Most_recent_confirmed_proposal_selects_the_order_over_a_newer_unconfirmed_one()
    {
        var a = TwoCandidateOrders()
            .Propose("P1", "ORD-1B", confirmed: true)
            .Propose("P2", Scenario.Order1, confirmed: false)
            .Assess();

        Assert.Equal("ORD-1B", a.MatchedOrderId);
        Assert.Equal(MatchSource.HumanConfirmation, a.OrderMatchSource);
    }

    [Fact]
    public void Of_two_confirmed_proposals_the_most_recent_selects_the_order()
    {
        var a = TwoCandidateOrders()
            .Propose("P1", "ORD-1B", confirmed: true)
            .Propose("P2", Scenario.Order1, confirmed: true)
            .Assess();

        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Assert.Equal(MatchSource.HumanConfirmation, a.OrderMatchSource);
    }

    [Fact]
    public void Without_confirmation_the_most_recent_unconfirmed_proposal_selects_the_order()
    {
        var a = TwoCandidateOrders()
            .Propose("P1", Scenario.Order1, confirmed: false)
            .Propose("P2", "ORD-1B", confirmed: false)
            .Assess();

        Assert.Equal("ORD-1B", a.MatchedOrderId);
        Assert.Equal(MatchSource.AgentProposal, a.OrderMatchSource);
    }

    private static Scenario ExtraOrderLineAndUnmatchedLines() =>
        Scenario.HappyPath()
            .WithOrder(Scenario.Order1, o => o with { Lines = [.. o.Lines, new OrderLine("OL-3", "SKU-C", "Washer M8", [], 100m, "pcs", 2.50m, 0.20m)] })
            .WithLine("L1", l => l with { Sku = null, Description = "Unmatched one" })
            .WithLine("L2", l => (l with { Sku = null, Description = "Unmatched two", Unit = "pcs" }).WithPrice(2.50m));

    [Fact]
    public void For_a_line_a_confirmed_proposal_outranks_a_newer_pending_one()
    {
        var a = ExtraOrderLineAndUnmatchedLines()
            .Propose("P1", Scenario.Order1, confirmed: true, ("L1", "OL-1"))
            .Propose("P2", Scenario.Order1, confirmed: false, ("L1", "OL-3"))
            .Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Assert.Equal(MatchSource.HumanConfirmation, a.Line("L1").MatchSource);
    }

    [Fact]
    public void An_order_line_is_never_assigned_to_two_document_lines_across_proposals()
    {
        var a = ExtraOrderLineAndUnmatchedLines()
            .Propose("P1", Scenario.Order1, confirmed: true, ("L1", "OL-1"))
            .Propose("P2", Scenario.Order1, confirmed: false, ("L2", "OL-1"))
            .Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Assert.Null(a.Line("L2").OrderLineId);
        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L2"));
    }

    // ---- Ambiguous directory / reference lookups count as not found -------------------------------------------

    [Fact]
    public void Supplier_registration_id_matching_two_directory_entries_counts_as_not_found()
    {
        var s = Scenario.HappyPath();
        s.Suppliers.Add(new Supplier("SUP-1-DUP", "Nordic Tools AB (dup)", " se-reg-001", [Scenario.Supplier1Iban]));

        var a = s.Assess();

        Expect.Fail(a.Doc(CheckCode.SupplierIdentity), Severity.Blocked);
    }

    [Fact]
    public void Po_reference_matching_two_orders_counts_as_not_found()
    {
        var a = Scenario.HappyPath().CloneOrder(Scenario.Order1, "ORD-1B", "po-1001 ").Assess();

        Expect.RootUnknown(a.Doc(CheckCode.OrderMatch));
        Assert.Contains(a.Questions, q => q.RequiredFact == RequiredFact.OrderReference);
    }

    // ---- Business key matching several earlier documents -------------------------------------------------------

    [Fact]
    public void An_equal_document_among_several_key_matches_makes_it_a_duplicate()
    {
        var s = Scenario.HappyPath();
        var doc = s.BuiltDocument();
        s.AddHistory(Scenario.HistoryFor(doc with { DocumentId = "HIST-A", IssueDate = "2026-09-01" }, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, true));
        s.AddEarlier(doc with { DocumentId = "DOC-0" }, "ASM-0");

        var evaluation = s.Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Equal("DOC-0", evaluation.OriginalDocumentId);
    }

    [Fact]
    public void Without_an_equal_match_the_conflict_names_the_first_match_with_history_before_case_documents()
    {
        var s = Scenario.HappyPath();
        var doc = s.BuiltDocument();
        s.AddEarlier(doc with { DocumentId = "DOC-0", IssueDate = "2026-09-03" });
        s.AddHistory(Scenario.HistoryFor(doc with { DocumentId = "HIST-A", IssueDate = "2026-09-01" }, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, true));
        s.AddHistory(Scenario.HistoryFor(doc with { DocumentId = "HIST-B", IssueDate = "2026-09-02" }, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, true));

        var evaluation = s.Evaluate();

        Assert.Equal(ImportDisposition.Conflict, evaluation.Disposition);
        Assert.Equal("HIST-A", evaluation.OriginalDocumentId);
    }
}
