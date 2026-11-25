using System.Globalization;
using System.Text.Json;
using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Tests;

/// <summary>The constants in CoreV1 / Wire agree with dataset/policies/core-v1.json (copied to the output as policies/core-v1.json).</summary>
public sealed class PolicyPinTests : IDisposable
{
    private readonly JsonDocument _json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "policies", "core-v1.json")));

    private JsonElement Root => _json.RootElement;

    public void Dispose() => _json.Dispose();

    private static T FromWire<T>(string name, Func<T, string> wire) where T : struct, Enum
    {
        var matches = Enum.GetValues<T>().Where(v => wire(v) == name).ToList();
        Assert.True(matches.Count == 1, $"Expected exactly one {typeof(T).Name} whose wire name is '{name}', found {matches.Count}.");
        return matches[0];
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private IEnumerable<JsonElement> Checks => Root.GetProperty("checks").EnumerateArray();

    [Fact]
    public void Policy_version_matches()
    {
        Assert.Equal(CoreV1.PolicyVersion, Root.GetProperty("policy_version").GetString());
    }

    [Fact]
    public void Money_scale_and_rounding_match()
    {
        var numbers = Root.GetProperty("numbers");
        Assert.Equal(CoreV1.MoneyScale, numbers.GetProperty("money_scale").GetInt32());
        Assert.Equal("ROUND_HALF_UP", numbers.GetProperty("rounding").GetString());
        Assert.Equal(2.35m, Money.Round(2.345m));
        Assert.Equal(-2.35m, Money.Round(-2.345m));
    }

    [Fact]
    public void Unit_price_tolerance_matches()
    {
        var tolerance = Checks.Single(c => c.GetProperty("code").GetString() == "UNIT_PRICE").GetProperty("tolerance");
        Assert.Equal(decimal.Parse(tolerance.GetProperty("value").GetString()!, CultureInfo.InvariantCulture), CoreV1.UnitPriceTolerance);
        Assert.True(tolerance.GetProperty("inclusive").GetBoolean());
        Assert.True(tolerance.GetProperty("symmetric").GetBoolean());
    }

    [Fact]
    public void Document_check_instances_match()
    {
        var expected = Strings(Root.GetProperty("check_instances").GetProperty("document"));
        Assert.Equal(expected, CoreV1.DocumentChecks.Select(Wire.Name).ToList());
    }

    [Fact]
    public void Line_check_instances_match()
    {
        var expected = Strings(Root.GetProperty("check_instances").GetProperty("line"));
        Assert.Equal(expected, CoreV1.LineChecks.Select(Wire.Name).ToList());
    }

    [Fact]
    public void Evaluation_order_matches()
    {
        var expected = Strings(Root.GetProperty("evaluation_order"));
        Assert.Equal(expected, CoreV1.EvaluationOrder.Select(Wire.Name).ToList());
    }

    [Fact]
    public void Every_check_code_appears_exactly_once_in_the_policy_checks()
    {
        var codes = Checks.Select(c => c.GetProperty("code").GetString()!).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(codes.Order(StringComparer.Ordinal), Enum.GetValues<CheckCode>().Select(Wire.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Fail_severity_of_every_check_matches()
    {
        foreach (var check in Checks)
        {
            var code = FromWire<CheckCode>(check.GetProperty("code").GetString()!, Wire.Name);
            Assert.Equal($"{Wire.Name(code)}: {check.GetProperty("fail_severity").GetString()}", $"{Wire.Name(code)}: {Wire.Name(CoreV1.FailSeverity(code))}");
        }
    }

    [Fact]
    public void Required_fact_of_every_check_matches()
    {
        foreach (var check in Checks)
        {
            var code = FromWire<CheckCode>(check.GetProperty("code").GetString()!, Wire.Name);
            var json = check.GetProperty("required_fact");
            var expected = json.ValueKind == JsonValueKind.Null ? "null" : json.GetString();
            var actual = CoreV1.RequiredFactOf(code) is { } fact ? Wire.Name(fact) : "null";
            Assert.Equal($"{Wire.Name(code)}: {expected}", $"{Wire.Name(code)}: {actual}");
        }
    }

    [Fact]
    public void Required_fact_dictionary_and_texts_match()
    {
        var texts = Root.GetProperty("required_facts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(texts.Keys.Order(StringComparer.Ordinal), Enum.GetValues<RequiredFact>().Select(Wire.Name).Order(StringComparer.Ordinal));
        foreach (var (name, text) in texts)
        {
            Assert.Equal(text, CoreV1.QuestionText(FromWire<RequiredFact>(name, Wire.Name)));
        }
    }

    [Fact]
    public void Wire_covers_every_check_status()
    {
        var expected = Strings(Root.GetProperty("check_statuses"));
        Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetValues<CheckStatus>().Select(Wire.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Wire_covers_every_match_source()
    {
        var expected = Strings(Root.GetProperty("match_sources"));
        Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetValues<MatchSource>().Select(Wire.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Wire_covers_every_decision_and_the_enum_is_declared_in_order_of_strictness()
    {
        // decision.priority lists the strictest first; the Decision enum is declared from least to most strict.
        var priority = Strings(Root.GetProperty("decision").GetProperty("priority"));
        var declared = Enum.GetValues<Decision>().OrderByDescending(d => (int)d).Select(Wire.Name).ToList();
        Assert.Equal(priority, declared);
    }

    [Fact]
    public void Wire_covers_every_severity_used_by_the_policy()
    {
        var used = Checks.Select(c => c.GetProperty("fail_severity").GetString()!).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(used, Enum.GetValues<Severity>().Select(Wire.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Wire_covers_every_import_disposition_used_by_the_policy()
    {
        var used = Checks
            .SelectMany(c => c.TryGetProperty("outcomes", out var o) ? o.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(o => o.TryGetProperty("import_disposition", out _))
            .Select(o => o.GetProperty("import_disposition").GetString()!)
            .Distinct()
            .Order(StringComparer.Ordinal);
        Assert.Equal(used, Enum.GetValues<ImportDisposition>().Select(Wire.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_depends_on_in_the_policy_is_a_known_check_code()
    {
        var targets = Checks
            .SelectMany(c => c.TryGetProperty("outcomes", out var o) ? o.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(o => o.TryGetProperty("depends_on", out var d) && d.ValueKind == JsonValueKind.String)
            .Select(o => o.GetProperty("depends_on").GetString()!)
            .Distinct();
        foreach (var target in targets)
        {
            FromWire<CheckCode>(target, Wire.Name);
        }
    }

    /// <summary>The hand-written tables the invariant tests rely on (TestData/Spec.cs) are themselves pinned to the JSON.</summary>
    [Fact]
    public void Test_side_spec_tables_agree_with_the_json()
    {
        foreach (var check in Checks)
        {
            var name = check.GetProperty("code").GetString()!;
            var code = FromWire<CheckCode>(name, Wire.Name);
            Assert.Equal($"{name}: {check.GetProperty("fail_severity").GetString()}", $"{name}: {Wire.Name(TestData.Spec.FailSeverity(code))}");

            var fact = check.GetProperty("required_fact");
            Assert.Equal(
                $"{name}: {(fact.ValueKind == JsonValueKind.Null ? "null" : fact.GetString())}",
                $"{name}: {(TestData.Spec.RequiredFact(code) is { } f ? Wire.Name(f) : "null")}");

            var dependsOn = (check.TryGetProperty("outcomes", out var o) ? o.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Where(x => x.TryGetProperty("depends_on", out var d) && d.ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("depends_on").GetString()!)
                .Distinct()
                .Order(StringComparer.Ordinal);
            Assert.Equal($"{name}: {string.Join(",", dependsOn)}", $"{name}: {string.Join(",", TestData.Spec.AllowedDependsOn(code).Select(Wire.Name).Order(StringComparer.Ordinal))}");
        }

        Assert.Equal(Strings(Root.GetProperty("check_instances").GetProperty("document")), TestData.Spec.DocumentChecks.Select(Wire.Name).ToList());
        Assert.Equal(Strings(Root.GetProperty("check_instances").GetProperty("line")), TestData.Spec.LineChecks.Select(Wire.Name).ToList());
    }
}
