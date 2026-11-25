using System.Globalization;
using System.Text.Json;

namespace InvoiceExceptions.Tests.Application;

/// <summary>
/// A port of <c>compare()</c> in tools/run_evals.py: the authoritative definition of "the output matches the expected
/// result". Returns the differences; empty means the step passed.
/// </summary>
internal static class StepComparer
{
    private static readonly string[] DecimalLineFields =
        ["calculated_net", "calculated_tax", "ordered", "received", "previously_invoiced", "available_to_invoice", "remaining_on_order"];

    public static readonly string[] ExactFields =
        ["case_id", "step", "document_id", "context_version", "import_disposition", "original_document_id", "matched_order_id", "order_match_source", "decision"];

    /// <param name="ignoreFields">Exact fields to leave out, for outputs that legitimately differ (e.g. context_version over MCP).</param>
    public static List<string> Compare(JsonElement expected, JsonElement actual, params string[] ignoreFields)
    {
        if (expected.TryGetProperty("error", out _))
        {
            return actual.TryGetProperty("error", out _) ? [] : ["expected VALIDATION_ERROR, got a result"];
        }

        if (actual.TryGetProperty("error", out var error))
        {
            return [$"unexpected VALIDATION_ERROR: {error}"];
        }

        var diffs = new List<string>();
        foreach (var field in ExactFields.Except(ignoreFields))
        {
            var e = expected.GetProperty(field);
            if (!actual.TryGetProperty(field, out var a) || !JsonElement.DeepEquals(e, a))
            {
                diffs.Add($"{field}: expected {e}, got {(actual.TryGetProperty(field, out var got) ? got.ToString() : "<missing>")}");
            }
        }

        var expTotals = expected.GetProperty("totals");
        var actTotals = actual.GetProperty("totals");
        if ((expTotals.ValueKind == JsonValueKind.Null) != (actTotals.ValueKind == JsonValueKind.Null))
        {
            diffs.Add($"totals: expected {expTotals}, got {actTotals}");
        }
        else if (expTotals.ValueKind != JsonValueKind.Null)
        {
            foreach (var property in expTotals.EnumerateObject())
            {
                var actualValue = actTotals.TryGetProperty(property.Name, out var v) ? v : default;
                if (AsDecimal(property.Value) != AsDecimal(actualValue))
                {
                    diffs.Add($"totals.{property.Name}: expected {property.Value}, got {actualValue}");
                }
            }
        }

        var expLines = expected.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("document_line_id").GetString()!);
        var actLines = new Dictionary<string, JsonElement>();
        foreach (var line in actual.GetProperty("lines").EnumerateArray())
        {
            var id = line.GetProperty("document_line_id").GetString()!;
            if (!actLines.TryAdd(id, line))
            {
                diffs.Add($"lines: duplicate document_line_id {id}");
                actLines[id] = line;
            }
        }

        foreach (var id in expLines.Keys.Union(actLines.Keys).Order(StringComparer.Ordinal))
        {
            if (!expLines.TryGetValue(id, out var exp) || !actLines.TryGetValue(id, out var act))
            {
                diffs.Add($"lines: {id} {(expLines.ContainsKey(id) ? "missing" : "unexpected")}");
                continue;
            }

            foreach (var key in new[] { "order_line_id", "match_source" })
            {
                if (!JsonElement.DeepEquals(exp.GetProperty(key), act.GetProperty(key)))
                {
                    diffs.Add($"line {id}.{key}: expected {exp.GetProperty(key)}, got {act.GetProperty(key)}");
                }
            }

            foreach (var key in DecimalLineFields)
            {
                if (AsDecimal(exp.GetProperty(key)) != AsDecimal(act.GetProperty(key)))
                {
                    diffs.Add($"line {id}.{key}: expected {exp.GetProperty(key)}, got {act.GetProperty(key)}");
                }
            }
        }

        var expChecks = CheckMap(expected, diffs);
        var actChecks = CheckMap(actual, diffs);
        foreach (var key in expChecks.Keys.Union(actChecks.Keys).Order(StringComparer.Ordinal))
        {
            if (!actChecks.TryGetValue(key, out var act))
            {
                diffs.Add($"check {key}: missing");
            }
            else if (!expChecks.TryGetValue(key, out var exp))
            {
                diffs.Add($"check {key}: unexpected");
            }
            else if (exp != act)
            {
                diffs.Add($"check {key}: expected {exp}, got {act}");
            }
        }

        var expQuestions = Questions(expected);
        var actQuestions = Questions(actual);
        if (!expQuestions.SequenceEqual(actQuestions))
        {
            diffs.Add($"questions: expected [{string.Join(", ", expQuestions)}], got [{string.Join(", ", actQuestions)}]");
        }

        return diffs;
    }

    private static Dictionary<string, (string? Status, string? Severity, string? DependsOn)> CheckMap(JsonElement result, List<string> diffs)
    {
        var map = new Dictionary<string, (string?, string?, string?)>();
        foreach (var check in result.GetProperty("checks").EnumerateArray())
        {
            var key = $"{check.GetProperty("code").GetString()}/{check.GetProperty("object_id").GetString()}";
            if (map.ContainsKey(key))
            {
                diffs.Add($"checks: duplicate {key}");
            }

            map[key] = (Text(check, "status"), Text(check, "severity"), Text(check, "depends_on"));
        }

        return map;
    }

    /// <summary>The questions as a sorted multiset of (required_fact, sorted target_object_ids).</summary>
    private static List<string> Questions(JsonElement result) =>
        [.. result.GetProperty("questions").EnumerateArray()
            .Select(q => q.GetProperty("required_fact").GetString() + " -> [" +
                string.Join(",", q.GetProperty("target_object_ids").EnumerateArray().Select(t => t.GetString()).Order(StringComparer.Ordinal)) + "]")
            .Order(StringComparer.Ordinal)];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Python's Decimal equality: "60" equals "60.000"; null equals null only.</summary>
    private static decimal? AsDecimal(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            JsonValueKind.Number => value.GetDecimal(),
            _ => null,
        };
}
