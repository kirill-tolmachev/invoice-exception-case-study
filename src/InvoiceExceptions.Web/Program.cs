using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Catalog;
using InvoiceExceptions.Application.Documents;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Infrastructure.Catalog;
using InvoiceExceptions.Infrastructure.Documents;
using InvoiceExceptions.Infrastructure.Storage;
using InvoiceExceptions.Web;
using InvoiceExceptions.Web.Api;
using InvoiceExceptions.Web.Mcp;

// One process, two doors onto the same use cases:
//   /cases/{id}  - the case page, for the person who reviews and confirms;
//   /mcp         - the MCP endpoint (Streamable HTTP), for the agent.
// Plus /api/cases/..., read-only JSON of the stored facts and conclusions.

var builder = WebApplication.CreateBuilder(args);

var settings = AppSettings.From(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICaseStore>(_ => SqliteCaseStore.InDirectory(settings.DataDirectory));
builder.Services.AddSingleton<ICaseCatalog>(_ => new DatasetCaseCatalog(settings.DatasetDirectory));
builder.Services.AddSingleton<IDocumentTextSource, PdfTextSource>();
builder.Services.AddSingleton<CaseService>();

builder.Services.AddRazorPages();
builder.Services.AddCaseMcpServer();

var app = builder.Build();

app.Logger.LogInformation("Data directory: {DataDirectory}", settings.DataDirectory);
app.Logger.LogInformation("Dataset directory: {DatasetDirectory}", settings.DatasetDirectory);

app.UseStaticFiles();
app.MapRazorPages();
app.MapReadApi();
app.MapMcp("/mcp");

app.Run();

public partial class Program;
