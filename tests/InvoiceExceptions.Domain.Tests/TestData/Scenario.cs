using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Domain.Tests.TestData;

/// <summary>
/// Fluent test-data builder. <see cref="HappyPath"/> is a complete, valid context + invoice that evaluates to clear;
/// every test varies one thing at a time on top of it.
///
/// Happy path:
///   SUP-1 "Nordic Tools AB" (SE-REG-001, IBAN SE00TEST0000000000000001), SUP-2 "Nordic Tools Oy" (FI-REG-002).
///   BUY-1 "Fictional Siteworks d.o.o." (RS-REG-100, alias "Fictional Siteworks"), BUY-2 "Other Buyer Ltd" (UK-REG-200).
///   ORD-1 "PO-1001", open, SUP-1 / BUY-1, EUR:
///     OL-1 SKU-A "Steel bolt M8" (alias "Bolt M8 steel") 100 pcs @ 2.50 / 0.20
///     OL-2 SKU-B "Wood screw 4x40"                       50 box @ 12.00 / 0.20
///   Receipts: OL-1 100 accepted, OL-2 50 accepted. History: empty.
///   DOC-1 "INV-1001", EUR, PO-1001, IBAN of SUP-1:
///     L1 SKU-A "Steel bolt M8"   40 pcs @ 2.50 -> 100.00 + 20.00
///     L2 SKU-B "Wood screw 4x40" 10 box @ 12.00 -> 120.00 + 24.00
///   Totals 220.00 + 44.00 = 264.00.
/// </summary>
internal sealed class Scenario
{
    public const string DocumentId = "DOC-1";
    public const string DocumentNumber = "INV-1001";
    public const string Supplier1 = "SUP-1";
    public const string Supplier2 = "SUP-2";
    public const string Supplier1RegId = "SE-REG-001";
    public const string Supplier2RegId = "FI-REG-002";
    public const string Supplier1Iban = "SE00TEST0000000000000001";
    public const string Supplier2Iban = "FI00TEST0000000000000002";
    public const string Buyer1 = "BUY-1";
    public const string Buyer2 = "BUY-2";
    public const string Buyer1RegId = "RS-REG-100";
    public const string Buyer2RegId = "UK-REG-200";
    public const string Buyer1Name = "Fictional Siteworks d.o.o.";
    public const string Buyer1Alias = "Fictional Siteworks";
    public const string Order1 = "ORD-1";
    public const string Order1Ref = "PO-1001";

    public List<Supplier> Suppliers { get; } = [];
    public List<Buyer> Buyers { get; } = [];
    public List<PurchaseOrder> Orders { get; } = [];
    public List<GoodsReceipt> Receipts { get; } = [];
    public bool ReceiptsAvailable { get; set; } = true;
    public List<HistoryDocument> History { get; } = [];
    public List<EarlierDocument> EarlierDocuments { get; } = [];
    public List<MatchProposal> Proposals { get; } = [];
    public InvoiceDocument Document { get; set; } = null!;

    /// <summary>When true, the declared document totals are recomputed from the lines on build (so they are always right).</summary>
    public bool AutoTotals { get; set; } = true;

