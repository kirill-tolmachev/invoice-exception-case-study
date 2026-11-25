using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>R1 SUPPLIER_IDENTITY, BUYER_IDENTITY, CURRENCY_MATCH.</summary>
public class SupplierIdentityTests
{
    [Fact]
    public void Registration_id_of_the_order_supplier_passes()
    {
        Expect.Pass(Scenario.HappyPath().Assess().Doc(CheckCode.SupplierIdentity));
    }

    [Theory]
    [InlineData("  se-reg-001 ")]
    [InlineData("Se-Reg-001")]
    public void Registration_id_is_normalized_by_trim_and_uppercase(string regId)
    {
        var a = Scenario.HappyPath().WithSupplierRegId(regId).Assess();

        Expect.Pass(a.Doc(CheckCode.SupplierIdentity));
        Expect.Pass(a.Doc(CheckCode.BankAccount));
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Theory]
    [InlineData("SEREG001")]   // separators are kept
    [InlineData("SE-REG-01")]  // leading zeros are kept
    public void Registration_id_normalization_keeps_separators_and_leading_zeros(string regId)
    {
        Expect.Fail(Scenario.HappyPath().WithSupplierRegId(regId).Assess().Doc(CheckCode.SupplierIdentity), Severity.Blocked);
    }

    [Fact]
    public void Supplier_name_is_never_used_for_identification()
    {
        var a = Scenario.HappyPath().WithDocument(d => d with { Supplier = new Party("Completely Different Name", Scenario.Supplier1RegId) }).Assess();

        Expect.Pass(a.Doc(CheckCode.SupplierIdentity));
    }

    [Fact]
    public void Missing_registration_id_is_a_root_unknown_even_when_the_name_matches_a_supplier()
    {
        var a = Scenario.HappyPath().WithSupplierRegId(null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.SupplierIdentity));
        Assert.Contains(a.Questions, q => q.RequiredFact == RequiredFact.SupplierRegistrationId);
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Unknown_registration_id_fails_blocked()
    {
        var a = Scenario.HappyPath().WithSupplierRegId("XX-NOT-THERE").Assess();

        Expect.Fail(a.Doc(CheckCode.SupplierIdentity), Severity.Blocked);
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Supplier_other_than_the_order_supplier_fails_blocked_while_order_match_passes()
    {
        // case-006: the document names SUP-2 by registration id, the PO reference points to SUP-1's order.
        var a = Scenario.HappyPath().WithSupplierRegId(Scenario.Supplier2RegId).WithIban(Scenario.Supplier2Iban).Assess();

        Expect.Fail(a.Doc(CheckCode.SupplierIdentity), Severity.Blocked);
        Expect.Pass(a.Doc(CheckCode.OrderMatch));
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Expect.AllOthersPass(a, (CheckCode.SupplierIdentity, Scenario.DocumentId));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Without_a_selected_order_it_depends_on_order_match()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess();

        Expect.DependsOn(a.Doc(CheckCode.SupplierIdentity), CheckCode.OrderMatch);
    }

    [Fact]
    public void Without_a_selected_order_a_missing_registration_id_is_still_its_own_root_unknown()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithSupplierRegId(null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.SupplierIdentity));
        Assert.Contains(a.Questions, q => q.RequiredFact == RequiredFact.SupplierRegistrationId);
    }

    [Fact]
    public void Without_a_selected_order_an_unknown_registration_id_still_fails()
    {
        // core-v1.json: "registration_id not found in suppliers" -> fail comes before "ORDER_MATCH unknown".
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithSupplierRegId("XX-NOT-THERE").Assess();

        Expect.Fail(a.Doc(CheckCode.SupplierIdentity), Severity.Blocked);
    }
}

public class BuyerIdentityTests
{
    [Fact]
    public void Registration_id_of_the_order_buyer_passes()
    {
        Expect.Pass(Scenario.HappyPath().Assess().Doc(CheckCode.BuyerIdentity));
    }

    [Fact]
    public void Registration_id_is_normalized()
    {
        Expect.Pass(Scenario.HappyPath().WithBuyer(null, " rs-reg-100\t").Assess().Doc(CheckCode.BuyerIdentity));
    }

