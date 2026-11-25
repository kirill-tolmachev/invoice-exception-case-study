using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>R2 ORDER_MATCH.</summary>
public class OrderMatchTests
{
    private const string Doc = Scenario.DocumentId;

    [Fact]
    public void Reference_found_passes_deterministically()
    {
        var a = Scenario.HappyPath().Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Assert.Equal(MatchSource.Deterministic, a.OrderMatchSource);
    }

    [Theory]
    [InlineData("  po-1001  ")]
    [InlineData("Po-1001")]
    public void Reference_is_normalized_by_trim_and_uppercase(string reference)
    {
        var a = Scenario.HappyPath().WithReference(reference).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
    }

    [Fact]
    public void Reference_normalization_keeps_separators()
    {
        Expect.RootUnknown(Scenario.HappyPath().WithReference("PO1001").Assess().Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void Reference_matching_a_closed_order_still_passes()
    {
        var a = Scenario.HappyPath().WithOrder(Scenario.Order1, o => o with { Status = OrderStatus.Closed }).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
    }

    [Fact]
    public void Reference_matching_an_order_of_another_supplier_passes_and_supplier_identity_fails()
    {
        var a = Scenario.HappyPath()
            .CloneOrder(Scenario.Order1, "ORD-9", "PO-9009", o => o with { SupplierId = Scenario.Supplier2 })
            .WithReference("PO-9009")
            .Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal("ORD-9", a.MatchedOrderId);
        Expect.Fail(a.Doc(CheckCode.SupplierIdentity), Severity.Blocked);
    }

    [Fact]
    public void Reference_not_found_is_a_root_unknown_and_everything_order_dependent_depends_on_it()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess();

        Expect.RootUnknown(a.Doc(CheckCode.OrderMatch));
        Assert.Null(a.MatchedOrderId);
        Assert.Null(a.OrderMatchSource);
        Expect.DependsOn(a.Doc(CheckCode.SupplierIdentity), CheckCode.OrderMatch);
        Expect.DependsOn(a.Doc(CheckCode.BuyerIdentity), CheckCode.OrderMatch);
        Expect.DependsOn(a.Doc(CheckCode.CurrencyMatch), CheckCode.OrderMatch);
        foreach (var line in new[] { "L1", "L2" })
        {
            Expect.DependsOn(a.Check(CheckCode.LineMatch, line), CheckCode.OrderMatch);
            Expect.DependsOn(a.Check(CheckCode.QtyReceived, line), CheckCode.OrderMatch);
            Expect.DependsOn(a.Check(CheckCode.QtyOrderRemaining, line), CheckCode.OrderMatch);
            Expect.DependsOn(a.Check(CheckCode.UnitPrice, line), CheckCode.OrderMatch);
            Expect.Pass(a.Check(CheckCode.LineArithmetic, line));
            Assert.Equal(new LineResult(line, null, null, a.Line(line).CalculatedNet, a.Line(line).CalculatedTax, null, null, null, null, null), a.Line(line));
        }

        Expect.Pass(a.Doc(CheckCode.DocumentConflict));
        Expect.Pass(a.Doc(CheckCode.BankAccount));
        Expect.Pass(a.Doc(CheckCode.TotalsArithmetic));
        Assert.Equal(Expect.Questions((RequiredFact.OrderReference, Doc)), Expect.Questions(a));
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Reference_not_found_even_if_lines_would_identify_a_unique_candidate()
    {
        // Step 2 (candidates) applies only when the document has no reference.
        Expect.RootUnknown(Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess().Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void Reference_is_searched_among_all_orders_regardless_of_supplier_buyer_and_status()
    {
        var a = Scenario.HappyPath()
            .CloneOrder(Scenario.Order1, "ORD-9", "PO-9009", o => o with { SupplierId = Scenario.Supplier2, BuyerId = Scenario.Buyer2, Status = OrderStatus.Closed })
            .WithReference("po-9009")
            .Assess();

        Assert.Equal("ORD-9", a.MatchedOrderId);
        Expect.Pass(a.Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void No_reference_and_one_open_candidate_passes_deterministically()
    {
        var a = Scenario.HappyPath().WithoutReference().Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Assert.Equal(MatchSource.Deterministic, a.OrderMatchSource);
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void No_reference_and_several_candidates_is_a_root_unknown()
    {
        // case-010 step 1 shape.
        var a = Scenario.HappyPath().WithoutReference().CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002").Assess();

        Expect.RootUnknown(a.Doc(CheckCode.OrderMatch));
        Assert.Null(a.MatchedOrderId);
        Expect.DependsOn(a.Doc(CheckCode.SupplierIdentity), CheckCode.OrderMatch);
        Expect.DependsOn(a.Check(CheckCode.LineMatch, "L1"), CheckCode.OrderMatch);
        Assert.Equal(Expect.Questions((RequiredFact.OrderReference, Doc)), Expect.Questions(a));
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void No_reference_and_zero_candidates_is_a_root_unknown()
    {
        var a = Scenario.HappyPath().WithoutReference().WithLine("L1", l => l with { Sku = null, Description = "Something unrelated" }).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.OrderMatch));
        Assert.Null(a.MatchedOrderId);
    }

    [Fact]
    public void Closed_orders_are_not_candidates()
    {
        var a = Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002", o => o with { Status = OrderStatus.Closed })
            .Assess();
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);

        var onlyClosed = Scenario.HappyPath().WithoutReference().WithOrder(Scenario.Order1, o => o with { Status = OrderStatus.Closed }).Assess();
        Expect.RootUnknown(onlyClosed.Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void Orders_of_another_supplier_are_not_candidates()
    {
        var a = Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-9", "PO-9009", o => o with { SupplierId = Scenario.Supplier2 })
            .Assess();

        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Expect.Pass(a.Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void Orders_of_another_buyer_are_not_candidates()
    {
        var a = Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-9", "PO-9009", o => o with { BuyerId = Scenario.Buyer2 })
            .Assess();

        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
    }

    [Fact]
    public void Order_where_not_every_line_matches_is_not_a_candidate()
    {
        var a = Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002", o => o with { Lines = [o.Lines[0]] })
            .Assess();

        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
    }

    [Fact]
    public void No_reference_with_unresolved_buyer_still_selects_the_single_candidate_of_the_supplier()
    {
        // core-v1.json: "exactly one open order of that supplier (and buyer, if resolved)".
        var a = Scenario.HappyPath().WithoutReference().WithBuyer("Nobody Known", null).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Expect.RootUnknown(a.Doc(CheckCode.BuyerIdentity));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("XX-NOT-THERE")]
    public void No_reference_with_unresolved_supplier_depends_on_supplier_identity(string? supplierRegId)
    {
        var a = Scenario.HappyPath().WithoutReference().WithSupplierRegId(supplierRegId).Assess();

        Expect.DependsOn(a.Doc(CheckCode.OrderMatch), CheckCode.SupplierIdentity);
        Assert.Null(a.MatchedOrderId);
        Assert.DoesNotContain(a.Questions, q => q.RequiredFact == RequiredFact.OrderReference);
        Expect.DependsOn(a.Check(CheckCode.LineMatch, "L1"), CheckCode.OrderMatch);
    }

    [Fact]
    public void Unconfirmed_agent_proposal_fails_needs_review_and_other_checks_use_the_proposed_order()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").Propose("P1", Scenario.Order1, confirmed: false).Assess();

        Expect.Fail(a.Doc(CheckCode.OrderMatch), Severity.NeedsReview);
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Assert.Equal(MatchSource.AgentProposal, a.OrderMatchSource);
        Expect.Pass(a.Doc(CheckCode.SupplierIdentity));
        Expect.Pass(a.Doc(CheckCode.BuyerIdentity));
        Expect.Pass(a.Doc(CheckCode.CurrencyMatch));
        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
        Assert.Equal(MatchSource.Deterministic, a.Line("L1").MatchSource);
        Expect.AllOthersPass(a, (CheckCode.OrderMatch, Doc));
        Assert.Empty(a.Questions);
        Assert.Equal(Decision.NeedsReview, a.Decision);
    }

    [Fact]
    public void Human_confirmed_proposal_passes_with_human_confirmation()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").Propose("P1", Scenario.Order1, confirmed: true).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(MatchSource.HumanConfirmation, a.OrderMatchSource);
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Proposal_takes_effect_when_there_are_several_candidates()
    {
        var a = Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002")
            .Propose("P1", "ORD-1B", confirmed: false)
            .Assess();

        Assert.Equal("ORD-1B", a.MatchedOrderId);
        Expect.Fail(a.Doc(CheckCode.OrderMatch), Severity.NeedsReview);
        Assert.Equal("ORD-1B/OL-1", a.Line("L1").OrderLineId);
    }

    [Fact]
    public void Proposal_takes_precedence_over_an_unresolved_supplier_when_there_is_no_reference()
    {
        // core-v1.json lists the proposal outcomes before "no reference; supplier not resolved".
        var a = Scenario.HappyPath().WithoutReference().WithSupplierRegId(null).Propose("P1", Scenario.Order1, confirmed: true).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(MatchSource.HumanConfirmation, a.OrderMatchSource);
        Expect.RootUnknown(a.Doc(CheckCode.SupplierIdentity));
    }

    [Fact]
    public void Deterministic_match_takes_precedence_over_a_proposal_for_the_same_order()
    {
        var a = Scenario.HappyPath().Propose("P1", Scenario.Order1, confirmed: false).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(MatchSource.Deterministic, a.OrderMatchSource);
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Proposals_for_another_document_are_ignored()
    {
        var s = Scenario.HappyPath().WithReference("PO-UNKNOWN");
        s.Proposals.Add(new MatchProposal("P-OTHER", "DOC-OTHER", Scenario.Order1, [], true));

        Expect.RootUnknown(s.Assess().Doc(CheckCode.OrderMatch));
    }
}

/// <summary>R2 LINE_MATCH and its effect on the line checks.</summary>
public class LineMatchTests
{
    private static Scenario OrderByReferenceWithUnmatchedL1() =>
        Scenario.HappyPath().WithLine("L1", l => l with { Sku = null, Description = "Galvanised fixing bolt" });

    [Fact]
    public void Line_matches_by_sku()
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { Description = "Something else entirely" }).Assess();

        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Assert.Equal(MatchSource.Deterministic, a.Line("L1").MatchSource);
    }

    [Theory]
    [InlineData(" sku-a ")]
    [InlineData("Sku-A")]
    public void Sku_is_normalized_by_trim_and_uppercase(string sku)
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { Sku = sku, Description = "Something else entirely" }).Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
    }

    [Theory]
    [InlineData("Steel bolt M8")]
    [InlineData("  STEEL   bolt\tm8 ")]
    public void Line_matches_by_normalized_description(string description)
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { Sku = null, Description = description }).Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
    }

    [Theory]
    [InlineData("Bolt M8 steel")]
    [InlineData(" bolt  m8   STEEL ")]
    public void Line_matches_by_normalized_explicit_alias(string description)
    {
        var a = Scenario.HappyPath().WithLine("L1", l => l with { Sku = null, Description = description }).Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
    }

    [Fact]
    public void Unmatched_line_is_a_root_unknown_and_its_dependent_checks_depend_on_line_match()
    {
        var a = OrderByReferenceWithUnmatchedL1().Assess();

        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L1"));
        Expect.DependsOn(a.Check(CheckCode.QtyReceived, "L1"), CheckCode.LineMatch);
        Expect.DependsOn(a.Check(CheckCode.QtyOrderRemaining, "L1"), CheckCode.LineMatch);
        Expect.DependsOn(a.Check(CheckCode.UnitPrice, "L1"), CheckCode.LineMatch);
        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L1"));
        Assert.Equal(new LineResult("L1", null, null, 100.00m, 20.00m, null, null, null, null, null), a.Line("L1"));
        Expect.Pass(a.Check(CheckCode.LineMatch, "L2"));
        Assert.Equal(Expect.Questions((RequiredFact.LineMatch, "L1")), Expect.Questions(a));
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Line_with_several_candidate_order_lines_is_a_root_unknown()
    {
        var a = Scenario.HappyPath()
            .WithOrderLine(Scenario.Order1, "OL-2", l => l with { Description = "Steel bolt M8" })
            .WithLine("L1", l => l with { Sku = null })
            .WithLine("L2", l => l with { Sku = "SKU-B" })
            .Assess();

        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L1"));
    }

    [Fact]
    public void Unconfirmed_proposal_for_a_line_fails_needs_review_and_line_checks_use_the_proposed_line()
    {
        var a = OrderByReferenceWithUnmatchedL1().Propose("P1", Scenario.Order1, confirmed: false, ("L1", "OL-1")).Assess();

        Expect.Fail(a.Check(CheckCode.LineMatch, "L1"), Severity.NeedsReview);
        Assert.Equal(new LineResult("L1", "OL-1", MatchSource.AgentProposal, 100.00m, 20.00m, 100m, 100m, 0m, 100m, 100m), a.Line("L1"));
        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(MatchSource.Deterministic, a.OrderMatchSource);
        Expect.AllOthersPass(a, (CheckCode.LineMatch, "L1"));
        Assert.Equal(Decision.NeedsReview, a.Decision);
    }

    [Fact]
    public void Human_confirmed_proposal_for_a_line_passes_with_human_confirmation()
    {
        var a = OrderByReferenceWithUnmatchedL1().Propose("P1", Scenario.Order1, confirmed: true, ("L1", "OL-1")).Assess();

        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
        Assert.Equal(MatchSource.HumanConfirmation, a.Line("L1").MatchSource);
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Case_010_flow_proposal_then_confirmation()
    {
        // No reference, two open orders, L1 description differs: no deterministic candidate.
        Scenario Build() => Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002")
            .WithLine("L1", l => l with { Sku = null, Description = "Steel bolt M8 galv." })
            .WithLine("L2", l => l with { Sku = null });

        var step1 = Build().Assess();
        Expect.RootUnknown(step1.Doc(CheckCode.OrderMatch));
        Assert.Equal(Expect.Questions((RequiredFact.OrderReference, Scenario.DocumentId)), Expect.Questions(step1));
        Assert.Equal(Decision.NeedsInformation, step1.Decision);

        var step2 = Build().Propose("P1", Scenario.Order1, confirmed: false, ("L1", "OL-1")).Assess();
        Expect.Fail(step2.Doc(CheckCode.OrderMatch), Severity.NeedsReview);
        Expect.Fail(step2.Check(CheckCode.LineMatch, "L1"), Severity.NeedsReview);
        Assert.Equal(MatchSource.AgentProposal, step2.Line("L1").MatchSource);
        Assert.Equal(MatchSource.Deterministic, step2.Line("L2").MatchSource);
        Assert.Equal("OL-2", step2.Line("L2").OrderLineId);
        Expect.AllOthersPass(step2, (CheckCode.OrderMatch, Scenario.DocumentId), (CheckCode.LineMatch, "L1"));
        Assert.Empty(step2.Questions);
        Assert.Equal(Decision.NeedsReview, step2.Decision);

        var step3 = Build().Propose("P1", Scenario.Order1, confirmed: true, ("L1", "OL-1")).Assess();
        Expect.Pass(step3.Doc(CheckCode.OrderMatch));
        Assert.Equal(MatchSource.HumanConfirmation, step3.OrderMatchSource);
        Assert.Equal(MatchSource.HumanConfirmation, step3.Line("L1").MatchSource);
        Assert.Equal(MatchSource.Deterministic, step3.Line("L2").MatchSource);
        Assert.Equal(Decision.Clear, step3.Decision);
    }

    [Fact]
    public void Line_matching_deterministically_inside_a_proposed_order_keeps_deterministic_even_if_the_proposal_lists_it()
    {
        var a = Scenario.HappyPath()
            .WithReference("PO-UNKNOWN")
            .Propose("P1", Scenario.Order1, confirmed: false, ("L1", "OL-1"), ("L2", "OL-2"))
            .Assess();

        Expect.Pass(a.Check(CheckCode.LineMatch, "L1"));
        Expect.Pass(a.Check(CheckCode.LineMatch, "L2"));
        Assert.Equal(MatchSource.Deterministic, a.Line("L1").MatchSource);
        Assert.Equal(MatchSource.Deterministic, a.Line("L2").MatchSource);
    }

    [Fact]
    public void Proposed_order_without_a_line_proposal_leaves_an_unmatched_line_root_unknown()
    {
        var a = OrderByReferenceWithUnmatchedL1().WithReference("PO-UNKNOWN").Propose("P1", Scenario.Order1, confirmed: false).Assess();

        Expect.Fail(a.Doc(CheckCode.OrderMatch), Severity.NeedsReview);
        Expect.RootUnknown(a.Check(CheckCode.LineMatch, "L1"));
        Expect.DependsOn(a.Check(CheckCode.QtyReceived, "L1"), CheckCode.LineMatch);
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Theory]
    [InlineData("deterministic")]
    [InlineData("agent_proposal")]
    [InlineData("human_confirmation")]
    public void Unit_mismatch_fails_line_match_needs_review_and_makes_quantity_and_price_checks_not_applicable(string source)
    {
        var s = Scenario.HappyPath().WithLine("L1", l => l with { Unit = "kg" });
        var expectedSource = MatchSource.Deterministic;
        if (source != "deterministic")
        {
            var confirmed = source == "human_confirmation";
            s.WithLine("L1", l => l with { Sku = null, Description = "Galvanised fixing bolt" })
                .Propose("P1", Scenario.Order1, confirmed, ("L1", "OL-1"));
            expectedSource = confirmed ? MatchSource.HumanConfirmation : MatchSource.AgentProposal;
        }

        var a = s.Assess();

        Expect.Fail(a.Check(CheckCode.LineMatch, "L1"), Severity.NeedsReview);
        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Assert.Equal(expectedSource, a.Line("L1").MatchSource);
        Expect.NotApplicable(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.NotApplicable(a.Check(CheckCode.QtyOrderRemaining, "L1"));
        Expect.NotApplicable(a.Check(CheckCode.UnitPrice, "L1"));
        Expect.Pass(a.Check(CheckCode.LineArithmetic, "L1"));
        Expect.AllOthersPass(a, (CheckCode.LineMatch, "L1"), (CheckCode.QtyReceived, "L1"), (CheckCode.QtyOrderRemaining, "L1"), (CheckCode.UnitPrice, "L1"));
        Assert.Empty(a.Questions);
        Assert.Equal(Decision.NeedsReview, a.Decision);
    }

    [Fact]
    public void Unit_mismatch_makes_checks_not_applicable_even_when_they_would_have_failed()
    {
        var a = Scenario.HappyPath()
            .WithLine("L1", l => (l with { Unit = "kg" }).WithQuantity(500m).WithPrice(9.99m))
            .Assess();

        Expect.NotApplicable(a.Check(CheckCode.QtyReceived, "L1"));
        Expect.NotApplicable(a.Check(CheckCode.QtyOrderRemaining, "L1"));
        Expect.NotApplicable(a.Check(CheckCode.UnitPrice, "L1"));
        Assert.Equal(Decision.NeedsReview, a.Decision);
    }
}
