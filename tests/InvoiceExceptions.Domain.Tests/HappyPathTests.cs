using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

public class HappyPathTests
{
    [Fact]
    public void Happy_path_is_new_and_clear_with_every_check_passing()
    {
        var evaluation = Scenario.HappyPath().Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
        Assert.Null(evaluation.OriginalDocumentId);
        var a = Assert.IsType<Assessment>(evaluation.Assessment);
        Assert.Equal(Decision.Clear, a.Decision);
        Assert.Empty(a.Questions);
        Expect.AllOthersPass(a);
        Assert.Equal(7 + (5 * 2), a.Checks.Count);
    }

    [Fact]
    public void Happy_path_reports_matches_and_computed_figures()
    {
        var a = Scenario.HappyPath().Assess();

        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Assert.Equal(MatchSource.Deterministic, a.OrderMatchSource);
        Assert.Equal(new LineResult("L1", "OL-1", MatchSource.Deterministic, 100.00m, 20.00m, 100m, 100m, 0m, 100m, 100m), a.Line("L1"));
        Assert.Equal(new LineResult("L2", "OL-2", MatchSource.Deterministic, 120.00m, 24.00m, 50m, 50m, 0m, 50m, 50m), a.Line("L2"));
        Assert.Equal(new Totals(220.00m, 44.00m, 264.00m), a.Totals);
    }
}
