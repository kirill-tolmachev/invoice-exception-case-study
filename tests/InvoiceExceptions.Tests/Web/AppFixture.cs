using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InvoiceExceptions.Tests.Web;

/// <summary>
/// The real application in-process, with its own empty data directory and the repository's dataset.
/// Creating a second instance on the same data directory stands in for a restart of the process.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>
{
    private readonly bool _ownsDataDirectory;

    public AppFixture()
        : this(Path.Combine(Path.GetTempPath(), "invoice-exceptions-tests", Guid.NewGuid().ToString("N")), ownsDataDirectory: true)
    {
    }

    private AppFixture(string dataDirectory, bool ownsDataDirectory)
    {
        DataDirectory = dataDirectory;
        _ownsDataDirectory = ownsDataDirectory;
    }

    public string DataDirectory { get; }

    /// <summary>A fresh host over the same stored data: what the application looks like after a restart.</summary>
    public AppFixture Restarted() => new(DataDirectory, ownsDataDirectory: false);

    public async Task<McpClient> ConnectAgentAsync()
    {
        var http = CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), Name = "test-agent" },
            http,
            loggerFactory: null,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("InvoiceExceptions:DataDirectory", DataDirectory);
        builder.UseSetting("InvoiceExceptions:DatasetDirectory", TestPaths.Dataset);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDataDirectory && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}

internal static class McpClientExtensions
{
    /// <summary>Calls a tool and returns its JSON reply together with the error flag.</summary>
    public static async Task<(JsonElement Json, bool IsError)> CallAsync(
        this McpClient agent, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await agent.CallToolAsync(tool, arguments);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (JsonDocument.Parse(text).RootElement.Clone(), result.IsError == true);
    }
}
