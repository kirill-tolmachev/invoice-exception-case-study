using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>"Итог по приоритету": blocked &gt; needs_information &gt; needs_review &gt; clear; not_applicable is ignored.</summary>
public class DecisionTests
{
    // L1 price 2.53 -> UNIT_PRICE fail needs_review.
    private static Scenario WithReviewIssue(Scenario s) => s.WithLine("L1", l => l.WithPrice(2.53m));

    // Missing currency -> root unknown.
    private static Scenario WithUnknown(Scenario s) => s.WithCurrency(null);

    // Wrong declared net on L2 -> LINE_ARITHMETIC fail blocked.
    private static Scenario WithBlockingIssue(Scenario s) => s.WithLine("L2", l => l with { DeclaredNetAmount = 120.01m });

    [Fact]
    public void Blocked_beats_unknown_and_needs_review()
    {
        var a = WithBlockingIssue(WithUnknown(WithReviewIssue(Scenario.HappyPath()))).Assess();

        Expect.Fail(a.Check(CheckCode.UnitPrice, "L1"), Severity.NeedsReview);
        Expect.RootUnknown(a.Doc(CheckCode.CurrencyMatch));
        Expect.Fail(a.Check(CheckCode.LineArithmetic, "L2"), Severity.Blocked);
        Assert.Equal(Decision.Blocked, a.Decision);
        // All checks are kept and questions are still asked whatever the decision.
        Assert.Equal(17, a.Checks.Count);
        Assert.Equal(Expect.Questions((RequiredFact.DocumentCurrency, Scenario.DocumentId)), Expect.Questions(a));
    }

    [Fact]
    public void Unknown_beats_needs_review()
    {
        var a = WithUnknown(WithReviewIssue(Scenario.HappyPath())).Assess();

        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Needs_review_beats_clear()
    {
        Assert.Equal(Decision.NeedsReview, WithReviewIssue(Scenario.HappyPath()).Assess().Decision);
    }

    [Fact]
    public void Blocked_beats_needs_review_without_unknowns()
    {
        Assert.Equal(Decision.Blocked, WithBlockingIssue(WithReviewIssue(Scenario.HappyPath())).Assess().Decision);
    }

    [Fact]
    public void Dependent_unknowns_alone_still_need_information()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess();

        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Blocked_failure_wins_over_a_failing_order_proposal()
    {
        var a = Scenario.HappyPath()
            .WithReference("PO-UNKNOWN")
            .Propose("P1", Scenario.Order1, confirmed: false)
            .WithIban("SE00TEST0000000000000999")
            .Assess();

        Expect.Fail(a.Doc(CheckCode.OrderMatch), Severity.NeedsReview);
        Expect.Fail(a.Doc(CheckCode.BankAccount), Severity.Blocked);
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Not_applicable_is_ignored_by_the_decision()
    {
        // Unit mismatch: LINE_MATCH fail needs_review + three not_applicable checks -> needs_review, not anything stricter.
        var a = Scenario.HappyPath().WithLine("L2", l => l with { Unit = "pcs" }).Assess();

        Assert.Equal(3, a.Checks.Count(c => c.Status == CheckStatus.NotApplicable));
        Assert.Equal(Decision.NeedsReview, a.Decision);
    }
}

/// <summary>Questions: exactly one per root unknown, with required_fact and target_object_ids = [object_id]; none for dependent unknowns.</summary>
public class QuestionsTests
{
    [Fact]
    public void One_question_per_root_unknown_on_the_document()
    {
        var a = Scenario.HappyPath()
            .WithSupplierRegId(null)
            .WithBuyer("Nobody Known", null)
            .WithCurrency(null)
            .WithIban(null)
            .Assess();

        Expect.RootUnknown(a.Doc(CheckCode.SupplierIdentity));
        Expect.RootUnknown(a.Doc(CheckCode.BuyerIdentity));
        Expect.RootUnknown(a.Doc(CheckCode.CurrencyMatch));
        Expect.RootUnknown(a.Doc(CheckCode.BankAccount));
        Expect.DependsOn(a.Doc(CheckCode.DocumentConflict), CheckCode.SupplierIdentity);
        Assert.Equal(
            Expect.Questions(
                (RequiredFact.SupplierRegistrationId, Scenario.DocumentId),
                (RequiredFact.BuyerRegistrationId, Scenario.DocumentId),
                (RequiredFact.DocumentCurrency, Scenario.DocumentId),
                (RequiredFact.BankAccount, Scenario.DocumentId)),
            Expect.Questions(a));
    }

    [Fact]
    public void Line_questions_target_the_line()
    {
        var a = Scenario.HappyPath()
            .WithLine("L1", l => l with { Sku = null, Description = "Unrelated" })
            .WithLine("L2", l => l with { Sku = null, Description = "Also unrelated" })
            .Assess();

        Assert.Equal(Expect.Questions((RequiredFact.LineMatch, "L1"), (RequiredFact.LineMatch, "L2")), Expect.Questions(a));
        Assert.All(a.Questions, q => Assert.Single(q.TargetObjectIds));
    }

    [Fact]
    public void Dependent_unknowns_ask_nothing()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess();

        Assert.Equal(3 + (4 * 2), a.Checks.Count(c => c.Status == CheckStatus.Unknown && c.DependsOn is not null));
        Assert.Equal(Expect.Questions((RequiredFact.OrderReference, Scenario.DocumentId)), Expect.Questions(a));
    }

    [Fact]
    public void Order_unresolved_because_of_supplier_asks_only_for_the_supplier()
    {
        var a = Scenario.HappyPath().WithoutReference().WithSupplierRegId(null).Assess();

        Assert.Equal(Expect.Questions((RequiredFact.SupplierRegistrationId, Scenario.DocumentId)), Expect.Questions(a));
    }

    [Fact]
    public void Every_question_carries_an_explanatory_text()
    {
        var a = Scenario.HappyPath().WithIban(null).Assess();

        var question = Assert.Single(a.Questions);
        Assert.False(string.IsNullOrWhiteSpace(question.Text));
    }
}
