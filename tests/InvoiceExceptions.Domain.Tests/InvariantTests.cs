using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>Properties that must hold for every input (BUSINESS_RULES.md "Статусы и итог", core-v1.json unknown_semantics etc.).</summary>
public class InvariantTests
{
    private const int Seeds = 1500;

    private static void ForEachSeed(Action<Scenario, Evaluation> check)
    {
        for (var seed = 1; seed <= Seeds; seed++)
        {
            var scenario = Generator.Scenario(seed);
            var evaluation = scenario.Evaluate();
            try
            {
                check(scenario, evaluation);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}:\n{Expect.Canonical(evaluation)}\n{ex.Message}");
            }
        }
    }

    private static void ForEachAssessed(Action<Scenario, Assessment> check) =>
        ForEachSeed((s, e) =>
        {
            if (e.Assessment is { } a)
            {
                check(s, a);
            }
        });

    [Fact]
    public void Generator_reaches_every_decision_and_disposition()
    {
        var seen = new HashSet<string>();
        for (var seed = 1; seed <= Seeds; seed++)
        {
            var e = Generator.Scenario(seed).Evaluate();
            seen.Add(e.Disposition.ToString());
            if (e.Assessment is { } a)
            {
                seen.Add(a.Decision.ToString());
                seen.Add("order:" + a.OrderMatchSource);
                foreach (var c in a.Checks)
                {
                    seen.Add($"{c.Code}:{c.Status}");
                }
            }
        }

        foreach (var expected in new[]
                 {
                     "New", "Duplicate", "Conflict", "Clear", "NeedsReview", "NeedsInformation", "Blocked",
                     "order:Deterministic", "order:AgentProposal", "order:HumanConfirmation", "order:",
                     "LineMatch:Fail", "UnitPrice:NotApplicable", "QtyReceived:NotApplicable", "QtyReceived:Unknown", "DocumentConflict:Unknown",
                 })
        {
            Assert.Contains(expected, seen);
        }
    }

    [Fact]
    public void Every_non_duplicate_has_seven_document_checks_and_five_per_line_with_unique_code_and_object()
    {
        ForEachAssessed((s, a) =>
        {
            var doc = s.Document;
            Assert.Equal(7 + (5 * doc.Lines.Count), a.Checks.Count);
            Assert.Equal(a.Checks.Count, a.Checks.Select(c => (c.Code, c.ObjectId)).Distinct().Count());

            var documentChecks = a.Checks.Where(c => Spec.DocumentChecks.Contains(c.Code)).ToList();
            Assert.Equal(Spec.DocumentChecks.Order(), documentChecks.Select(c => c.Code).Order());
            Assert.All(documentChecks, c => Assert.Equal(doc.DocumentId, c.ObjectId));

            foreach (var line in doc.Lines)
            {
                var lineChecks = a.Checks.Where(c => c.ObjectId == line.LineId).Select(c => c.Code).Order();
                Assert.Equal(Spec.LineChecks.Order(), lineChecks);
            }

            Assert.Equal(doc.Lines.Select(l => l.LineId).Order(StringComparer.Ordinal), a.Lines.Select(l => l.DocumentLineId).Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void Severity_is_set_iff_status_is_fail_and_equals_the_checks_fixed_severity()
    {
        ForEachAssessed((_, a) =>
        {
            foreach (var c in a.Checks)
            {
                var expected = c.Status == CheckStatus.Fail ? Spec.FailSeverity(c.Code) : (Severity?)null;
                Assert.True(expected == c.Severity, Expect.Describe(c));
            }
        });
    }

    [Fact]
    public void Depends_on_is_set_only_on_unknown_and_only_to_a_target_the_policy_allows()
    {
        ForEachAssessed((_, a) =>
        {
            foreach (var c in a.Checks.Where(c => c.DependsOn is not null))
            {
                Assert.True(c.Status == CheckStatus.Unknown, Expect.Describe(c));
                Assert.Contains(c.DependsOn!.Value, Spec.AllowedDependsOn(c.Code));
            }
        });
    }

    [Fact]
    public void Root_unknowns_only_occur_on_checks_with_a_required_fact()
    {
        ForEachAssessed((_, a) =>
        {
            foreach (var c in a.Checks.Where(c => c.Status == CheckStatus.Unknown && c.DependsOn is null))
            {
                Assert.True(Spec.RequiredFact(c.Code) is not null, Expect.Describe(c));
            }
        });
    }

    [Fact]
    public void There_is_exactly_one_question_per_root_unknown_and_no_other()
    {
        ForEachAssessed((_, a) =>
        {
            var expected = a.Checks
                .Where(c => c.Status == CheckStatus.Unknown && c.DependsOn is null)
                .Select(c => (Spec.RequiredFact(c.Code)!.Value, c.ObjectId))
                .ToArray();
            Assert.Equal(Expect.Questions(expected), Expect.Questions(a));
        });
    }

    [Fact]
    public void Decision_follows_the_priority_rule()
    {
        ForEachAssessed((_, a) => Assert.Equal(Spec.Decide(a.Checks), a.Decision));
    }

    [Fact]
    public void Line_quantity_figures_are_null_iff_the_line_is_not_matched()
    {
        ForEachAssessed((s, a) =>
        {
            foreach (var line in a.Lines)
            {
                var lineMatch = a.Check(CheckCode.LineMatch, line.DocumentLineId);
                var matched = line.OrderLineId is not null;
                Assert.Equal(lineMatch.Status != CheckStatus.Unknown, matched);
                Assert.Equal(matched, line.MatchSource is not null);
                Assert.Equal(matched, line.Ordered is not null);
                Assert.Equal(matched, line.PreviouslyInvoiced is not null);
                Assert.Equal(matched, line.RemainingOnOrder is not null);
                Assert.Equal(matched && s.ReceiptsAvailable, line.Received is not null);
                Assert.Equal(matched && s.ReceiptsAvailable, line.AvailableToInvoice is not null);
                if (matched)
                {
                    Assert.Equal(line.Ordered - line.PreviouslyInvoiced, line.RemainingOnOrder);
                    if (s.ReceiptsAvailable)
                    {
                        Assert.Equal(line.Received - line.PreviouslyInvoiced, line.AvailableToInvoice);
                    }
                }
            }
        });
    }

    [Fact]
    public void Not_applicable_occurs_exactly_on_the_three_checks_of_a_matched_line_with_a_different_unit()
    {
        ForEachAssessed((s, a) =>
        {
            Assert.DoesNotContain(a.Checks, c => c.Status == CheckStatus.NotApplicable && c.Code is not (CheckCode.QtyReceived or CheckCode.QtyOrderRemaining or CheckCode.UnitPrice));
            foreach (var line in a.Lines)
            {
                var docLine = s.Document.Lines.Single(l => l.LineId == line.DocumentLineId);
                var order = s.Orders.SingleOrDefault(o => o.OrderId == a.MatchedOrderId);
                var orderLine = order?.Lines.SingleOrDefault(l => l.LineId == line.OrderLineId);
                var unitsDiffer = orderLine is not null && orderLine.Unit != docLine.Unit;
                foreach (var code in new[] { CheckCode.QtyReceived, CheckCode.QtyOrderRemaining, CheckCode.UnitPrice })
                {
                    Assert.Equal(unitsDiffer, a.Check(code, line.DocumentLineId).Status == CheckStatus.NotApplicable);
                }

                if (unitsDiffer)
                {
                    Expect.Fail(a.Check(CheckCode.LineMatch, line.DocumentLineId), Severity.NeedsReview);
                }
            }
        });
    }

    [Fact]
    public void Order_match_and_matched_order_are_consistent()
    {
        ForEachAssessed((_, a) =>
        {
            var check = a.Doc(CheckCode.OrderMatch);
            Assert.Equal(check.Status == CheckStatus.Unknown, a.MatchedOrderId is null);
            Assert.Equal(a.MatchedOrderId is null, a.OrderMatchSource is null);
            switch (check.Status)
            {
                case CheckStatus.Pass:
                    Assert.NotEqual(MatchSource.AgentProposal, a.OrderMatchSource);
                    break;
                case CheckStatus.Fail:
                    Assert.Equal(MatchSource.AgentProposal, a.OrderMatchSource);
                    break;
            }

            if (check.Status == CheckStatus.Unknown)
            {
                Assert.All(a.Checks.Where(c => c.Code == CheckCode.LineMatch), c => Expect.DependsOn(c, CheckCode.OrderMatch));
            }
        });
    }

    [Fact]
    public void Calculated_amounts_follow_round_half_up_whatever_else_happens()
    {
        ForEachAssessed((s, a) =>
        {
            foreach (var docLine in s.Document.Lines)
            {
                var line = a.Line(docLine.LineId);
                var net = SpecMath.Net(docLine.Quantity, docLine.UnitNetPrice);
                Assert.Equal(net, line.CalculatedNet);
                Assert.Equal(SpecMath.Tax(net, docLine.TaxRate), line.CalculatedTax);
            }

            Assert.Equal(a.Lines.Sum(l => l.CalculatedNet), a.Totals.CalculatedSubtotal);
            Assert.Equal(a.Lines.Sum(l => l.CalculatedTax), a.Totals.CalculatedTaxTotal);
            Assert.Equal(a.Totals.CalculatedSubtotal + a.Totals.CalculatedTaxTotal, a.Totals.CalculatedTotal);
        });
    }

    [Fact]
    public void Duplicate_has_no_assessment_and_conflict_names_the_original_and_fails_document_conflict()
    {
        ForEachSeed((_, e) =>
        {
            Assert.Equal(e.Disposition == ImportDisposition.Duplicate, e.Assessment is null);
            Assert.Equal(e.Disposition != ImportDisposition.New, e.OriginalDocumentId is not null);
            if (e.Assessment is { } a)
            {
                var conflict = a.Doc(CheckCode.DocumentConflict);
                Assert.Equal(e.Disposition == ImportDisposition.Conflict, conflict.Status == CheckStatus.Fail);
            }
        });
    }

    [Fact]
    public void Evaluating_the_same_input_twice_gives_equal_results()
    {
        ForEachSeed((s, e) =>
        {
            var again = InvoiceEvaluator.Evaluate(s.Input());
            Assert.Equal(Expect.Canonical(e), Expect.Canonical(again));
        });
    }

    [Fact]
    public void Reordering_document_lines_changes_no_per_line_result_nor_the_decision()
    {
        ForEachSeed((s, e) =>
        {
            var reversed = s.Input() with { Document = s.BuiltDocument() with { Lines = [.. s.Document.Lines.Reverse()] } };
            var other = InvoiceEvaluator.Evaluate(reversed);

            Assert.Equal(e.Disposition, other.Disposition);
            Assert.Equal(e.OriginalDocumentId, other.OriginalDocumentId);
            if (e.Assessment is not { } a)
            {
                Assert.Null(other.Assessment);
                return;
            }

            var b = Assert.IsType<Assessment>(other.Assessment);
            Assert.Equal(a.Decision, b.Decision);
            Assert.Equal(a.MatchedOrderId, b.MatchedOrderId);
            Assert.Equal(a.OrderMatchSource, b.OrderMatchSource);
            Assert.Equal(a.Totals, b.Totals);
            Assert.Equal(a.Lines.OrderBy(l => l.DocumentLineId, StringComparer.Ordinal), b.Lines.OrderBy(l => l.DocumentLineId, StringComparer.Ordinal));
            Assert.Equal(
                a.Checks.Select(Expect.Describe).Order(StringComparer.Ordinal),
                b.Checks.Select(Expect.Describe).Order(StringComparer.Ordinal));
            Assert.Equal(Expect.Questions(a), Expect.Questions(b));
        });
    }
}
