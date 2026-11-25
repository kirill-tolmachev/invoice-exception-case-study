using System.Text.Json;

namespace InvoiceExceptions.Application.Catalog;

/// <summary>
/// Where cases come from when the agent opens one: the documents to read and the context to reconcile them against.
/// In this exercise that is the dataset folder; in production it would be the mail intake and the ERP.
/// A catalog hands out inputs only - never extracted facts or expected results.
/// </summary>
public interface ICaseCatalog
{
    IReadOnlyList<string> ListCaseIds();

    CatalogCase? Find(string caseId);
}

/// <param name="Context">A context snapshot in the shape of context.schema.json, including its context_version.</param>
public sealed record CatalogCase(string CaseId, string AsOf, JsonElement Context, IReadOnlyList<CatalogDocument> Documents);

/// <param name="Path">Absolute path of the PDF as the agent can read it.</param>
/// <param name="ContentSha256">SHA-256 of the file as it is on disk now; null if the file is missing.</param>
public sealed record CatalogDocument(string DocumentId, string Path, string? ContentSha256);
