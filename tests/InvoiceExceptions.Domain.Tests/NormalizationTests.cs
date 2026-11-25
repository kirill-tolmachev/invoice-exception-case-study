using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>
/// "Нормализация": the normalizations apply to comparisons, so both sides (document and directory/context) are normalized.
/// </summary>
public class NormalizationTests
{
    [Fact]
    public void Supplier_registration_id_in_the_directory_is_normalized_too()
    {
        var s = Scenario.HappyPath();
        s.Suppliers[0] = s.Suppliers[0] with { RegistrationId = " se-reg-001 " };

        Expect.Pass(s.Assess().Doc(CheckCode.SupplierIdentity));
    }

    [Fact]
    public void Buyer_registration_id_in_the_directory_is_normalized_too()
    {
        var s = Scenario.HappyPath();
        s.Buyers[0] = s.Buyers[0] with { RegistrationId = "rs-reg-100" };

        Expect.Pass(s.Assess().Doc(CheckCode.BuyerIdentity));
    }

    [Fact]
    public void Buyer_legal_name_and_alias_in_the_directory_are_normalized_too()
    {
        var s = Scenario.HappyPath();
        s.Buyers[0] = s.Buyers[0] with { LegalName = "  FICTIONAL   Siteworks  D.O.O. ", ExplicitAliases = ["  fictional\tSITEWORKS "] };

        Expect.Pass(s.WithBuyer("Fictional Siteworks d.o.o.", null).Assess().Doc(CheckCode.BuyerIdentity));
        Expect.Pass(s.WithBuyer("Fictional Siteworks", null).Assess().Doc(CheckCode.BuyerIdentity));
    }

    [Fact]
    public void Order_reference_in_the_context_is_normalized_too()
    {
        var a = Scenario.HappyPath().WithOrder(Scenario.Order1, o => o with { Reference = " po-1001" }).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
    }

    [Fact]
    public void Order_line_sku_description_and_alias_are_normalized_too()
    {
        var a = Scenario.HappyPath()
            .WithOrderLine(Scenario.Order1, "OL-1", l => l with { Sku = " sku-a ", Description = "x", ExplicitAliases = [] })
            .WithOrderLine(Scenario.Order1, "OL-2", l => l with { Sku = null, Description = "  WOOD  screw 4X40 " })
            .WithLine("L2", l => l with { Sku = null })
            .Assess();

        Assert.Equal("OL-1", a.Line("L1").OrderLineId);
        Assert.Equal("OL-2", a.Line("L2").OrderLineId);

        var b = Scenario.HappyPath()
            .WithOrderLine(Scenario.Order1, "OL-1", l => l with { Sku = null, ExplicitAliases = [" BOLT m8   steel "] })
            .WithLine("L1", l => l with { Sku = null, Description = "Bolt M8 steel" })
            .Assess();
        Assert.Equal("OL-1", b.Line("L1").OrderLineId);
    }

    [Fact]
    public void Approved_iban_in_the_directory_is_normalized_too()
    {
        var s = Scenario.HappyPath();
        s.Suppliers[0] = s.Suppliers[0] with { ApprovedIbans = ["se00 test 0000 0000 0000 0001"] };

        Expect.Pass(s.Assess().Doc(CheckCode.BankAccount));
    }
}

public class SpecEdgeTests
{
    [Fact]
    public void Unit_mismatch_does_not_prevent_a_line_from_matching_when_selecting_candidate_orders()
    {
        // ORDER_MATCH step 2 needs every line to match deterministically; the unit is checked afterwards (LINE_MATCH unit_check).
        var a = Scenario.HappyPath().WithoutReference().WithLine("L1", l => l with { Unit = "kg" }).Assess();

        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Expect.Fail(a.Check(CheckCode.LineMatch, "L1"), Severity.NeedsReview);
        Assert.Equal(MatchSource.Deterministic, a.Line("L1").MatchSource);
    }

    [Fact]
    public void Duplicate_search_covers_history_records_that_are_not_counted()
    {
        // R6 compares the key with "записями истории" / invoice_history without a status filter (unlike R3).
        var s = Scenario.HappyPath();
        s.AddHistory(Scenario.HistoryFor(s.BuiltDocument() with { DocumentId = "HIST-ORIG" }, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, counted: false));

        var evaluation = s.Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Equal("HIST-ORIG", evaluation.OriginalDocumentId);
    }
}
