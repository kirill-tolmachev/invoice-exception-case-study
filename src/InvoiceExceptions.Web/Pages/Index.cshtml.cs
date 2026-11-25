using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Catalog;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceExceptions.Web.Pages;

/// <summary>The list of cases: those opened in the application with their current outcome, and those the dataset still offers.</summary>
public sealed class IndexModel(CaseService cases, ICaseCatalog catalog) : PageModel
{
    public IReadOnlyList<CaseView> Opened { get; private set; } = [];

    public IReadOnlyList<string> NotOpened { get; private set; } = [];

    public void OnGet()
    {
        Opened = [.. cases.ListCaseIds()
            .Order(StringComparer.Ordinal)
            .Select(cases.Get)
            .OfType<CaseView>()];
        var opened = Opened.Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);
        NotOpened = [.. catalog.ListCaseIds().Where(id => !opened.Contains(id)).Order(StringComparer.Ordinal)];
        ViewData["Title"] = "Cases";
    }
}
