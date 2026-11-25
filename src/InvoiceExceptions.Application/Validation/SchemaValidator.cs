using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace InvoiceExceptions.Application.Validation;

/// <summary>
/// Validates input against the published JSON Schemas (dataset/schemas), which are embedded into this assembly
/// unchanged. The schemas are the format contract; no shape rule is duplicated in code.
/// </summary>
public sealed class SchemaValidator
{
    private const string SchemaBase = "https://invoice-exception-case-study.example/schemas/";
    private static readonly Lazy<SchemaValidator> Instance = new(() => new SchemaValidator());

    private readonly JsonSchema _stepInput;
    private readonly Dictionary<string, JsonDocument> _sources;
    private readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.Hierarchical };

    private SchemaValidator()
    {
        var assembly = typeof(SchemaValidator).Assembly;
        var build = new BuildOptions { SchemaRegistry = new SchemaRegistry() };

        var texts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("schemas/", StringComparison.Ordinal))
            .ToDictionary(name => name["schemas/".Length..], name => ReadResource(assembly, name));
        _sources = texts.ToDictionary(pair => pair.Key, pair => JsonDocument.Parse(pair.Value));

        // A schema can only be built once everything it references is registered, so build in dependency order.
        var built = new Dictionary<string, JsonSchema>();
        var pending = new Queue<string>(texts.Keys.OrderBy(name => name, StringComparer.Ordinal));
        var stalled = 0;
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (References(_sources[name].RootElement).All(reference => reference == name || built.ContainsKey(reference)))
            {
                built[name] = JsonSchema.FromText(texts[name], build);
                stalled = 0;
            }
            else
            {
                pending.Enqueue(name);
                if (++stalled > pending.Count)
                {
                    throw new InvalidOperationException($"Schema '{name}' references a schema that is not embedded.");
                }
            }
        }

        _stepInput = built["step-input.schema.json"];
    }

    public static SchemaValidator Shared => Instance.Value;

    /// <summary>Validates a complete change of input (step-input.schema.json). Paths are JSON Pointers into the input.</summary>
    public IReadOnlyList<ValidationIssue> ValidateStepInput(JsonElement input)
    {
        var results = _stepInput.Evaluate(input, _evaluation);
        if (results.IsValid)
        {
            return [];
        }

        var failures = new List<Failure>();
        Collect(results, failures);

        // A value that fails "A or null" reports the failure of each alternative plus one for the combinator itself.
        // The alternative says what is actually wrong, so the combinator line and the "should be null" line are
        // dropped wherever something more specific exists at the same location.
        var issues = failures
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var specific = group.Where(f => !IsCombinator(f.Keyword) && !f.IsNullAlternative).ToList();
                var kept = specific.Count > 0 ? specific : group.ToList();
                return new ValidationIssue(group.Key, string.Join("; ", kept.Select(Describe).Distinct()));
            })
            .ToList();

        return issues.Count > 0 ? issues : [new ValidationIssue(string.Empty, "The input does not match step-input.schema.json.")];
    }

    /// <summary>
    /// Walks only the failures that caused the rejection. A valid node hides its children; the condition of an
    /// if/then/else is a test rather than a requirement; and the losing alternatives of an anyOf/oneOf matter
    /// only when the combinator itself failed.
    /// </summary>
    private static void Collect(EvaluationResults node, List<Failure> failures)
    {
        if (node.IsValid)
        {
            return;
        }

        var errors = node.Errors;
        if (errors is { Count: > 0 })
        {
            failures.AddRange(errors.Select(error =>
                new Failure(node.InstanceLocation.ToString(), error.Key, error.Value, node.SchemaLocation)));
        }

        var prefix = node.EvaluationPath.ToString();
        var byKeyword = (node.Details ?? [])
            .GroupBy(child => child.EvaluationPath.ToString()[prefix.Length..].TrimStart('/').Split('/')[0]);
        foreach (var group in byKeyword)
        {
            var satisfied = group.Key switch
            {
                "if" => true,
                "anyOf" => group.Any(child => child.IsValid),
                "oneOf" => group.Count(child => child.IsValid) == 1,
                _ => false,
            };
            if (satisfied)
            {
                continue;
            }

            foreach (var child in group)
            {
                Collect(child, failures);
            }
        }
    }

    /// <summary>Turns a keyword failure into a sentence that names what was expected, taken from the schema itself.</summary>
    private string Describe(Failure failure)
    {
        var schema = Resolve(failure.SchemaLocation);
        switch (failure.Keyword)
        {
            case "pattern" when schema is { } s && s.TryGetProperty("pattern", out var pattern):
                var description = s.TryGetProperty("description", out var text) ? $"{text.GetString()} " : string.Empty;
                return $"The value has the wrong format. {description}It must match {pattern.GetString()}";
            case "const" when schema is { } s && s.TryGetProperty("const", out var constant):
                return $"The value must be {constant.GetRawText()}";
            case "enum" when schema is { } s && s.TryGetProperty("enum", out var allowed):
                return $"The value must be one of {allowed.GetRawText()}";
            case "required":
                return failure.Message.Replace("Required properties", "Required properties are missing:", StringComparison.Ordinal)
                    .Replace(" are not present", string.Empty, StringComparison.Ordinal);
            default:
                return failure.Message.Contains("false schema", StringComparison.Ordinal)
                    ? "This property is not part of the format"
                    : failure.Message;
        }
    }

    private JsonElement? Resolve(Uri? schemaLocation)
    {
        if (schemaLocation is null)
        {
            return null;
        }

        var text = schemaLocation.OriginalString;
        var hash = text.IndexOf('#', StringComparison.Ordinal);
        var file = (hash < 0 ? text : text[..hash]).Replace(SchemaBase, string.Empty, StringComparison.Ordinal);
        if (!_sources.TryGetValue(file, out var document))
        {
            return null;
        }

        var element = document.RootElement;
        var pointer = hash < 0 ? string.Empty : Uri.UnescapeDataString(text[(hash + 1)..]);
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var property))
            {
                element = property;
            }
            else if (element.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index < element.GetArrayLength())
            {
                element = element[index];
            }
            else
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.Object ? element : null;
    }

    private static bool IsCombinator(string keyword) => keyword is "oneOf" or "anyOf" or "allOf";

    private static IEnumerable<string> References(JsonElement schema)
    {
        return Walk(schema).Distinct().ToList();

        static IEnumerable<string> Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("$ref") && property.Value.ValueKind == JsonValueKind.String)
                        {
                            var file = property.Value.GetString()!.Split('#')[0];
                            if (file.Length > 0)
                            {
                                yield return file.StartsWith(SchemaBase, StringComparison.Ordinal) ? file[SchemaBase.Length..] : file;
                            }
                        }
                        else
                        {
                            foreach (var nested in Walk(property.Value))
                            {
                                yield return nested;
                            }
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var nested in element.EnumerateArray().SelectMany(Walk))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Failure(string Path, string Keyword, string Message, Uri? SchemaLocation)
    {
        public bool IsNullAlternative => Keyword == "type" && Message.Contains("should be \"null\"", StringComparison.Ordinal);
    }
}
