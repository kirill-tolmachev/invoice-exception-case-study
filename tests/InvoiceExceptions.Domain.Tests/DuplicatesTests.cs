using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Tests.TestData;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>R6 DOCUMENT_CONFLICT and import_disposition.</summary>
public class DuplicatesTests
{
    private static Scenario WithHistoryOf(Func<InvoiceDocument, InvoiceDocument> original, string supplierId = Scenario.Supplier1, string buyerId = Scenario.Buyer1)
    {
        var s = Scenario.HappyPath();
        var doc = original(s.BuiltDocument() with { DocumentId = "HIST-ORIG" });
        return s.AddHistory(Scenario.HistoryFor(doc, supplierId, buyerId, Scenario.Order1, counted: true, ("OL-1", 40m), ("OL-2", 10m)));
    }

    private static Scenario WithEarlier(Func<InvoiceDocument, InvoiceDocument> earlier, string? assessmentRef = "ASM-7")
    {
        var s = Scenario.HappyPath();
        return s.AddEarlier(earlier(s.BuiltDocument() with { DocumentId = "DOC-0" }), assessmentRef);
    }

    [Fact]
    public void New_key_passes_with_disposition_new()
    {
        var evaluation = WithHistoryOf(d => d with { Number = "INV-0999" }).Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
        Assert.Null(evaluation.OriginalDocumentId);
        Expect.Pass(Assert.IsType<Assessment>(evaluation.Assessment).Doc(CheckCode.DocumentConflict));
    }

    [Fact]
    public void Same_key_and_facts_as_history_is_a_duplicate_without_assessment()
    {
        // case-008.
        var evaluation = WithHistoryOf(d => d).Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Equal("HIST-ORIG", evaluation.OriginalDocumentId);
        Assert.Null(evaluation.Assessment);
    }

    [Fact]
    public void Same_key_and_facts_as_an_earlier_case_document_is_a_duplicate_referencing_its_assessment()
    {
        var evaluation = WithEarlier(d => d).Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Equal("DOC-0", evaluation.OriginalDocumentId);
        Assert.Equal("ASM-7", evaluation.ExistingAssessmentRef);
        Assert.Null(evaluation.Assessment);
    }

    [Fact]
    public void Duplicate_of_an_earlier_document_without_assessment_has_no_assessment_reference()
    {
        var evaluation = WithEarlier(d => d, assessmentRef: null).Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Null(evaluation.ExistingAssessmentRef);
    }

    [Fact]
    public void Duplicate_is_reported_even_if_the_document_would_otherwise_be_blocked()
    {
        var s = WithHistoryOf(d => d with { Iban = "SE00TEST0000000000000999" }).WithIban("SE00TEST0000000000000999");

        var evaluation = s.Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
        Assert.Null(evaluation.Assessment);
    }

    public static TheoryData<string> Variations() =>
    [
        "number case and spaces",
        "iban spacing and case",
        "line order",
        "quantity scale 35 vs 35.000",
        "amount scale",
        "descriptions",
        "po reference case",
        "sku case",
        "supplier and buyer names",
    ];

    [Theory]
    [MemberData(nameof(Variations))]
    public void Duplicate_detection_ignores_normalization_and_layout_differences(string variation)
    {
        static InvoiceDocument Vary(InvoiceDocument d, string variation) => variation switch
        {
            "number case and spaces" => d with { Number = "  inv-1001 " },
            "iban spacing and case" => d with { Iban = "se00 test 0000 0000 0000 0001" },
            "line order" => d with { Lines = [.. d.Lines.Reverse()] },
            "quantity scale 35 vs 35.000" => d with { Lines = [.. d.Lines.Select(l => l with { Quantity = l.Quantity * 1.000m })] },
            "amount scale" => d with
            {
                DeclaredSubtotal = d.DeclaredSubtotal * 1.0000m,
                Lines = [.. d.Lines.Select(l => l with { UnitNetPrice = l.UnitNetPrice * 1.000m, DeclaredNetAmount = l.DeclaredNetAmount * 1.0m })],
            },
            "descriptions" => d with { Lines = [.. d.Lines.Select(l => l with { Description = "Different wording " + l.LineId })] },
            "po reference case" => d with { PurchaseOrderReference = " po-1001" },
            "sku case" => d with { Lines = [.. d.Lines.Select(l => l with { Sku = l.Sku!.ToLowerInvariant() })] },
            "supplier and buyer names" => d with { Supplier = d.Supplier with { LegalName = "Renamed AB" }, Buyer = d.Buyer with { LegalName = "Renamed Buyer" } },
            _ => throw new ArgumentOutOfRangeException(nameof(variation)),
        };

        var history = WithHistoryOf(d => Vary(d, variation)).Evaluate();
        Assert.Equal(ImportDisposition.Duplicate, history.Disposition);

        var earlier = WithEarlier(d => Vary(d, variation)).Evaluate();
        Assert.Equal(ImportDisposition.Duplicate, earlier.Disposition);
    }

