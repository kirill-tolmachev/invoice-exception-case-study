using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>
/// R2 "Предложения и подтверждения": a proposal that contradicts a deterministic match is rejected at registration;
/// a proposal may only fill a gap.
/// </summary>
public class ProposalRulesTests
{
    private static IReadOnlyList<ProposalIssue> Validate(Scenario s, string orderId, params (string Doc, string Ord)[] lines)
    {
        var proposal = new MatchProposal("P1", Scenario.DocumentId, orderId, [.. lines.Select(l => new LineMatch(l.Doc, l.Ord))], false);
        return ProposalRules.Validate(s.Context(), s.BuiltDocument(), proposal);
    }

    private static Scenario GapInL1() =>
        Scenario.HappyPath().WithLine("L1", l => l with { Sku = null, Description = "Galvanised fixing bolt" });

    [Fact]
    public void Valid_gap_filling_proposal_has_no_issues()
    {
        Assert.Empty(Validate(GapInL1(), Scenario.Order1, ("L1", "OL-1")));
    }

    [Fact]
    public void Gap_filling_proposal_for_the_order_when_there_is_no_deterministic_order_has_no_issues()
    {
        var s = GapInL1().WithoutReference().CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002");

        Assert.Empty(Validate(s, "ORD-1B", ("L1", "ORD-1B/OL-1")));
    }

    [Fact]
    public void Proposal_restating_a_deterministic_line_match_has_no_issues()
    {
        Assert.Empty(Validate(GapInL1(), Scenario.Order1, ("L1", "OL-1"), ("L2", "OL-2")));
    }

    [Fact]
    public void Unknown_order_is_rejected()
    {
        Assert.NotEmpty(Validate(GapInL1(), "ORD-NOPE", ("L1", "OL-1")));
    }

    [Fact]
    public void Unknown_document_line_is_rejected()
    {
        Assert.NotEmpty(Validate(GapInL1(), Scenario.Order1, ("L9", "OL-1")));
    }

    [Fact]
    public void Unknown_order_line_is_rejected()
    {
        Assert.NotEmpty(Validate(GapInL1(), Scenario.Order1, ("L1", "OL-9")));
    }

    [Fact]
    public void Order_line_of_another_order_is_rejected()
    {
        var s = GapInL1().CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002");

        Assert.NotEmpty(Validate(s, Scenario.Order1, ("L1", "ORD-1B/OL-1")));
    }

    [Fact]
    public void Proposal_naming_another_order_than_the_deterministic_reference_match_is_rejected()
    {
        var s = GapInL1().CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002");

        Assert.NotEmpty(Validate(s, "ORD-1B", ("L1", "ORD-1B/OL-1")));
    }

    [Fact]
    public void Proposal_naming_another_order_than_the_unique_deterministic_candidate_is_rejected()
    {
        // No reference; ORD-1 is the only open candidate where every line matches. ORD-1B is closed, so it is not a candidate.
        var s = Scenario.HappyPath()
            .WithoutReference()
            .CloneOrder(Scenario.Order1, "ORD-1B", "PO-1002", o => o with { Status = OrderStatus.Closed });

        Assert.NotEmpty(Validate(s, "ORD-1B"));
    }

    [Fact]
    public void Proposal_mapping_a_line_differently_from_its_deterministic_match_is_rejected()
    {
        // L2 matches OL-2 by SKU.
        Assert.NotEmpty(Validate(GapInL1(), Scenario.Order1, ("L2", "OL-1")));
    }

    [Fact]
    public void Proposal_taking_an_order_line_that_deterministically_belongs_to_another_line_is_rejected()
    {
        // One-to-one: OL-2 already belongs to L2.
        Assert.NotEmpty(Validate(GapInL1(), Scenario.Order1, ("L1", "OL-2")));
    }

    [Fact]
    public void Proposal_using_one_order_line_twice_is_rejected()
    {
        var s = GapInL1().WithLine("L2", l => l with { Sku = null, Description = "Also unmatched" });

        Assert.NotEmpty(Validate(s, Scenario.Order1, ("L1", "OL-1"), ("L2", "OL-1")));
    }
}
