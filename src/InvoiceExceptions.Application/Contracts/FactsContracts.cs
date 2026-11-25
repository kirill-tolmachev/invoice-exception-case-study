namespace InvoiceExceptions.Application.Contracts;

// Extracted invoice facts exactly as dataset/schemas/extraction.schema.json defines them:
// declared values as printed, null (plus a missing_fields entry) for what the document does not say,
// and for every fact the page and the verbatim excerpt it was read from.

public sealed record FactsDto(
    string SchemaVersion,
    string DocumentId,
    string Type,
    string Number,
    string IssueDate,
    string? DueDate,
    string? Currency,
    string? PurchaseOrderReference,
    string? PaymentTerms,
    PartyDto Supplier,
    PartyDto Buyer,
    BankDto Bank,
    IReadOnlyList<FactLineDto> Lines,
    string DeclaredSubtotal,
    string DeclaredTaxTotal,
    string DeclaredTotal,
    IReadOnlyList<MissingFieldDto> MissingFields,
    IReadOnlyList<EvidenceDto> Evidence);

public sealed record PartyDto(string? LegalName, string? RegistrationId, string? Country);

public sealed record BankDto(string? Iban, string? Bic);

public sealed record FactLineDto(
    string LineId,
    string Description,
    string? Sku,
    string Quantity,
    string Unit,
    string UnitNetPrice,
    string DeclaredNetAmount,
    string TaxRate,
    string? DeclaredTaxAmount);

public sealed record MissingFieldDto(string FieldPath, string Reason);

/// <summary>Where a fact comes from: JSON Pointer of the fact, the document, the page and the text as printed.</summary>
public sealed record EvidenceDto(string FieldPath, string DocumentId, int Page, string Excerpt);
