using System.Text.Json;
using InvoiceExceptions.Application.Runner;

namespace InvoiceExceptions.Tests.Application;

/// <summary>
/// The acceptance run of tools/run_evals.py, in-process: every dataset case replayed through the adapter on its own
/// fresh store and compared with expected/ by the runner's own rules.
/// </summary>
public sealed class DatasetAcceptanceTests
{
    public static TheoryData<string> Cases => Dataset.CaseIdData();

    [Fact]
    public void The_dataset_has_cases_to_replay()
    {
        Assert.NotEmpty(Dataset.CaseIds());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_step_matches_its_expected_output(string caseId)
    {
        using var workspace = new CaseWorkspace();
        var lastRevision = int.MinValue;
        var failures = new List<string>();

        var steps = Dataset.StepFiles(caseId);
        Assert.NotEmpty(steps);
        for (var step = 1; step <= steps.Count; step++)
        {
            var expected = Dataset.Expected(caseId, step);
            var (exit, output) = workspace.Run(Dataset.StepText(caseId, step));

            var expectsError = expected.TryGetProperty("error", out _);
            var expectedExit = expectsError ? StepRunner.ExitValidationError : StepRunner.ExitResult;
            if (exit != expectedExit)
            {
                failures.Add($"step {step}: exit code {exit}, expected {expectedExit}: {output}");
                continue;
            }

            failures.AddRange(OutputSchema.Problems(output).Select(p => $"step {step}: schema: {p}"));
            failures.AddRange(StepComparer.Compare(expected, output).Select(d => $"step {step}: {d}"));

            if (!expectsError)
            {
                var revision = output.GetProperty("input_revision").GetInt32();
                if (revision <= lastRevision)
                {
                    failures.Add($"step {step}: input_revision must increase: previous {lastRevision}, got {revision}");
                }

                lastRevision = revision;
            }
        }

        Assert.True(failures.Count == 0, $"{caseId}:\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Replaying_the_same_steps_on_two_fresh_stores_gives_byte_identical_outputs(string caseId)
    {
        using var first = new CaseWorkspace();
        using var second = new CaseWorkspace();

        for (var step = 1; step <= Dataset.StepFiles(caseId).Count; step++)
        {
            var input = Dataset.StepText(caseId, step);
            var a = first.Runner.Run(input);
            var b = second.Runner.Run(input);

            Assert.Equal(a.ExitCode, b.ExitCode);
            Assert.Equal(a.OutputJson, b.OutputJson);
        }
    }

    [Fact]
    public void The_comparer_rejects_a_changed_decision_and_accepts_decimals_of_another_scale()
    {
        // Guards the port itself: a comparer that never reports anything would make the acceptance theory meaningless.
        var expected = Dataset.Expected("case-002", 1);
        var node = System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())!;

        node["lines"]![0]!["received"] = node["lines"]![0]!["received"]!.GetValue<string>() + ".000";
        Assert.Empty(StepComparer.Compare(expected, JsonSerializer.SerializeToElement(node)));

        node["decision"] = "blocked";
        Assert.Contains(StepComparer.Compare(expected, JsonSerializer.SerializeToElement(node)), d => d.StartsWith("decision", StringComparison.Ordinal));
    }

    [Fact]
    public void The_output_schema_accepts_an_expected_output_and_rejects_a_broken_one()
    {
        var expected = Dataset.Expected("case-001", 1);
        Assert.Empty(OutputSchema.Problems(expected));

        var node = System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())!;
        node["decision"] = "approved";
        node["lines"]![0]!["calculated_net"] = 1050.0;
        Assert.NotEmpty(OutputSchema.Problems(JsonSerializer.SerializeToElement(node)));
    }
}
