using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvoiceExceptions.Application.Catalog;

namespace InvoiceExceptions.Infrastructure.Catalog;

/// <summary>
/// Reads cases from a dataset folder laid out as dataset/README.md describes:
/// <c>cases/case-NNN/case.json</c>, <c>context/*.json</c> and <c>documents/*.pdf</c>.
/// It deliberately never opens <c>steps/</c> or <c>expected/</c>: those hold reference facts and answers.
/// </summary>
public sealed partial class DatasetCaseCatalog(string datasetDirectory) : ICaseCatalog
{
    // The context files and the property each one becomes in the snapshot, in a fixed order.
    private static readonly (string File, string Property)[] ContextFiles =
    [
        ("purchase-orders.json", "purchase_orders"),
        ("goods-receipts.json", "goods_receipts"),
        ("suppliers.json", "suppliers"),
        ("buyers.json", "buyers"),
        ("invoice-history.json", "invoice_history"),
    ];

    public string DatasetDirectory { get; } = Path.GetFullPath(datasetDirectory);

    public IReadOnlyList<string> ListCaseIds()
    {
        var cases = Path.Combine(DatasetDirectory, "cases");
        if (!Directory.Exists(cases))
        {
            return [];
        }

        return Directory.EnumerateDirectories(cases)
            .Select(Path.GetFileName)
            .Where(name => name is not null && CaseIdPattern().IsMatch(name) && File.Exists(Path.Combine(cases, name, "case.json")))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public CatalogCase? Find(string caseId)
    {
        // The id becomes part of a file path, so it is checked before it gets anywhere near the file system.
        if (!CaseIdPattern().IsMatch(caseId))
        {
            return null;
        }

        var directory = Path.Combine(DatasetDirectory, "cases", caseId);
        var manifestPath = Path.Combine(directory, "case.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;

        var context = new JsonObject();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (file, property) in ContextFiles)
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "context", file)));
            context[property] = node;
            hash.AppendData(Encoding.UTF8.GetBytes(property));
            hash.AppendData(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"));
        }

        // The context files carry no version of their own, so the version is derived from their content:
        // the same files always give the same version, and any edit gives a new one.
        var snapshot = new JsonObject { ["context_version"] = "ctx-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..12] };
        foreach (var (name, value) in context.ToList())
        {
            context.Remove(name);
            snapshot[name] = value;
        }

        var documents = root.GetProperty("input_documents").EnumerateArray()
            .Select(document =>
            {
                var path = Path.GetFullPath(Path.Combine(directory, document.GetProperty("path").GetString()!));
                return new CatalogDocument(
                    document.GetProperty("document_id").GetString()!,
                    path,
                    File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null);
            })
            .ToList();

        return new CatalogCase(
            caseId,
            root.GetProperty("as_of").GetString()!,
            JsonSerializer.SerializeToElement(snapshot),
            documents);
    }

    [GeneratedRegex("^case-[0-9]{3}$")]
    private static partial Regex CaseIdPattern();
}
