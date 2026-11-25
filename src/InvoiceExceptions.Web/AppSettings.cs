namespace InvoiceExceptions.Web;

/// <summary>
/// Where the application keeps its data and where it finds cases. Both can be set in configuration
/// (<c>InvoiceExceptions:DataDirectory</c>, <c>InvoiceExceptions:DatasetDirectory</c>, also as environment
/// variables or command-line arguments); by default they are the <c>data</c> and <c>dataset</c> folders of the repository.
/// </summary>
public sealed record AppSettings(string DataDirectory, string DatasetDirectory)
{
    public static AppSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("InvoiceExceptions");
        var dataset = section["DatasetDirectory"] is { Length: > 0 } configuredDataset
            ? Path.GetFullPath(configuredDataset)
            : FindUpwards("dataset") ?? Path.GetFullPath("dataset");
        var data = section["DataDirectory"] is { Length: > 0 } configuredData
            ? Path.GetFullPath(configuredData)
            : Path.Combine(Path.GetDirectoryName(dataset)!, "data");
        return new AppSettings(data, dataset);
    }

    /// <summary>
    /// <c>dotnet run --project src/InvoiceExceptions.Web</c> may start in the repository root or in the project folder
    /// depending on how it is launched, so the dataset is looked for from the working directory upwards.
    /// </summary>
    private static string? FindUpwards(string folder)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, folder);
                if (Directory.Exists(Path.Combine(candidate, "cases")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
