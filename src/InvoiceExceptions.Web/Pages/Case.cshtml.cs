using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceExceptions.Web.Pages;

/// <summary>
/// The case page: what the engine concluded and why, and the one place where a person confirms an agent's proposal.
/// Reading is GET (optionally an earlier conclusion via <c>?revision=N&amp;document=...</c>); confirming is a POST
/// protected by the default antiforgery check, answered with a redirect back to the page (Post/Redirect/Get).
/// </summary>
public sealed class CaseModel(CaseService cases) : PageModel
{
    public const int MaxNameLength = 100;

    public CaseView View { get; private set; } = null!;

    public IReadOnlyList<DocumentPanel> Panels { get; private set; } = [];

    /// <summary>The earlier revision being viewed, or null for the current state.</summary>
    public int? ViewedRevision { get; private set; }

    /// <summary>Set after a successful confirmation (from <c>?confirmed=</c>, looked up in the stored data).</summary>
    public ProposalView? JustConfirmed { get; private set; }

    /// <summary>Why the last confirmation attempt was refused; the proposal it was about.</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? ErrorProposalId { get; private set; }

    public string? PostedName { get; private set; }

    public IActionResult OnGet(string caseId, int? revision, string? document, string? confirmed)
    {
        if (cases.Get(caseId) is not { } view)
        {
            return NotFound();
        }

        View = view;
        if (revision is { } wanted)
        {
            if (document is not null && view.Document(document) is null)
            {
                return NotFound();
            }

            var panels = new List<DocumentPanel>();
            for (var i = 0; i < view.Documents.Count; i++)
            {
                var doc = view.Documents[i];
                var shown = document is null || doc.DocumentId == document
                    ? doc.History.FirstOrDefault(e => e.InputRevision == wanted)
                    : null;
                if (shown is not null)
                {
                    panels.Add(new DocumentPanel(view, doc, shown, i));
                }
            }

            if (panels.Count == 0)
            {
                return NotFound();
            }

            Panels = panels;
            ViewedRevision = panels.All(p => p.IsCurrent) ? null : wanted;
        }
        else
        {
            Panels = [.. view.Documents.Select((doc, i) => new DocumentPanel(view, doc, doc.Current, i))];
        }

        if (confirmed is not null)
        {
            JustConfirmed = view.Documents
                .SelectMany(d => d.Proposals)
                .FirstOrDefault(p => p.ProposalId == confirmed && p.Confirmation is not null);
        }

        ViewData["Title"] = view.CaseId;
        return Page();
    }

    public IActionResult OnPostConfirm(string caseId, string? proposalId, string? confirmedBy)
    {
        if (cases.Get(caseId) is null)
        {
            return NotFound();
        }

        var name = confirmedBy?.Trim() ?? "";
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            errors.Add("No proposal was named. Use the button next to the proposal you want to confirm.");
        }

        if (name.Length == 0)
        {
            errors.Add("Enter your name: the confirmation is recorded as your decision.");
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add($"The name is too long (at most {MaxNameLength} characters).");
        }

        if (errors.Count == 0)
        {
            try
            {
                cases.ConfirmProposal(caseId, proposalId!, name);
                return RedirectToPage(null, null, new { caseId, confirmed = proposalId }, "proposals");
            }
            catch (InputValidationException e)
            {
                errors.AddRange(e.Issues.Select(i => i.Message));
            }
            catch (CaseNotFoundException)
            {
                return NotFound();
            }
        }

        // Nothing was stored. Show the page again, as it is now, with the reasons next to the form.
        var result = OnGet(caseId, revision: null, document: null, confirmed: null);
        Errors = errors;
        ErrorProposalId = proposalId;
        PostedName = confirmedBy;
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return result;
    }
}
