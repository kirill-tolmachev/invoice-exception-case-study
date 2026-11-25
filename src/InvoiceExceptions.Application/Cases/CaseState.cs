using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Mapping;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Application.Cases;

/// <summary>
/// The input of a case at its latest revision, rebuilt from the append-only record: the current context,
/// every registered version of every document (in import order) and all proposals with their confirmations.
/// A working copy - <see cref="CaseService"/> applies a change to it and then stores the difference.
/// </summary>
internal sealed class CaseState
{
    public int Revision { get; private init; }
    public ContextDto? Context { get; private set; }
    public ContextSnapshot? ContextModel { get; private set; }

    /// <summary>
    /// Every registered version of every document, in the order they were imported. Registered facts are never
    /// replaced: a different reading of a document that is already there becomes a further version next to it.
    /// </summary>
    public List<DocumentState> Imports { get; } = [];

    public List<ProposalState> Proposals { get; } = [];

    /// <summary>The current (latest registered) version of each document, in order of first import.</summary>
    public IReadOnlyList<DocumentState> Documents => [.. Imports.GroupBy(i => i.DocumentId).Select(versions => versions.Last())];

    public DocumentState? Document(string documentId) => Imports.LastOrDefault(i => i.DocumentId == documentId);

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
            state.Imports.Add(new DocumentState(
                version.DocumentId, version.Revision, version.ContentSha256, facts, mapper.Document(facts, string.Empty), version.SourcePath));
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

    public IReadOnlyList<MatchProposal> ProposalsFor(string documentId) =>
    [
        .. Proposals.Where(p => p.DocumentId == documentId).Select(p =>
            new MatchProposal(p.ProposalId, p.DocumentId, p.OrderId, p.LineMatches, p.Confirmation is not null)),
    ];
}

/// <summary>One registered version of a document's facts.</summary>
/// <param name="Revision">The input revision at which this version was registered.</param>
internal sealed record DocumentState(
    string DocumentId,
    int Revision,
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
