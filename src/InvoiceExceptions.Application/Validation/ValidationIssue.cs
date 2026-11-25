namespace InvoiceExceptions.Application.Validation;

/// <summary>One reason an input was rejected. <paramref name="Path"/> is a JSON Pointer into the submitted input.</summary>
public sealed record ValidationIssue(string Path, string Message)
{
    public ValidationIssue Under(string prefix) => this with { Path = prefix + Path };
}

/// <summary>
/// The input was rejected as a whole. Nothing was stored: no revision, no facts, no conclusion.
/// </summary>
public sealed class InputValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception("The input is invalid: " + string.Join("; ", issues.Select(i => $"{(i.Path.Length == 0 ? "/" : i.Path)}: {i.Message}")))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;

    public static InputValidationException Single(string path, string message) => new([new ValidationIssue(path, message)]);
}

/// <summary>The request names a case that does not exist.</summary>
public sealed class CaseNotFoundException(string caseId) : Exception($"There is no case '{caseId}'.")
{
    public string CaseId { get; } = caseId;
}
