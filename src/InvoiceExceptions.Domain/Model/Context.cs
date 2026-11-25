namespace InvoiceExceptions.Domain.Model;

/// <summary>Reference data the invoice is reconciled against. A snapshot is immutable and versioned by the sender.</summary>
public sealed record ContextSnapshot(
    string ContextVersion,
    IReadOnlyList<PurchaseOrder> Orders,
    GoodsReceipts GoodsReceipts,
    IReadOnlyList<Supplier> Suppliers,
    IReadOnlyList<Buyer> Buyers,
    IReadOnlyList<HistoryDocument> History);

public enum OrderStatus { Open, Closed }

public sealed record PurchaseOrder(
    string OrderId,
    string Reference,
    OrderStatus Status,
    string SupplierId,
    string BuyerId,
    string Currency,
    IReadOnlyList<OrderLine> Lines);

public sealed record OrderLine(
    string LineId,
    string? Sku,
    string Description,
    IReadOnlyList<string> ExplicitAliases,
    decimal Quantity,
    string Unit,
    decimal UnitNetPrice,
    decimal TaxRate);

/// <summary><paramref name="Available"/> = false means "we do not know", which is different from an empty list of receipts.</summary>
public sealed record GoodsReceipts(bool Available, IReadOnlyList<GoodsReceipt> Receipts);

public sealed record GoodsReceipt(
    string ReceiptId,
    string OrderId,
    string OrderLineId,
    decimal AcceptedQuantity,
    bool Accepted);

public sealed record Supplier(
    string SupplierId,
    string LegalName,
    string RegistrationId,
    IReadOnlyList<string> ApprovedIbans);

public sealed record Buyer(
    string BuyerId,
    string LegalName,
    string RegistrationId,
    IReadOnlyList<string> ExplicitAliases);

/// <summary>An invoice that accounting has already counted. Feeds R3 (quantities) and R6 (duplicates).</summary>
public sealed record HistoryDocument(
    string DocumentId,
    string SupplierId,
    string BuyerId,
    string DocumentNumber,
    bool Counted,
    string MatchedOrderId,
    IReadOnlyList<HistoryLine> Lines,
    ComparableFacts Facts);

public sealed record HistoryLine(string OrderLineId, decimal Quantity);
