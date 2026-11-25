using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Domain.Tests.TestData;

/// <summary>Small deterministic PRNG (xorshift64*), so generated inputs are identical on every run and platform.</summary>
internal sealed class Rng(int seed)
{
    private ulong _state = ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL | 1UL;

    private ulong Next()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 0x2545F4914F6CDD1DUL;
    }

    public int Int(int maxExclusive) => (int)(Next() % (ulong)maxExclusive);

    public int Int(int min, int maxInclusive) => min + Int(maxInclusive - min + 1);

    public bool Chance(int percent) => Int(100) < percent;

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(items.Count)];
}

/// <summary>
/// Generates varied, mostly-plausible inputs around the happy-path directory: random orders sharing SKUs and
/// descriptions, document lines derived from order lines with perturbations, missing facts, receipts, history,
/// earlier documents and proposals (kept only if <see cref="ProposalRules.Validate"/> accepts them, as at registration).
/// </summary>
internal static class Generator
{
    private static readonly string[] Skus = ["SKU-A", "SKU-B", "SKU-C", "SKU-D"];
    private static readonly string[] Descriptions = ["Steel bolt M8", "Wood screw 4x40", "Washer M8", "Wall plug 10 mm"];
    private static readonly string[] Units = ["pcs", "box", "kg"];
    private static readonly decimal[] OrderQuantities = [10m, 20m, 50m, 100m, 12.5m];
    private static readonly decimal[] Prices = [0.35m, 1.13m, 2.50m, 12.00m, 18.75m, 0.03m];
    private static readonly decimal[] Rates = [0.20m, 0.10m, 0.00m, 0.15m];
    private static readonly decimal[] InvoiceQuantities = [1m, 5m, 10m, 12.5m, 40.125m, 100m, 150m, 1.5m, 0.375m];

