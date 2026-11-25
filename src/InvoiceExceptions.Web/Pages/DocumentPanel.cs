using System.Text.Json;
using InvoiceExceptions.Application.Cases;
using InvoiceExceptions.Application.Contracts;

namespace InvoiceExceptions.Web.Pages;

/// <summary>
/// Everything the case page shows for one document at one conclusion (the current one, or an earlier one from the
/// history), with the joins already done: document lines to line results to order lines, checks to their sources.
/// </summary>
public sealed class DocumentPanel
{
    private readonly JsonElement _factsJson;
    private readonly Dictionary<string, OrderLineDto> _orderLines;

    public DocumentPanel(CaseView view, DocumentView document, EvaluationView shown, int position)
    {
        Position = position;
        Case = view;
        Document = document;
        Shown = shown;
        _factsJson = JsonSerializer.SerializeToElement(document.Facts, WireJson.Options);

        var orders = view.Context?.PurchaseOrders.Orders ?? [];
        _orderLines = orders
            .SelectMany(o => o.Lines)
            .GroupBy(l => l.LineId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        MatchedOrder = Result.MatchedOrderId is { } orderId ? orders.FirstOrDefault(o => o.OrderId == orderId) : null;

        Lines = [.. Facts.Lines.Select((line, index) =>
        {
            var result = Result.Lines.FirstOrDefault(r => r.DocumentLineId == line.LineId);
            return new LineRow(index, line, result, OrderLine(result?.OrderLineId));
        })];

        CheckGroups = BuildCheckGroups();
        Proposals = [.. document.Proposals.Select(BuildProposal)];
    }

    public CaseView Case { get; }

    /// <summary>Index of the document within the case, used for anchors: document ids come from the document.</summary>
    public int Position { get; }

    public DocumentView Document { get; }

    public EvaluationView Shown { get; }

    public ResultDto Result => Shown.Result;

    public FactsDto Facts => Document.Facts;

    public bool IsCurrent => Shown.InputRevision == Document.Current.InputRevision;

    public bool IsDuplicate => Result.ImportDisposition == "duplicate";

    public bool IsConflict => Result.ImportDisposition == "conflict";

    /// <summary>The facts were corrected after the conclusion shown: the facts on the page are newer than that conclusion.</summary>
    public bool FactsNewerThanShown => Document.FactsRevision > Shown.InputRevision;

    /// <summary>The context on the page (the latest) is not the one the conclusion shown was drawn against.</summary>
    public bool ContextDiffersFromShown => Case.ContextVersion is { } latest && latest != Result.ContextVersion;

    public OrderDto? MatchedOrder { get; }

    public IReadOnlyList<LineRow> Lines { get; }

    public IReadOnlyList<CheckGroup> CheckGroups { get; }

    public IReadOnlyList<ProposalRow> Proposals { get; }

    public IEnumerable<CheckDto> FailingChecks => Result.Checks.Where(c => c.Status == "fail");

    public IEnumerable<CheckDto> UnknownChecks => Result.Checks.Where(c => c.Status == "unknown");

    public int AwaitingConfirmation => Document.Proposals.Count(p => p.Confirmation is null);

    /// <summary>An id that is safe and unique to use in anchors.</summary>
    public string Anchor(string part) => $"doc{Position}-{part}";

    public OrderLineDto? OrderLine(string? orderLineId) =>
        orderLineId is not null && _orderLines.TryGetValue(orderLineId, out var line) ? line : null;

    public FactLineDto? DocumentLine(string documentLineId) => Facts.Lines.FirstOrDefault(l => l.LineId == documentLineId);

    public string ObjectLabel(string objectId) =>
        objectId == Document.DocumentId ? "Document " + objectId
        : DocumentLine(objectId) is { } line ? $"Line {objectId}: {line.Description}"
        : objectId;

    /// <summary>The extracted value a JSON Pointer into the facts points at, as text.</summary>
    public string? FactValue(string pointer)
    {
        var current = _factsJson;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var child))
            {
                current = child;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => current.GetRawText(),
        };
    }

    /// <summary>
    /// The evidence behind a pointer: entries recorded for exactly that field, or, for a pointer to an object such
    /// as <c>/supplier</c>, for the fields inside it.
    /// </summary>
    public IReadOnlyList<EvidenceDto> EvidenceFor(string pointer)
    {
        var exact = Facts.Evidence.Where(e => e.FieldPath == pointer).ToList();
        return exact.Count > 0
            ? exact
            : [.. Facts.Evidence.Where(e => e.FieldPath.StartsWith(pointer + "/", StringComparison.Ordinal))];
    }

    public MissingFieldDto? Missing(string pointer) => Facts.MissingFields.FirstOrDefault(m => m.FieldPath == pointer);

    private List<CheckGroup> BuildCheckGroups()
    {
        var groups = new List<CheckGroup>();
        var documentChecks = Result.Checks.Where(c => c.ObjectId == Document.DocumentId).ToList();
        if (documentChecks.Count > 0)
        {
            groups.Add(new CheckGroup("Document " + Document.DocumentId, Anchor("checks-doc"), [.. documentChecks.Select(Row)]));
        }

        var lineIds = Facts.Lines.Select(l => l.LineId).ToList();
        foreach (var byObject in Result.Checks
                     .Where(c => c.ObjectId != Document.DocumentId)
                     .GroupBy(c => c.ObjectId)
                     .OrderBy(g => lineIds.IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i))
        {
            var index = lineIds.IndexOf(byObject.Key);
            groups.Add(new CheckGroup(ObjectLabel(byObject.Key), Anchor("checks-" + (index < 0 ? "other" : "l" + index)), [.. byObject.Select(Row)]));
        }

        return groups;

        CheckRow Row(CheckDto check) => new(
            check,
            [.. check.EvidenceRefs.Select(pointer => new SourceRef(pointer, FactValue(pointer), EvidenceFor(pointer), Missing(pointer)))]);
    }

    private ProposalRow BuildProposal(ProposalView proposal)
    {
        var order = Case.Context?.PurchaseOrders.Orders.FirstOrDefault(o => o.OrderId == proposal.OrderId);
        var matches = proposal.LineMatches
            .Select(m => new ProposalLineRow(m, DocumentLine(m.DocumentLineId), OrderLine(m.OrderLineId)))
            .ToList();

        // The conclusion shown already uses this proposal if its order is the matched one and a match source says
        // so - that is how the reviewer sees the consequences before confirming.
        var appliedAs = proposal.Confirmation is null ? "agent_proposal" : "human_confirmation";
        var applied = Result.MatchedOrderId == proposal.OrderId
                      && (Result.OrderMatchSource == appliedAs
                          || Result.Lines.Any(l => l.MatchSource == appliedAs
                                                   && proposal.LineMatches.Any(m => m.DocumentLineId == l.DocumentLineId)));
        return new ProposalRow(proposal, order, matches, applied);
    }
}

public sealed record LineRow(int Index, FactLineDto Fact, LineResultDto? Result, OrderLineDto? OrderLine);

public sealed record CheckGroup(string Title, string Anchor, IReadOnlyList<CheckRow> Checks)
{
    public int Count(string status) => Checks.Count(c => c.Check.Status == status);
}

public sealed record CheckRow(CheckDto Check, IReadOnlyList<SourceRef> Sources);

public sealed record SourceRef(string Pointer, string? Value, IReadOnlyList<EvidenceDto> Evidence, MissingFieldDto? Missing);

public sealed record ProposalRow(ProposalView Proposal, OrderDto? Order, IReadOnlyList<ProposalLineRow> Matches, bool AppliedInShownResult);

public sealed record ProposalLineRow(LineMatchDto Match, FactLineDto? DocumentLine, OrderLineDto? OrderLine);
