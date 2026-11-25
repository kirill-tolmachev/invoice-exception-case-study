using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace InvoiceExceptions.Tests.Application;

/// <summary>
/// dataset/schemas/step-output.schema.json, built from the files in the repository (not from the copies embedded in
/// the application), with every schema it references registered first.
/// </summary>
internal static partial class OutputSchema
{
    private static readonly Lazy<JsonSchema> StepOutput = new(Build);

    /// <summary>The reasons the output does not match the schema; empty when it does.</summary>
    public static IReadOnlyList<string> Problems(JsonElement output)
    {
        var results = StepOutput.Value.Evaluate(output, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var problems = (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}"))
            .ToList();
        return problems.Count > 0 ? problems : ["output does not match step-output.schema.json"];
    }

    private static JsonSchema Build()
    {
        var build = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var texts = Directory.GetFiles(Path.Combine(TestPaths.Dataset, "schemas"), "*.schema.json")
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllText);

        var built = new Dictionary<string, JsonSchema>();
        var pending = new Queue<string>(texts.Keys.Order(StringComparer.Ordinal));
        var stalled = 0;
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            var references = ReferencePattern().Matches(texts[name])
                .Select(m => m.Groups["file"].Value)
                .Where(file => file.Length > 0)
                .Select(file => file[(file.LastIndexOf('/') + 1)..]);
            if (references.All(file => file == name || built.ContainsKey(file)))
            {
                built[name] = JsonSchema.FromText(texts[name], build);
                stalled = 0;
            }
            else
            {
                pending.Enqueue(name);
                if (++stalled > pending.Count)
                {
                    throw new InvalidOperationException($"Cannot resolve the references of {name}.");
                }
            }
        }

        return built["step-output.schema.json"];
    }

    [GeneratedRegex("\"\\$ref\"\\s*:\\s*\"(?<file>[^\"#]*)")]
    private static partial Regex ReferencePattern();
}
