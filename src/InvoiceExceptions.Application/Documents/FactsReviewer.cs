using System.Text;
using System.Text.Json;
using InvoiceExceptions.Application.Contracts;

namespace InvoiceExceptions.Application.Documents;

/// <summary>Reads the text layer of a source document. There is no OCR: a scanned page simply has no text.</summary>
public interface IDocumentTextSource
{
    /// <summary>The text of every page, or null when the file is missing or cannot be read as a PDF.</summary>
    IReadOnlyList<PageText>? ReadPages(string path);
}

public sealed record PageText(int Page, string Text);

public enum EvidenceStatus
{
    /// <summary>The excerpt occurs on the stated page of the document.</summary>
    Found,

    /// <summary>The excerpt does not occur on the stated page.</summary>
    NotFound,

    /// <summary>The document text is not available, so nothing could be compared.</summary>
    NotChecked,
}

public sealed record EvidenceCheck(string FieldPath, int Page, string Excerpt, EvidenceStatus Status);

/// <param name="Evidence">One entry per evidence item of the facts, in the same order.</param>
/// <param name="Notes">Gaps in the provenance of the facts: path of the fact and what is missing.</param>
public sealed record FactsReview(IReadOnlyList<EvidenceCheck> Evidence, IReadOnlyList<ReviewNote> Notes)
{
    public int NotFound => Evidence.Count(e => e.Status == EvidenceStatus.NotFound);
}

public sealed record ReviewNote(string FieldPath, string Message);

/// <summary>
/// Looks at how well a set of facts is backed by its document: is every excerpt really on the page it names,
/// does every stated value have a source, is every absent value declared as missing.
/// <para>
/// This is a quality signal for the agent and for the person reviewing the case. It never changes a conclusion:
/// the rules work on the facts as registered, and whether those were read correctly is shown next to them.
/// </para>
/// </summary>
public static class FactsReviewer
{
    // Facts that identify things rather than state what is printed; they need no excerpt.
    private static readonly HashSet<string> Structural = ["schema_version", "document_id", "missing_fields", "evidence"];

    public static FactsReview Review(FactsDto facts, IReadOnlyList<PageText>? pages)
    {
        var searchable = pages?.ToDictionary(p => p.Page, p => Squeeze(p.Text));
        var evidence = facts.Evidence
            .Select(e => new EvidenceCheck(e.FieldPath, e.Page, e.Excerpt, searchable is null
                ? EvidenceStatus.NotChecked
                : searchable.TryGetValue(e.Page, out var text) && text.Contains(Squeeze(e.Excerpt), StringComparison.Ordinal)
                    ? EvidenceStatus.Found
                    : EvidenceStatus.NotFound))
            .ToList();

        var notes = new List<ReviewNote>();
        var withEvidence = facts.Evidence.Select(e => e.FieldPath).ToHashSet(StringComparer.Ordinal);
        var declaredMissing = facts.MissingFields.Select(m => m.FieldPath).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Flatten(JsonSerializer.SerializeToElement(facts, WireJson.Options), string.Empty, values);

        foreach (var (path, value) in values)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!declaredMissing.Contains(path))
                {
                    notes.Add(new(path, "The value is null but is not listed in missing_fields with a reason."));
                }
            }
            else if (!withEvidence.Contains(path))
            {
                notes.Add(new(path, "The value has no evidence (page and verbatim excerpt)."));
            }
        }

        foreach (var path in declaredMissing.Where(path => values.TryGetValue(path, out var value) && value.ValueKind != JsonValueKind.Null))
        {
            notes.Add(new(path, "The field is listed in missing_fields but has a value."));
        }

        foreach (var path in withEvidence.Concat(declaredMissing).Distinct().Where(path => !values.ContainsKey(path)))
        {
            notes.Add(new(path, "This path does not point at a fact of the document."));
        }

        return new FactsReview(evidence, notes);
    }

    private static void Flatten(JsonElement element, string path, Dictionary<string, JsonElement> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (path.Length == 0 && Structural.Contains(property.Name))
                    {
                        continue;
                    }

                    // A line id is our own label for the line, not something the document prints.
                    if (property.Name == "line_id")
                    {
                        continue;
                    }

                    Flatten(property.Value, $"{path}/{property.Name}", values);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{path}/{index++}", values);
                }

                break;
            default:
                values[path] = element;
                break;
        }
    }

    /// <summary>Whitespace is layout, not content: "DE00 SYNT" on the page and "DE00  SYNT" in an excerpt are the same text.</summary>
    private static string Squeeze(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
