using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Mapping;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Application.Validation;
using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Application.Cases;

/// <summary>
/// The input of a case at its latest revision, rebuilt from the append-only record: the current context,
/// the latest facts of every document (in import order) and all proposals with their confirmations.
/// A working copy - <see cref="CaseService"/> applies a change to it and then stores the difference.
/// </summary>
internal sealed class CaseState
{
    public int Revision { get; private init; }
    public ContextDto? Context { get; private set; }
    public ContextSnapshot? ContextModel { get; private set; }
    public List<DocumentState> Documents { get; } = [];
    public List<ProposalState> Proposals { get; } = [];

    public static CaseState Empty() => new();

    public static CaseState From(CaseRecord record)
    {
        var state = new CaseState { Revision = record.Revisions.Count == 0 ? 0 : record.Revisions[^1].Revision };

        // Stored payloads were validated when they were accepted, so issues cannot arise here.
        var mapper = new DomainMapper([]);
        if (record.Contexts.Count > 0)
        {
            var context = WireJson.Deserialize<ContextDto>(record.Contexts[^1].PayloadJson);
            state.SetContext(context, mapper.Context(context, string.Empty));
        }

        foreach (var version in record.Documents)
        {
            var facts = WireJson.Deserialize<FactsDto>(version.FactsJson);
            state.PutDocument(new DocumentState(
                version.DocumentId, version.Revision, version.Revision, version.ContentSha256, facts,
                mapper.Document(facts, string.Empty), version.SourcePath));
        }

        var confirmations = record.Confirmations.ToDictionary(c => c.ProposalId);
        foreach (var proposal in record.Proposals)
        {
            state.Proposals.Add(new ProposalState(
                proposal.ProposalId, proposal.DocumentId, proposal.OrderId, proposal.LineMatches, proposal.Rationale,
                proposal.Revision, confirmations.GetValueOrDefault(proposal.ProposalId)));
        }

        return state;
    }

    public void SetContext(ContextDto context, ContextSnapshot model)
    {
        Context = context;
        ContextModel = model;
    }

    /// <summary>Adds a document, or replaces the facts of one that is already there while keeping its place in the import order.</summary>
    public void PutDocument(DocumentState document)
    {
        var index = Documents.FindIndex(d => d.DocumentId == document.DocumentId);
        if (index < 0)
        {
            Documents.Add(document);
        }
        else
        {
            Documents[index] = document with { ImportedAtRevision = Documents[index].ImportedAtRevision };
        }
    }

    public IReadOnlyList<MatchProposal> ProposalsFor(string documentId) =>
    [
        .. Proposals.Where(p => p.DocumentId == documentId).Select(p =>
            new MatchProposal(p.ProposalId, p.DocumentId, p.OrderId, p.LineMatches, p.Confirmation is not null)),
    ];
}

internal sealed record DocumentState(
    string DocumentId,
    int ImportedAtRevision,
    int FactsRevision,
    string ContentSha256,
    FactsDto Facts,
    InvoiceDocument Model,
    string? SourcePath);

internal sealed record ProposalState(
    string ProposalId,
    string DocumentId,
    string OrderId,
    IReadOnlyList<LineMatch> LineMatches,
    string? Rationale,
    int RegisteredAtRevision,
    ConfirmationRecord? Confirmation);
