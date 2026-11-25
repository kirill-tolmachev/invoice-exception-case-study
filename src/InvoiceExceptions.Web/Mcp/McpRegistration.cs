using System.Reflection;
using System.Text.Json;
using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Validation;
using ModelContextProtocol.Server;

namespace InvoiceExceptions.Web.Mcp;

public static class McpRegistration
{
    /// <summary>
    /// Publishes every <see cref="McpServerToolAttribute"/> method of <see cref="CaseTools"/> as an MCP tool over
    /// Streamable HTTP. The server is stateless: each request stands on its own and all state lives in the case store.
    /// </summary>
    public static IServiceCollection AddCaseMcpServer(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<CaseTools>();

        foreach (var method in typeof(CaseTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            services.AddSingleton(provider => Create(method, provider));
        }

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "invoice-exceptions", Version = "1.0.0", Title = "Invoice Exception Agent" };
                options.ServerInstructions = CaseTools.ServerInstructions;
            })
            .WithHttpTransport(options => options.Stateless = true);

        return services;
    }

    private static McpServerTool Create(MethodInfo method, IServiceProvider provider)
    {
        var tool = McpServerTool.Create(method, provider.GetRequiredService<CaseTools>(), new McpServerToolCreateOptions
        {
            Services = provider,
            SerializerOptions = WireJson.Options,
        });

        // The facts argument is described by the published extraction schema itself, not by a paraphrase of it.
        if (tool.ProtocolTool.Name == "submit_document_facts")
        {
            var schema = JsonSerializer.SerializeToNode(tool.ProtocolTool.InputSchema)!.AsObject();
            schema["properties"]!["facts"] = PublishedSchemas.FactsSchema();
            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        }

        return tool;
    }
}