    public static Scenario HappyPath()
    {
        var s = new Scenario();
        s.Suppliers.Add(new Supplier(Supplier1, "Nordic Tools AB", Supplier1RegId, [Supplier1Iban]));
        s.Suppliers.Add(new Supplier(Supplier2, "Nordic Tools Oy", Supplier2RegId, [Supplier2Iban]));
        s.Buyers.Add(new Buyer(Buyer1, Buyer1Name, Buyer1RegId, [Buyer1Alias]));
        s.Buyers.Add(new Buyer(Buyer2, "Other Buyer Ltd", Buyer2RegId, []));
        s.Orders.Add(new PurchaseOrder(Order1, Order1Ref, OrderStatus.Open, Supplier1, Buyer1, "EUR",
        [
            new OrderLine("OL-1", "SKU-A", "Steel bolt M8", ["Bolt M8 steel"], 100m, "pcs", 2.50m, 0.20m),
            new OrderLine("OL-2", "SKU-B", "Wood screw 4x40", [], 50m, "box", 12.00m, 0.20m),
        ]));
        s.Receipts.Add(new GoodsReceipt("GR-1", Order1, "OL-1", 100m, true));
        s.Receipts.Add(new GoodsReceipt("GR-2", Order1, "OL-2", 50m, true));
        s.Document = new InvoiceDocument(
            DocumentId,
            DocumentNumber,
            "2026-10-01",
            "2026-10-31",
            "EUR",
            Order1Ref,
            new Party("Nordic Tools AB", Supplier1RegId),
            new Party(Buyer1Name, Buyer1RegId),
            Supplier1Iban,
            [
                Line("L1", "Steel bolt M8", "SKU-A", 40m, "pcs", 2.50m),
                Line("L2", "Wood screw 4x40", "SKU-B", 10m, "box", 12.00m),
            ],
            220.00m,
            44.00m,
            264.00m);
        return s;
    }

    /// <summary>A document line whose declared amounts are arithmetically right (computed independently of the engine).</summary>
    public static InvoiceLine Line(string id, string description, string? sku, decimal quantity, string unit, decimal price, decimal taxRate = 0.20m, bool declareTax = true)
    {
        var net = SpecMath.Net(quantity, price);
        return new InvoiceLine(id, description, sku, quantity, unit, price, net, taxRate, declareTax ? SpecMath.Tax(net, taxRate) : null);
    }

    // ---- document -------------------------------------------------------------------------------------------

    public Scenario WithDocument(Func<InvoiceDocument, InvoiceDocument> change)
    {
        Document = change(Document);
        return this;
    }

    public Scenario WithLines(params InvoiceLine[] lines) => WithDocument(d => d with { Lines = lines });

    public Scenario WithLine(string lineId, Func<InvoiceLine, InvoiceLine> change) =>
        WithDocument(d => d with { Lines = [.. d.Lines.Select(l => l.LineId == lineId ? change(l) : l)] });

    public Scenario WithReference(string? reference) => WithDocument(d => d with { PurchaseOrderReference = reference });

    public Scenario WithoutReference() => WithReference(null);

    public Scenario WithSupplierRegId(string? regId) => WithDocument(d => d with { Supplier = d.Supplier with { RegistrationId = regId } });

    public Scenario WithBuyer(string? legalName, string? regId) => WithDocument(d => d with { Buyer = new Party(legalName, regId) });

    public Scenario WithCurrency(string? currency) => WithDocument(d => d with { Currency = currency });

    public Scenario WithIban(string? iban) => WithDocument(d => d with { Iban = iban });

    public Scenario WithNumber(string number) => WithDocument(d => d with { Number = number });

    // ---- context --------------------------------------------------------------------------------------------

    public Scenario WithOrder(string orderId, Func<PurchaseOrder, PurchaseOrder> change)
    {
        var index = Orders.FindIndex(o => o.OrderId == orderId);
        Orders[index] = change(Orders[index]);
        return this;
    }

    public Scenario WithOrderLine(string orderId, string lineId, Func<OrderLine, OrderLine> change) =>
        WithOrder(orderId, o => o with { Lines = [.. o.Lines.Select(l => l.LineId == lineId ? change(l) : l)] });

    public Scenario AddOrder(PurchaseOrder order)
    {
        Orders.Add(order);
        return this;
    }

    /// <summary>Copies an order under a new id and reference; its line ids get the new order id as prefix ("ORD-X/OL-1").</summary>
    public Scenario CloneOrder(string sourceId, string newId, string newReference, Func<PurchaseOrder, PurchaseOrder>? change = null)
    {
        var source = Orders.Single(o => o.OrderId == sourceId);
        var copy = source with
        {
            OrderId = newId,
            Reference = newReference,
            Lines = [.. source.Lines.Select(l => l with { LineId = $"{newId}/{l.LineId}" })],
        };
        Orders.Add(change is null ? copy : change(copy));
        return this;
    }