    public static Scenario Scenario(int seed)
    {
        var r = new Rng(seed);
        var s = TestData.Scenario.HappyPath();
        s.Orders.Clear();
        s.Receipts.Clear();

        var orderCount = r.Int(1, 3);
        for (var o = 0; o < orderCount; o++)
        {
            var id = $"ORD-{o + 1}";
            var lines = new List<OrderLine>();
            var lineCount = r.Int(1, 3);
            for (var l = 0; l < lineCount; l++)
            {
                var desc = r.Pick(Descriptions);
                lines.Add(new OrderLine(
                    $"{id}/OL-{l + 1}",
                    r.Chance(70) ? r.Pick(Skus) : null,
                    desc,
                    r.Chance(30) ? [desc + " alias"] : [],
                    r.Pick(OrderQuantities),
                    r.Pick(Units),
                    r.Pick(Prices),
                    0.20m));
            }

            s.Orders.Add(new PurchaseOrder(
                id,
                $"PO-{1000 + o}",
                r.Chance(80) ? OrderStatus.Open : OrderStatus.Closed,
                r.Chance(80) ? TestData.Scenario.Supplier1 : TestData.Scenario.Supplier2,
                r.Chance(80) ? TestData.Scenario.Buyer1 : TestData.Scenario.Buyer2,
                r.Chance(90) ? "EUR" : "USD",
                lines));

            foreach (var line in lines)
            {
                if (r.Chance(75))
                {
                    var parts = r.Int(1, 2);
                    for (var p = 0; p < parts; p++)
                    {
                        s.Receipts.Add(new GoodsReceipt($"GR-{line.LineId}-{p}", r.Chance(90) ? id : "ORD-X", line.LineId, line.Quantity / parts, r.Chance(85)));
                    }
                }
            }
        }

        s.ReceiptsAvailable = r.Chance(85);

        // Document lines derived from one order (sometimes mixing in a foreign line).
        var source = r.Pick(s.Orders);
        var docLines = new List<InvoiceLine>();
        var docLineCount = r.Int(1, 3);
        for (var i = 0; i < docLineCount; i++)
        {
            var ol = r.Chance(85) ? r.Pick(source.Lines) : r.Pick(r.Pick(s.Orders).Lines);
            var sku = r.Int(5) switch
            {
                0 => null,
                1 => ol.Sku?.ToLowerInvariant(),
                2 => "SKU-ZZ",
                _ => ol.Sku,
            };
            var description = r.Int(5) switch
            {
                0 => "  " + ol.Description.ToUpperInvariant() + " ",
                1 => ol.ExplicitAliases.Count > 0 ? ol.ExplicitAliases[0] : ol.Description,
                2 => "Unrelated item",
                _ => ol.Description,
            };
            var unit = r.Chance(85) ? ol.Unit : r.Pick(Units);
            var price = ol.UnitNetPrice + r.Pick(new[] { 0m, 0m, 0m, 0.01m, -0.01m, 0.02m });
            if (price < 0)
            {
                price = 0m;
            }

            var line = TestData.Scenario.Line($"L{i + 1}", description, sku, r.Pick(InvoiceQuantities), unit, price, r.Pick(Rates), declareTax: r.Chance(85));
            if (r.Chance(8))
            {
                line = line with { DeclaredNetAmount = line.DeclaredNetAmount + 0.01m };
            }

            if (r.Chance(5) && line.DeclaredTaxAmount is { } tax)
            {
                line = line with { DeclaredTaxAmount = tax - 0.01m };
            }

            docLines.Add(line);
        }

        s.WithLines([.. docLines]);
        s.WithReference(r.Int(6) switch
        {
            0 => null,
            1 => null,
            2 => "PO-9999",
            3 => " " + source.Reference.ToLowerInvariant(),
            _ => source.Reference,
        });
        s.WithSupplierRegId(r.Int(8) switch
        {
            0 => null,
            1 => "XX-NOPE",
            2 => TestData.Scenario.Supplier2RegId,
            _ => TestData.Scenario.Supplier1RegId,
        });
        switch (r.Int(8))
        {
            case 0: s.WithBuyer(null, null); break;
            case 1: s.WithBuyer(" fictional  siteworks ", null); break;
            case 2: s.WithBuyer("Nobody", null); break;
            case 3: s.WithBuyer(null, "XX-NOPE"); break;
            case 4: s.WithBuyer("Other Buyer Ltd", TestData.Scenario.Buyer2RegId); break;
            default: break;
        }

        s.WithCurrency(r.Int(10) switch { 0 => null, 1 => "USD", _ => "EUR" });
        s.WithIban(r.Int(8) switch
        {
            0 => null,
            1 => "se00 test 0000 0000 0000 0001",
            2 => TestData.Scenario.Supplier2Iban,
            3 => "XX00BAD",
            _ => TestData.Scenario.Supplier1Iban,
        });

        if (r.Chance(10))
        {
            s.AutoTotals = false;
            var built = s.BuiltDocument();
            s.WithDocument(d => d with
            {
                DeclaredSubtotal = built.Lines.Sum(l => SpecMath.Net(l.Quantity, l.UnitNetPrice)) + (r.Chance(50) ? 0.01m : 0m),
                DeclaredTaxTotal = built.Lines.Sum(l => SpecMath.Tax(SpecMath.Net(l.Quantity, l.UnitNetPrice), l.TaxRate)),
                DeclaredTotal = 0m,
            });
        }

        // History: counted invoices of random lines; sometimes one with the current business key.
        var historyCount = r.Int(0, 2);
        for (var h = 0; h < historyCount; h++)
        {
            var order = r.Pick(s.Orders);
            var ol = r.Pick(order.Lines);
            var sameKey = r.Chance(20);
            var facts = s.BuiltDocument() with
            {
                DocumentId = $"HIST-{h}",
                Number = sameKey ? TestData.Scenario.DocumentNumber.ToLowerInvariant() : $"INV-H{h}",
                IssueDate = r.Chance(50) ? s.Document.IssueDate : "2026-01-01",
            };
            s.AddHistory(TestData.Scenario.HistoryFor(facts, TestData.Scenario.Supplier1, TestData.Scenario.Buyer1, order.OrderId, r.Chance(85), (ol.LineId, r.Pick(new[] { 5m, 10m, 40m }))));
        }

        if (r.Chance(10))
        {
            var earlier = s.BuiltDocument() with { DocumentId = "DOC-0", IssueDate = r.Chance(50) ? s.Document.IssueDate : "2026-01-02" };
            s.AddEarlier(earlier, r.Chance(50) ? "ASM-0" : null);
        }

        // Proposals: only those registration would accept.
        var proposalCount = r.Chance(40) ? r.Int(1, 2) : 0;
        for (var p = 0; p < proposalCount; p++)
        {
            var order = r.Pick(s.Orders);
            var matches = new List<LineMatch>();
            foreach (var dl in s.Document.Lines)
            {
                if (r.Chance(50))
                {
                    matches.Add(new LineMatch(dl.LineId, r.Pick(order.Lines).LineId));
                }
            }

            var proposal = new MatchProposal($"P{p}", TestData.Scenario.DocumentId, order.OrderId, matches, r.Chance(40));
            if (ProposalRules.Validate(s.Context(), s.BuiltDocument(), proposal).Count == 0)
            {
                s.Proposals.Add(proposal);
            }
        }

        return s;
    }
}
