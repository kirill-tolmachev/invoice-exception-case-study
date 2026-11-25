namespace InvoiceExceptions.Application.Contracts;

// The context snapshot exactly as dataset/schemas/context.schema.json defines it.
// Decimals stay strings here: they are parsed once, on the way into the domain model.

public sealed record ContextDto(
    string ContextVersion,
    PurchaseOrdersDto PurchaseOrders,
    GoodsReceiptsDto GoodsReceipts,
    SuppliersDto Suppliers,
    BuyersDto Buyers,
    InvoiceHistoryDto InvoiceHistory);

public sealed record PurchaseOrdersDto(string SchemaVersion, IReadOnlyList<OrderDto> Orders);

public sealed record OrderDto(
    string OrderId,
    string Reference,
    string Status,
    string SupplierId,
    string BuyerId,
    string Currency,
    IReadOnlyList<OrderLineDto> Lines);

public sealed record OrderLineDto(
    string LineId,
    string? Sku,
    string Description,
    IReadOnlyList<string> ExplicitAliases,
    string Quantity,
    string Unit,
    string UnitNetPrice,
    string TaxRate);

public sealed record GoodsReceiptsDto(string SchemaVersion, string Availability, IReadOnlyList<ReceiptDto> Receipts);

public sealed record ReceiptDto(
    string ReceiptId,
    string OrderId,
    string OrderLineId,
    string AcceptedQuantity,
    string Unit,
    string AcceptedAt,
    string Status);

public sealed record SuppliersDto(string SchemaVersion, IReadOnlyList<SupplierDto> Suppliers);

public sealed record SupplierDto(
    string SupplierId,
    string LegalName,
    string Country,
    string RegistrationId,
    IReadOnlyList<ApprovedBankAccountDto> ApprovedBankAccounts);

public sealed record ApprovedBankAccountDto(string Iban, string? Bic);

public sealed record BuyersDto(string SchemaVersion, IReadOnlyList<BuyerDto> Buyers);

public sealed record BuyerDto(
    string BuyerId,
    string LegalName,
    string Country,
    string RegistrationId,
    IReadOnlyList<string> ExplicitAliases);

public sealed record InvoiceHistoryDto(string SchemaVersion, IReadOnlyList<HistoryDocumentDto> Documents);

public sealed record HistoryDocumentDto(
    string DocumentId,
    string SupplierId,
    string BuyerId,
    string DocumentType,
    string DocumentNumber,
    string Currency,
    string Status,
    string MatchedOrderId,
    string ContentSha256,
    IReadOnlyList<HistoryLineDto> Lines,
    NormalizedFactsDto NormalizedFacts);

public sealed record HistoryLineDto(string DocumentLineId, string OrderLineId, string Quantity);

public sealed record NormalizedFactsDto(
    string IssueDate,
    string? DueDate,
    string? Currency,
    string? PurchaseOrderReference,
    string? Iban,
    string DeclaredSubtotal,
    string DeclaredTaxTotal,
    string DeclaredTotal,
    IReadOnlyList<NormalizedLineDto> Lines);

public sealed record NormalizedLineDto(
    string? Sku,
    string Quantity,
    string Unit,
    string UnitNetPrice,
    string TaxRate,
    string DeclaredNetAmount,
    string? DeclaredTaxAmount);