    public Scenario WithReceipts(params GoodsReceipt[] receipts)
    {
        Receipts.Clear();
        Receipts.AddRange(receipts);
        return this;
    }

    public Scenario AddReceipt(GoodsReceipt receipt)
    {
        Receipts.Add(receipt);
        return this;
    }

    public Scenario WithReceiptsUnavailable()
    {
        ReceiptsAvailable = false;
        return this;
    }

    public Scenario AddHistory(HistoryDocument history)
    {
        History.Add(history);
        return this;
    }

    /// <summary>A counted history document with a different business key (number) that only feeds R3.</summary>
    public Scenario AddCountedHistory(string historyId, string number, string orderId, params (string OrderLineId, decimal Quantity)[] lines) =>
        AddHistory(HistoryFor(Document with { DocumentId = historyId, Number = number }, Supplier1, Buyer1, orderId, counted: true, lines));

    public Scenario AddEarlier(InvoiceDocument document, string? assessmentRef = null)
    {
        EarlierDocuments.Add(new EarlierDocument(document, assessmentRef));
        return this;
    }

    public Scenario Propose(string proposalId, string orderId, bool confirmed, params (string DocumentLineId, string OrderLineId)[] lines)
    {
        Proposals.Add(new MatchProposal(proposalId, DocumentId, orderId, [.. lines.Select(l => new LineMatch(l.DocumentLineId, l.OrderLineId))], confirmed));
        return this;
    }

    /// <summary>A history record of <paramref name="document"/> whose compared facts are exactly the document's facts.</summary>
    public static HistoryDocument HistoryFor(InvoiceDocument document, string supplierId, string buyerId, string orderId, bool counted, params (string OrderLineId, decimal Quantity)[] lines) =>
        new(
            document.DocumentId,
            supplierId,
            buyerId,
            document.Number,
            counted,
            orderId,
            [.. lines.Select(l => new HistoryLine(l.OrderLineId, l.Quantity))],
            ComparableFacts.Of(document));

    // ---- build ----------------------------------------------------------------------------------------------

    public InvoiceDocument BuiltDocument()
    {
        if (!AutoTotals)
        {
            return Document;
        }

        var subtotal = Document.Lines.Sum(l => SpecMath.Net(l.Quantity, l.UnitNetPrice));
        var tax = Document.Lines.Sum(l => SpecMath.Tax(SpecMath.Net(l.Quantity, l.UnitNetPrice), l.TaxRate));
        return Document with { DeclaredSubtotal = subtotal, DeclaredTaxTotal = tax, DeclaredTotal = subtotal + tax };
    }

    public ContextSnapshot Context() =>
        new("CTX-TEST-1", [.. Orders], new GoodsReceipts(ReceiptsAvailable, [.. Receipts]), [.. Suppliers], [.. Buyers], [.. History]);

    public EvaluationInput Input() => new(Context(), BuiltDocument(), [.. EarlierDocuments], [.. Proposals]);

    public Evaluation Evaluate() => InvoiceEvaluator.Evaluate(Input());

    /// <summary>Evaluates and asserts the result is not a duplicate.</summary>
    public Assessment Assess()
    {
        var evaluation = Evaluate();
        Assert.NotNull(evaluation.Assessment);
        return evaluation.Assessment;
    }
}

internal static class InvoiceLineExtensions
{
    /// <summary>Changes the quantity and keeps the declared amounts arithmetically right.</summary>
    public static InvoiceLine WithQuantity(this InvoiceLine line, decimal quantity) => (line with { Quantity = quantity }).Recomputed();

    /// <summary>Changes the unit price and keeps the declared amounts arithmetically right.</summary>
    public static InvoiceLine WithPrice(this InvoiceLine line, decimal price) => (line with { UnitNetPrice = price }).Recomputed();

    public static InvoiceLine Recomputed(this InvoiceLine line)
    {
        var net = SpecMath.Net(line.Quantity, line.UnitNetPrice);
        return line with
        {
            DeclaredNetAmount = net,
            DeclaredTaxAmount = line.DeclaredTaxAmount is null ? null : SpecMath.Tax(net, line.TaxRate),
        };
    }
}