    [Fact]
    public void Quantity_scale_difference_is_still_a_duplicate_when_the_current_document_has_the_shorter_scale()
    {
        var s = Scenario.HappyPath().WithLines(
            Scenario.Line("L1", "Steel bolt M8", "SKU-A", 35m, "pcs", 2.50m),
            Scenario.Line("L2", "Wood screw 4x40", "SKU-B", 10m, "box", 12.00m));
        var original = s.BuiltDocument() with
        {
            DocumentId = "HIST-ORIG",
            Lines = [.. s.Document.Lines.Select(l => l with { Quantity = l.LineId == "L1" ? 35.000m : l.Quantity })],
        };
        s.AddHistory(Scenario.HistoryFor(original, Scenario.Supplier1, Scenario.Buyer1, Scenario.Order1, counted: true, ("OL-1", 35m)));

        Assert.Equal(ImportDisposition.Duplicate, s.Evaluate().Disposition);
    }

    public static TheoryData<string> FactChanges() =>
    [
        "issue_date", "due_date", "currency", "purchase_order_reference", "iban",
        "declared_subtotal", "declared_tax_total", "declared_total",
        "line sku", "line quantity", "line unit", "line unit_net_price", "line tax_rate", "line declared_net_amount", "line declared_tax_amount",
        "line count",
    ];

    [Theory]
    [MemberData(nameof(FactChanges))]
    public void Same_key_with_a_different_compared_fact_is_a_conflict(string fact)
    {
        static InvoiceDocument Change(InvoiceDocument d, string fact)
        {
            InvoiceDocument L1(Func<InvoiceLine, InvoiceLine> f) => d with { Lines = [.. d.Lines.Select(l => l.LineId == "L1" ? f(l) : l)] };
            return fact switch
            {
                "issue_date" => d with { IssueDate = "2026-09-01" },
                "due_date" => d with { DueDate = null },
                "currency" => d with { Currency = "SEK" },
                "purchase_order_reference" => d with { PurchaseOrderReference = "PO-1002" },
                "iban" => d with { Iban = "SE00TEST0000000000000002" },
                "declared_subtotal" => d with { DeclaredSubtotal = 1m },
                "declared_tax_total" => d with { DeclaredTaxTotal = 1m },
                "declared_total" => d with { DeclaredTotal = 1m },
                "line sku" => L1(l => l with { Sku = "SKU-Z" }),
                "line quantity" => L1(l => l with { Quantity = 41m }),
                "line unit" => L1(l => l with { Unit = "box" }),
                "line unit_net_price" => L1(l => l with { UnitNetPrice = 2.51m }),
                "line tax_rate" => L1(l => l with { TaxRate = 0.10m }),
                "line declared_net_amount" => L1(l => l with { DeclaredNetAmount = 100.01m }),
                "line declared_tax_amount" => L1(l => l with { DeclaredTaxAmount = null }),
                "line count" => d with { Lines = [d.Lines[0]] },
                _ => throw new ArgumentOutOfRangeException(nameof(fact)),
            };
        }

        var history = WithHistoryOf(d => Change(d, fact)).Evaluate();
        Assert.Equal(ImportDisposition.Conflict, history.Disposition);
        Assert.Equal("HIST-ORIG", history.OriginalDocumentId);

        var earlier = WithEarlier(d => Change(d, fact)).Evaluate();
        Assert.Equal(ImportDisposition.Conflict, earlier.Disposition);
        Assert.Equal("DOC-0", earlier.OriginalDocumentId);
    }

