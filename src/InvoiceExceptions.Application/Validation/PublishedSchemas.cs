using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceExceptions.Application.Validation;

/// <summary>Read access to the embedded copies of dataset/schemas, for callers that need to show a contract rather than enforce it.</summary>
public static class PublishedSchemas
{
    /// <summary>
    /// extraction.schema.json as one self-contained schema: every <c>$ref</c> is replaced by what it points to.
    /// Used as the input schema of the MCP tool that accepts document facts, so the agent is given the published
    /// format itself rather than a description of it.
    /// </summary>
    public static JsonObject FactsSchema()
    {
        var schema = Inline(Load("extraction.schema.json"), "extraction.schema.json").AsObject();
        schema.Remove("$schema");
        schema.Remove("$id");
        schema.Remove("$defs");
        return schema;
    }

    private static JsonNode Load(string file)
    {
        var assembly = typeof(PublishedSchemas).Assembly;
        using var stream = assembly.GetManifestResourceStream("schemas/" + file)
            ?? throw new InvalidOperationException($"Schema '{file}' is not embedded.");
        return JsonNode.Parse(stream)!;
    }

    private static JsonNode Inline(JsonNode node, string currentFile)
    {
        switch (node)
        {
            case JsonObject obj when obj["$ref"] is JsonValue reference:
            {
                var parts = reference.GetValue<string>().Split('#');
                var file = parts[0].Length == 0 ? currentFile : parts[0];
                var target = Load(file);
                foreach (var segment in (parts.Length > 1 ? parts[1] : string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    target = target[segment] ?? throw new InvalidOperationException($"Unresolvable reference '{reference}' in {currentFile}.");
                }

                // Keywords written next to the $ref (there are none today) would be lost here; fail rather than drop them silently.
                if (obj.Count != 1)
                {
                    throw new NotSupportedException($"A $ref with sibling keywords is not supported: {obj.ToJsonString()}");
                }

                return Inline(target.DeepClone(), file);
            }

            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    copy[name] = value is null ? null : Inline(value, currentFile);
                }

                return copy;
            }

            case JsonArray array:
                return new JsonArray([.. array.Select(item => item is null ? null : Inline(item, currentFile))]);

            default:
                return node.DeepClone();
        }
    }
}
