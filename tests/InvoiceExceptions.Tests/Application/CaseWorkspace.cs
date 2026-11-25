using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Runner;
using InvoiceExceptions.Infrastructure.Storage;

namespace InvoiceExceptions.Tests.Application;

/// <summary>
/// A real <see cref="SqliteCaseStore"/> in its own temporary directory, with the case service and the runner adapter
/// on top of it. <see cref="Reopen"/> gives brand-new instances over the same directory: a process restart.
/// </summary>
internal sealed class CaseWorkspace : IDisposable
{
    public CaseWorkspace()
    {
        Directory = Path.Combine(Path.GetTempPath(), "invoice-exceptions-app-tests", Guid.NewGuid().ToString("N"));
        Store = SqliteCaseStore.InDirectory(Directory);
        Cases = new CaseService(Store, TimeProvider.System);
        Runner = new StepRunner(Cases);
    }

    private CaseWorkspace(string directory)
    {
        Directory = directory;
        Store = SqliteCaseStore.InDirectory(directory);
        Cases = new CaseService(Store, TimeProvider.System);
        Runner = new StepRunner(Cases);
        _ownsDirectory = false;
    }

    private readonly bool _ownsDirectory = true;

    public string Directory { get; }

    public SqliteCaseStore Store { get; }

    public CaseService Cases { get; }

    public StepRunner Runner { get; }

    /// <summary>New store, service and adapter instances over the same data directory.</summary>
    public CaseWorkspace Reopen() => new(Directory);

    /// <summary>Runs one step through the adapter and parses its output.</summary>
    public (int ExitCode, JsonElement Output) Run(string json)
    {
        var result = Runner.Run(json);
        return (result.ExitCode, Dataset.Parse(result.OutputJson));
    }

    public (int ExitCode, JsonElement Output) Run(JsonNode step) => Run(step.ToJsonString());

    /// <summary>Runs a dataset step that must succeed.</summary>
    public JsonElement RunOk(string caseId, int step)
    {
        var (exit, output) = Run(Dataset.StepText(caseId, step));
        Assert.True(exit == StepRunner.ExitResult, $"{caseId} step {step} was rejected: {output}");
        return output;
    }

    public JsonElement RunOk(JsonNode step)
    {
        var (exit, output) = Run(step);
        Assert.True(exit == StepRunner.ExitResult, $"step was rejected: {output}");
        return output;
    }

    /// <summary>Everything the store holds for the case, serialized; null when there is no such case.</summary>
    public string? Snapshot(string caseId) =>
        Store.Load(caseId) is { } record ? JsonSerializer.Serialize(record) : null;

    public void Dispose()
    {
        if (_ownsDirectory && System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
