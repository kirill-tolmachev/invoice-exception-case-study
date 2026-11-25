namespace InvoiceExceptions.Tests;

internal static class TestPaths
{
    /// <summary>Repository root, found by walking up from the test binaries to the solution file.</summary>
    public static string Repository { get; } = FindRepositoryRoot();

    public static string Dataset => Path.Combine(Repository, "dataset");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InvoiceExceptions.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("InvoiceExceptions.slnx was not found above " + AppContext.BaseDirectory);
    }
}
