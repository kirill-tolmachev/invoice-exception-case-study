using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Validation;
using Xunit.Abstractions;

namespace InvoiceExceptions.Tests.Validation;

public sealed class SchemaValidatorTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> StepFiles() =>
        Directory.EnumerateFiles(Path.Combine(TestPaths.Dataset, "cases"), "step-*.json", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}steps{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetRelativePath(TestPaths.Dataset, path).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(StepFiles))]
    public void Every_published_step_input_is_valid(string relativePath)
    {
        var issues = SchemaValidator.Shared.ValidateStepInput(Load(relativePath).Deserialize<JsonElement>());

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("/document/facts/lines/0/quantity", "\"0\"", "a quantity of zero")]
    [InlineData("/document/facts/lines/0/quantity", "\"-5\"", "a negative quantity")]
    [InlineData("/document/facts/lines/0/quantity", "\"1.2345\"", "a quantity with four decimals")]
    [InlineData("/document/facts/lines/0/unit_net_price", "\"-1.00\"", "a negative price")]
    [InlineData("/document/facts/lines/0/unit_net_price", "\"12,50\"", "a decimal comma")]
    [InlineData("/document/facts/lines/0/unit_net_price", "12.5", "a JSON number instead of a decimal string")]
    [InlineData("/document/facts/declared_total", "\"1e3\"", "exponent notation")]
    [InlineData("/document/facts/lines/0/tax_rate", "\"20\"", "a tax rate given as a percentage")]
    [InlineData("/document/facts/due_date", "\"25.11.2026\"", "a date in the wrong format")]
    [InlineData("/document/facts/currency", "\"eur\"", "a lower-case currency")]
    [InlineData("/document/content_sha256", "\"abc\"", "a malformed hash")]
    [InlineData("/context/goods_receipts/availability", "\"maybe\"", "an unknown availability")]
    [InlineData("/policy_ref", "\"core-v2\"", "an unknown policy")]
    public void A_malformed_value_is_reported_at_its_own_path(string pointer, string replacementJson, string because)
    {
        var input = Load("cases/case-001/steps/step-001.json");
        Replace(input, pointer, JsonNode.Parse(replacementJson));

        var issues = SchemaValidator.Shared.ValidateStepInput(input.Deserialize<JsonElement>());

        foreach (var issue in issues)
        {
            output.WriteLine($"{issue.Path}: {issue.Message}");
        }

        Assert.True(issues.Any(i => i.Path == pointer), $"Expected an issue at {pointer} for {because}.");
    }

    [Fact]
    public void A_missing_required_field_and_an_unexpected_one_are_both_reported()
    {
        var input = Load("cases/case-001/steps/step-001.json");
        var facts = input["document"]!["facts"]!.AsObject();
        facts.Remove("number");
        facts["approved_by"] = "the supplier";

        var issues = SchemaValidator.Shared.ValidateStepInput(input.Deserialize<JsonElement>());

        foreach (var issue in issues)
        {
            output.WriteLine($"{issue.Path}: {issue.Message}");
        }

        Assert.Contains(issues, i => i.Path.StartsWith("/document/facts", StringComparison.Ordinal) && i.Message.Contains("number", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Path.StartsWith("/document/facts", StringComparison.Ordinal) && i.Message.Contains("approved_by", StringComparison.Ordinal) || i.Path == "/document/facts/approved_by");
    }

    [Fact]
    public void A_step_that_changes_nothing_is_rejected()
    {
        var input = JsonNode.Parse("""{"schema_version":"1.0","case_id":"case-001","step":1,"as_of":"2026-11-02","policy_ref":"core-v1"}""")!;

        var issues = SchemaValidator.Shared.ValidateStepInput(input.Deserialize<JsonElement>());

        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Receipts_listed_while_availability_is_unavailable_are_rejected()
    {
        var input = Load("cases/case-001/steps/step-001.json");
        input["context"]!["goods_receipts"]!["availability"] = "unavailable";

        var issues = SchemaValidator.Shared.ValidateStepInput(input.Deserialize<JsonElement>());

        Assert.Contains(issues, i => i.Path.StartsWith("/context/goods_receipts", StringComparison.Ordinal));
    }

    private static JsonNode Load(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Dataset, relativePath)))!;

    private static void Replace(JsonNode root, string pointer, JsonNode? value)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var node = root;
        foreach (var segment in segments[..^1])
        {
            node = node is JsonArray array ? array[int.Parse(segment)]! : node![segment]!;
        }

        if (node is JsonArray parentArray)
        {
            parentArray[int.Parse(segments[^1])] = value;
        }
        else
        {
            node![segments[^1]] = value;
        }
    }
}