    [Fact]
    public void Conflict_fails_blocked_and_still_computes_every_other_check()
    {
        // case-009: price changed by 0.01 (inside tolerance), both versions arithmetically right.
        var evaluation = WithHistoryOf(d => d with
        {
            Lines = [.. d.Lines.Select(l => l.LineId == "L1" ? l.WithPrice(2.51m) : l)],
            DeclaredSubtotal = 220.40m,
            DeclaredTaxTotal = 44.08m,
            DeclaredTotal = 264.48m,
        }).Evaluate();

        Assert.Equal(ImportDisposition.Conflict, evaluation.Disposition);
        var a = Assert.IsType<Assessment>(evaluation.Assessment);
        Expect.Fail(a.Doc(CheckCode.DocumentConflict), Severity.Blocked);
        Expect.AllOthersPass(a, (CheckCode.DocumentConflict, Scenario.DocumentId));
        Assert.Equal(7 + (5 * 2), a.Checks.Count);
        Assert.Equal(Scenario.Order1, a.MatchedOrderId);
        Assert.Equal(Decision.Blocked, a.Decision);
    }

    [Fact]
    public void Conflict_with_an_earlier_case_document_also_fails_blocked()
    {
        var evaluation = WithEarlier(d => d with { IssueDate = "2026-09-01" }).Evaluate();

        Assert.Equal(ImportDisposition.Conflict, evaluation.Disposition);
        Assert.Null(evaluation.ExistingAssessmentRef);
        Expect.Fail(Assert.IsType<Assessment>(evaluation.Assessment).Doc(CheckCode.DocumentConflict), Severity.Blocked);
    }

    [Fact]
    public void Same_number_from_another_supplier_is_a_different_key()
    {
        var evaluation = WithHistoryOf(d => d, supplierId: Scenario.Supplier2).Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
    }

    [Fact]
    public void Same_number_for_another_buyer_is_a_different_key()
    {
        var evaluation = WithHistoryOf(d => d, buyerId: Scenario.Buyer2).Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
    }

    [Fact]
    public void Earlier_document_of_another_supplier_is_a_different_key()
    {
        var evaluation = WithEarlier(d => d with { Supplier = d.Supplier with { RegistrationId = Scenario.Supplier2RegId } }).Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
    }

    [Fact]
    public void Business_key_uses_the_buyer_resolved_by_name()
    {
        var evaluation = WithHistoryOf(d => d).WithBuyer(Scenario.Buyer1Alias, null).Evaluate();

        Assert.Equal(ImportDisposition.Duplicate, evaluation.Disposition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("XX-NOT-THERE")]
    public void Unresolved_supplier_makes_document_conflict_depend_on_supplier_identity(string? regId)
    {
        var evaluation = WithHistoryOf(d => d).WithSupplierRegId(regId).Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
        var a = Assert.IsType<Assessment>(evaluation.Assessment);
        Expect.DependsOn(a.Doc(CheckCode.DocumentConflict), CheckCode.SupplierIdentity);
    }

    [Theory]
    [InlineData("Nobody", null)]
    [InlineData(Scenario.Buyer1Name, "XX-NOT-THERE")]
    public void Unresolved_buyer_makes_document_conflict_depend_on_buyer_identity(string? name, string? regId)
    {
        var evaluation = WithHistoryOf(d => d).WithBuyer(name, regId).Evaluate();

        Assert.Equal(ImportDisposition.New, evaluation.Disposition);
        Expect.DependsOn(Assert.IsType<Assessment>(evaluation.Assessment).Doc(CheckCode.DocumentConflict), CheckCode.BuyerIdentity);
    }

    [Fact]
    public void Unresolved_supplier_takes_precedence_over_unresolved_buyer()
    {
        var a = Scenario.HappyPath().WithSupplierRegId(null).WithBuyer(null, null).Assess();

        Expect.DependsOn(a.Doc(CheckCode.DocumentConflict), CheckCode.SupplierIdentity);
    }
}
