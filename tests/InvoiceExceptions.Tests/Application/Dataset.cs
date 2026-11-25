using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceExceptions.Tests.Application;

/// <summary>Reads the published dataset: case folders, step inputs, expected outputs and reference extractions.</summary>
internal static class Dataset
{
    public static string CasesDirectory => Path.Combine(TestPaths.Dataset, "cases");

    public static string CaseDirectory(string caseId) => Path.Combine(CasesDirectory, caseId);

    public static IReadOnlyList<string> CaseIds() =>
        [.. Directory.GetDirectories(CasesDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && File.Exists(Path.Combine(CasesDirectory, name, "case.json")))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)];

    public static TheoryData<string> CaseIdData()
    {
        var data = new TheoryData<string>();
        foreach (var id in CaseIds())
        {
            data.Add(id);
        }

        return data;
    }

    /// <summary>Step file names (step-001.json, ...) in order.</summary>
    public static IReadOnlyList<string> StepFiles(string caseId) =>
        [.. Directory.GetFiles(Path.Combine(CaseDirectory(caseId), "steps"), "step-*.json")
            .Select(Path.GetFileName)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)];

    public static string StepText(string caseId, int step) =>
        File.ReadAllText(Path.Combine(CaseDirectory(caseId), "steps", $"step-{step:000}.json"));

    public static JsonObject Step(string caseId, int step) => JsonNode.Parse(StepText(caseId, step))!.AsObject();

    public static JsonElement Expected(string caseId, int step) =>
        Parse(File.ReadAllText(Path.Combine(CaseDirectory(caseId), "expected", $"step-{step:000}.json")));

    public static JsonObject Extraction(string caseId) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(CaseDirectory(caseId), "expected", "extraction.json")))!.AsObject();

    public static JsonElement CaseManifest(string caseId) =>
        Parse(File.ReadAllText(Path.Combine(CaseDirectory(caseId), "case.json")));

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);
}
