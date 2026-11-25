using System.Text;
using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Runner;
using InvoiceExceptions.Infrastructure.Storage;

// Adapter for tools/run_evals.py (contract: docs/EVALUATION.md):
//
//   <this program> --state-dir <dir>  < step-input.json  > step-output.json
//
// stdin is one JSON step, stdout is one JSON result, logs go to stderr.
// Exit code 0 = result, 2 = validation error, anything else = failure.

const int exitFailure = 1;

try
{
    var stateDir = StateDirectory(args);
    if (stateDir is null)
    {
        Console.Error.WriteLine("usage: InvoiceExceptions.Cli --state-dir <directory>  (step input on stdin)");
        return exitFailure;
    }

    using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var input = stdin.ReadToEnd();

    var cases = new CaseService(SqliteCaseStore.InDirectory(stateDir), TimeProvider.System);
    var result = new StepRunner(cases).Run(input);

    using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
    stdout.Write(result.OutputJson);
    stdout.Write('\n');
    return result.ExitCode;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return exitFailure;
}

static string? StateDirectory(string[] args)
{
    var index = Array.IndexOf(args, "--state-dir");
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
