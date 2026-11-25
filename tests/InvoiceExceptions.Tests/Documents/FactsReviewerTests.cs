using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Documents;
using InvoiceExceptions.Infrastructure.Documents;

namespace InvoiceExceptions.Tests.Documents;

public sealed class FactsReviewerTests
{
    private static readonly PdfTextSource Pdf = new();

    /// <summary>Every reference extraction of the dataset next to the PDF it was read from.</summary>
    public static IEnumerable<object[]> ReferenceExtractions() =>
        from caseDirectory in Directory.EnumerateDirectories(Path.Combine(TestPaths.Dataset, "cases")).Order(StringComparer.Ordinal)
        from pair in new[] { ("document-001.pdf", "extraction.json"), ("original-001.pdf", "original-extraction.json") }
        let pdf = Path.Combine(caseDirectory, "documents", pair.Item1)
        let extraction = Path.Combine(caseDirectory, "expected", pair.Item2)
        where File.Exists(pdf) && File.Exists(extraction)
        select new object[] { Path.GetFileName(caseDirectory), pair.Item1, pair.Item2 };

    [Theory]
    [MemberData(nameof(ReferenceExtractions))]
    public void Every_reference_excerpt_is_found_on_its_page_and_nothing_is_unaccounted_for(string caseId, string pdf, string extraction)
    {
        var caseDirectory = Path.Combine(TestPaths.Dataset, "cases", caseId);
        var facts = WireJson.Deserialize<FactsDto>(File.ReadAllText(Path.Combine(caseDirectory, "expected", extraction)));

        var review = FactsReviewer.Review(facts, Pdf.ReadPages(Path.Combine(caseDirectory, "documents", pdf)));

        Assert.All(review.Evidence, e => Assert.Equal(EvidenceStatus.Found, e.Status));
        Assert.Equal(facts.Evidence.Count, review.Evidence.Count);
        Assert.Empty(review.Notes);
    }

    [Fact]
    public void Dataset_pdfs_are_byte_identical_to_the_manifest()
    {
        // Guards the .gitattributes entry: without it Git rewrites line endings inside the PDFs on Windows.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Dataset, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(TestPaths.Dataset, file.GetProperty("path").GetString()!);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.True(file.GetProperty("sha256").GetString() == actual, $"{path} differs from the manifest.");
        }
    }

    [Fact]
    public void An_excerpt_that_is_not_in_the_document_is_reported()
    {
        var (facts, pages) = Case004(evidence => evidence["excerpt"] = "approved by the buyer on 2026-10-01");

        var review = FactsReviewer.Review(facts, pages);

        var notFound = Assert.Single(review.Evidence, e => e.Status == EvidenceStatus.NotFound);
        Assert.Equal("/number", notFound.FieldPath);
    }

    [Fact]
    public void An_excerpt_on_the_wrong_page_is_reported()
    {
        var (facts, pages) = Case004(evidence => evidence["page"] = 2);

        var review = FactsReviewer.Review(facts, pages);

        Assert.Equal(1, review.NotFound);
    }

    [Fact]
    public void Spacing_differences_between_excerpt_and_page_do_not_matter()
    {
        var (facts, pages) = Case004(evidence => evidence["excerpt"] = "INV-2026-4104".Replace("-", " - "), field: "/number");
        var spaced = pages!.Select(p => p with { Text = p.Text.Replace("INV-2026-4104", "INV - 2026 - 4104") }).ToList();

        Assert.Equal(0, FactsReviewer.Review(facts, spaced).NotFound);
    }

    [Fact]
    public void Without_document_text_nothing_is_claimed_either_way()
    {
        var (facts, _) = Case004(_ => { });

        var review = FactsReviewer.Review(facts, pages: null);

        Assert.All(review.Evidence, e => Assert.Equal(EvidenceStatus.NotChecked, e.Status));
    }

    [Fact]
    public void A_null_that_is_not_declared_missing_is_noted()
    {
        var facts = Mutate(root => root["due_date"] = null);

        var note = Assert.Single(FactsReviewer.Review(facts, null).Notes, n => n.Message.Contains("missing_fields", StringComparison.Ordinal));

        Assert.Equal("/due_date", note.FieldPath);
    }

    [Fact]
    public void A_value_without_evidence_is_noted()
    {
        var facts = Mutate(root =>
        {
            var evidence = root["evidence"]!.AsArray();
            evidence.Remove(evidence.First(e => e!["field_path"]!.GetValue<string>() == "/bank/iban"));
        });

        var note = Assert.Single(FactsReviewer.Review(facts, null).Notes);

        Assert.Equal("/bank/iban", note.FieldPath);
        Assert.Contains("no evidence", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_declared_missing_that_has_a_value_is_noted()
    {
        var facts = Mutate(root => root["missing_fields"]!.AsArray().Add(
            new JsonObject { ["field_path"] = "/currency", ["reason"] = "not_present_in_document" }));

        Assert.Contains(FactsReviewer.Review(facts, null).Notes, n => n.FieldPath == "/currency" && n.Message.Contains("has a value", StringComparison.Ordinal));
    }

    [Fact]
    public void Evidence_for_a_path_that_is_not_a_fact_is_noted()
    {
        var facts = Mutate(root => root["evidence"]!.AsArray().Add(new JsonObject
        {
            ["field_path"] = "/lines/7/quantity", ["document_id"] = "DOC-004", ["page"] = 1, ["excerpt"] = "80",
        }));

        Assert.Contains(FactsReviewer.Review(facts, null).Notes, n => n.FieldPath == "/lines/7/quantity");
    }

    [Fact]
    public void The_text_source_answers_no_text_for_a_missing_or_unreadable_file()
    {
        var notPdf = Path.Combine(Path.GetTempPath(), $"not-a-pdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(notPdf, "this is not a PDF");
        try
        {
            Assert.Null(Pdf.ReadPages(notPdf));
            Assert.Null(Pdf.ReadPages(notPdf + ".missing"));
        }
        finally
        {
            File.Delete(notPdf);
        }
    }

    private static (FactsDto Facts, IReadOnlyList<PageText>? Pages) Case004(Action<JsonObject> changeEvidence, string field = "/number")
    {
        var facts = Mutate(root => changeEvidence(
            root["evidence"]!.AsArray().First(e => e!["field_path"]!.GetValue<string>() == field)!.AsObject()));
        return (facts, Pdf.ReadPages(Path.Combine(TestPaths.Dataset, "cases", "case-004", "documents", "document-001.pdf")));
    }

    private static FactsDto Mutate(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Dataset, "cases", "case-004", "expected", "extraction.json")))!.AsObject();
        change(root);
        return WireJson.Deserialize<FactsDto>(root.ToJsonString());
    }
}