    [Theory]
    [InlineData("Fictional Siteworks d.o.o.")]
    [InlineData("  fictional   SITEWORKS  d.o.o. ")]
    [InlineData("FICTIONAL\tSiteworks\nD.O.O.")]
    [InlineData("Fictional Siteworks")]          // explicit alias
    [InlineData("  fictional  siteworks ")]      // explicit alias, whitespace and case differences
    public void Without_registration_id_the_normalized_name_or_alias_resolves_the_buyer(string name)
    {
        var a = Scenario.HappyPath().WithBuyer(name, null).Assess();

        Expect.Pass(a.Doc(CheckCode.BuyerIdentity));
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Theory]
    [InlineData("Fictional")]               // a prefix is not a match
    [InlineData("Fictional Siteworks doo")] // punctuation is not normalized away
    [InlineData("Unknown Buyer GmbH")]
    public void Without_registration_id_a_name_that_matches_no_buyer_is_a_root_unknown(string name)
    {
        var a = Scenario.HappyPath().WithBuyer(name, null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.BuyerIdentity));
        Assert.Equal(Expect.Questions((RequiredFact.BuyerRegistrationId, Scenario.DocumentId)), Expect.Questions(a));
    }

    [Fact]
    public void Without_registration_id_and_without_name_it_is_a_root_unknown()
    {
        Expect.RootUnknown(Scenario.HappyPath().WithBuyer(null, null).Assess().Doc(CheckCode.BuyerIdentity));
    }

    [Fact]
    public void Without_registration_id_an_ambiguous_name_is_a_root_unknown()
    {
        var s = Scenario.HappyPath();
        s.Buyers.Add(new Buyer("BUY-3", "Fictional Siteworks Holding", "XX-REG-300", [Scenario.Buyer1Alias]));

        var a = s.WithBuyer(Scenario.Buyer1Alias, null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.BuyerIdentity));
    }

    [Fact]
    public void Ambiguity_also_counts_between_one_buyers_legal_name_and_another_buyers_alias()
    {
        var s = Scenario.HappyPath();
        s.Buyers.Add(new Buyer("BUY-3", "Something Else", "XX-REG-300", [Scenario.Buyer1Name]));

        Expect.RootUnknown(s.WithBuyer(Scenario.Buyer1Name, null).Assess().Doc(CheckCode.BuyerIdentity));
    }

    [Fact]
    public void Unknown_registration_id_fails_blocked_even_if_the_name_would_resolve()
    {
        var a = Scenario.HappyPath().WithBuyer(Scenario.Buyer1Name, "XX-NOT-THERE").Assess();

        Expect.Fail(a.Doc(CheckCode.BuyerIdentity), Severity.Blocked);
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Buyer_of_another_order_fails_blocked()
    {
        var a = Scenario.HappyPath().WithBuyer("Other Buyer Ltd", Scenario.Buyer2RegId).Assess();

        Expect.Fail(a.Doc(CheckCode.BuyerIdentity), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.BuyerIdentity, Scenario.DocumentId));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Buyer_resolved_by_name_to_another_buyer_fails_blocked()
    {
        Expect.Fail(Scenario.HappyPath().WithBuyer("other buyer ltd", null).Assess().Doc(CheckCode.BuyerIdentity), Severity.Blocked);
    }

    [Fact]
    public void Without_a_selected_order_it_depends_on_order_match()
    {
        Expect.DependsOn(Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess().Doc(CheckCode.BuyerIdentity), CheckCode.OrderMatch);
    }

    [Fact]
    public void Without_a_selected_order_an_unresolvable_buyer_is_still_its_own_root_unknown()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithBuyer("Nobody", null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.BuyerIdentity));
        Assert.Contains(a.Questions, q => q.RequiredFact == RequiredFact.BuyerRegistrationId);
    }

    [Fact]
    public void Without_a_selected_order_an_unknown_registration_id_still_fails()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithBuyer(null, "XX-NOT-THERE").Assess();

        Expect.Fail(a.Doc(CheckCode.BuyerIdentity), Severity.Blocked);
    }
}

public class CurrencyMatchTests
{
    [Fact]
    public void Same_currency_as_the_order_passes()
    {
        Expect.Pass(Scenario.HappyPath().Assess().Doc(CheckCode.CurrencyMatch));
    }

    [Fact]
    public void Different_currency_fails_blocked()
    {
        var a = Scenario.HappyPath().WithCurrency("USD").Assess();

        Expect.Fail(a.Doc(CheckCode.CurrencyMatch), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.CurrencyMatch, Scenario.DocumentId));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Missing_currency_is_a_root_unknown()
    {
        var a = Scenario.HappyPath().WithCurrency(null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.CurrencyMatch));
        Assert.Equal(Expect.Questions((RequiredFact.DocumentCurrency, Scenario.DocumentId)), Expect.Questions(a));
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Without_a_selected_order_it_depends_on_order_match()
    {
        Expect.DependsOn(Scenario.HappyPath().WithReference("PO-UNKNOWN").Assess().Doc(CheckCode.CurrencyMatch), CheckCode.OrderMatch);
    }

    [Fact]
    public void Without_a_selected_order_a_missing_currency_is_still_its_own_root_unknown()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithCurrency(null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.CurrencyMatch));
        Assert.Equal(
            Expect.Questions((RequiredFact.DocumentCurrency, Scenario.DocumentId), (RequiredFact.OrderReference, Scenario.DocumentId)),
            Expect.Questions(a));
    }

    [Fact]
    public void Currency_is_compared_against_the_order_selected_by_a_proposal()
    {
        var a = Scenario.HappyPath()
            .WithReference("PO-UNKNOWN")
            .WithOrder(Scenario.Order1, o => o with { Currency = "USD" })
            .Propose("P1", Scenario.Order1, confirmed: false)
            .Assess();

        Expect.Fail(a.Doc(CheckCode.CurrencyMatch), Severity.Blocked);
    }
}
