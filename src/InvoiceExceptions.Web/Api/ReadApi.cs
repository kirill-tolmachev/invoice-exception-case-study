using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Contracts;

namespace InvoiceExceptions.Web.Api;

/// <summary>
/// Read-only JSON views of what is stored, for scripts and reviewers: the facts exactly as registered and the
/// conclusions exactly as stored. There is deliberately nothing here that writes.
/// </summary>
public static class ReadApi
{
    public static IEndpointRouteBuilder MapReadApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/cases/{caseId}/documents/{documentId}");

        api.MapGet("/facts", (string caseId, string documentId, CaseService cases) =>
            Find(cases, caseId, documentId) is { } document ? Json(document.Facts) : Results.NotFound());

        api.MapGet("/result", (string caseId, string documentId, int? revision, CaseService cases) =>
        {
            if (Find(cases, caseId, documentId) is not { } document)
            {
                return Results.NotFound();
            }

            var evaluation = revision is null ? document.Current : document.History.FirstOrDefault(e => e.InputRevision == revision);
            return evaluation is null
                ? Results.NotFound()
                : Json(new { case_id = caseId, assessment_id = evaluation.AssessmentId, result = evaluation.Result });
        });

        return app;
    }

    private static DocumentView? Find(CaseService cases, string caseId, string documentId) =>
        cases.Get(caseId)?.Document(documentId);

    private static IResult Json(object value) => Results.Json(value, WireJson.Indented);
}
