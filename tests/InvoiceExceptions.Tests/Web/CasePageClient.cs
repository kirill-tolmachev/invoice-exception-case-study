using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using InvoiceExceptions.Application.Cases;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceExceptions.Tests.Web;

/// <summary>Drives the case pages over HTTP the way a browser would: cookies kept, redirects not followed.</summary>
internal sealed class CasePageClient(AppFixture app)
{
    private readonly HttpClient _http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public CaseService Cases => app.Services.GetRequiredService<CaseService>();

    public HttpClient Http => _http;

    public static string StepPath(string caseId, int step) =>
        Path.Combine(TestPaths.Dataset, "cases", caseId, "steps", $"step-{step:000}.json");

    /// <summary>Replays dataset steps through the use case, as the acceptance runner does.</summary>
    public void Replay(string caseId, params int[] steps)
    {
        foreach (var step in steps)
        {
            Cases.Apply(JsonDocument.Parse(File.ReadAllText(StepPath(caseId, step))).RootElement, Actor.Runner);
        }
    }

    public async Task<Page> GetAsync(string url)
    {
        var response = await _http.GetAsync(url);
        return await Page.From(response);
    }

    /// <summary>Submits the confirmation form of a proposal as it is rendered on <paramref name="page"/>.</summary>
    public async Task<HttpResponseMessage> SubmitConfirmationAsync(Page page, string proposalId, string name, bool withToken = true)
    {
        var form = page.ConfirmationForm(proposalId) ?? throw new InvalidOperationException($"No confirmation form for {proposalId}.");
        var fields = form.QuerySelectorAll("input[type=hidden]")
            .Where(i => withToken || i.GetAttribute("name") != "__RequestVerificationToken")
            .Select(i => new KeyValuePair<string, string>(i.GetAttribute("name")!, i.GetAttribute("value") ?? ""))
            .Append(new KeyValuePair<string, string>("confirmedBy", name));
        return await _http.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(fields));
    }
}

internal sealed record Page(HttpStatusCode Status, string Body, IDocument Html)
{
    public static async Task<Page> From(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return new Page(response.StatusCode, body, new HtmlParser().ParseDocument(body));
    }

    public string Text => Html.Body?.TextContent ?? "";

    public IElement? ConfirmationForm(string proposalId) =>
        Html.QuerySelector($"article[data-proposal='{proposalId}'] form.confirm");

    public IElement? Check(string code, string objectId) =>
        Html.QuerySelector($"tr[data-check='{code}'][data-object='{objectId}']");

    public string LineField(string lineId, string field) =>
        Html.QuerySelector($"tr[data-line='{lineId}'] td[data-field='{field}']")?.TextContent.Trim()
        ?? throw new InvalidOperationException($"No {field} cell for line {lineId}.");

    public string Outcome => Html.QuerySelector(".outcome .outcome-title")?.TextContent ?? "";
}
