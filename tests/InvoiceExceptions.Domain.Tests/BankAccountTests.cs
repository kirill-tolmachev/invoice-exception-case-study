using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>R5 BANK_ACCOUNT.</summary>
public class BankAccountTests
{
    [Fact]
    public void Approved_iban_passes()
    {
        Expect.Pass(Scenario.HappyPath().Assess().Doc(CheckCode.BankAccount));
    }

    [Theory]
    [InlineData("SE00 TEST 0000 0000 0000 0001")]
    [InlineData("se00test0000000000000001")]
    [InlineData(" se00 TEST\t0000000000000001 ")]
    public void Iban_is_normalized_by_removing_whitespace_and_uppercasing(string iban)
    {
        var a = Scenario.HappyPath().WithIban(iban).Assess();

        Expect.Pass(a.Doc(CheckCode.BankAccount));
        Assert.Equal(Decision.Clear, a.Decision);
    }

    [Fact]
    public void Approved_iban_stored_with_spaces_also_matches()
    {
        var s = Scenario.HappyPath();
        s.Suppliers[0] = s.Suppliers[0] with { ApprovedIbans = ["se00 test 0000 0000 0000 0001"] };

        Expect.Pass(s.Assess().Doc(CheckCode.BankAccount));
    }

    [Fact]
    public void Iban_not_in_the_approved_list_fails_blocked()
    {
        // case-007.
        var a = Scenario.HappyPath().WithIban("SE00TEST0000000000000999").Assess();

        Expect.Fail(a.Doc(CheckCode.BankAccount), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.BankAccount, Scenario.DocumentId));
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Missing_iban_is_a_root_unknown()
    {
        var a = Scenario.HappyPath().WithIban(null).Assess();

        Expect.RootUnknown(a.Doc(CheckCode.BankAccount));
        Assert.Equal(Expect.Questions((RequiredFact.BankAccount, Scenario.DocumentId)), Expect.Questions(a));
        Assert.Equal(Decision.NeedsInformation, a.Decision);
    }

    [Fact]
    public void Missing_iban_is_a_root_unknown_even_when_the_supplier_is_unresolved()
    {
        // core-v1.json: "document IBAN is null" comes before "supplier not resolved".
        Expect.RootUnknown(Scenario.HappyPath().WithIban(null).WithSupplierRegId(null).Assess().Doc(CheckCode.BankAccount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("XX-NOT-THERE")]
    public void Unresolved_supplier_makes_it_depend_on_supplier_identity(string? regId)
    {
        Expect.DependsOn(Scenario.HappyPath().WithSupplierRegId(regId).Assess().Doc(CheckCode.BankAccount), CheckCode.SupplierIdentity);
    }

    [Fact]
    public void Iban_is_checked_against_the_document_supplier_even_when_it_is_not_the_order_supplier()
    {
        // case-006: document names SUP-2 by registration id; its IBAN is SUP-2's; the order belongs to SUP-1.
        var a = Scenario.HappyPath().WithSupplierRegId(Scenario.Supplier2RegId).WithIban(Scenario.Supplier2Iban).Assess();
        Expect.Pass(a.Doc(CheckCode.BankAccount));

        var b = Scenario.HappyPath().WithSupplierRegId(Scenario.Supplier2RegId).WithIban(Scenario.Supplier1Iban).Assess();
        Expect.Fail(b.Doc(CheckCode.BankAccount), Severity.Blocked);
    }

    [Fact]
    public void Iban_is_checked_even_when_no_order_is_selected()
    {
        var a = Scenario.HappyPath().WithReference("PO-UNKNOWN").WithIban("SE00TEST0000000000000999").Assess();

        Expect.Fail(a.Doc(CheckCode.BankAccount), Severity.Blocked);
    }
}
