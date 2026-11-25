using System.Text.Json;
using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Mapping;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Application.Validation;
using InvoiceExceptions.Domain;
using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Application.Cases;

/// <summary>
/// The use cases of the application. There is exactly one way to change a case - <see cref="Apply"/> - and the
/// CLI adapter, the MCP tools and the case page all go through it.
/// <para>
/// A change is all-or-nothing: it is validated as a whole, becomes one new input revision, and every document of
/// the case is evaluated against that revision in the same transaction. A stored conclusion can therefore never be
/// out of date with respect to the stored input, and an invalid input leaves no trace.
/// </para>
/// </summary>
public sealed class CaseService(ICaseStore store, TimeProvider clock)
{
    private readonly SchemaValidator _schemas = SchemaValidator.Shared;

    public IReadOnlyList<string> ListCaseIds() => store.ListCaseIds();

    public CaseView? Get(string caseId) => store.Load(caseId) is { } record ? CaseViewBuilder.Build(record) : null;

    // The four things that can happen to a case, one at a time. Each of them only phrases its request in the
    // step-input contract and hands it to Apply; none has logic of its own.

    /// <summary>Creates the case with its context, or replaces the context of an existing case with a new snapshot.</summary>
    public CaseView SetContext(string caseId, string asOf, JsonElement context, Actor actor) =>
        Apply(Change(caseId, asOf, new { context }), actor);

    /// <summary>Registers the facts of a document: a new document, or a corrected version of one already imported.</summary>
    public CaseView SubmitDocument(string caseId, string contentSha256, JsonElement facts, Actor actor, string? sourcePath = null) =>
        Apply(
            Change(caseId, AsOf(caseId), new { document = new { content_sha256 = contentSha256, facts } }),
            actor,
            new ChangeNotes(DocumentSourcePath: sourcePath));

    /// <summary>Registers an agent's proposal. It decides nothing until a human confirms it.</summary>
    /// <returns>The id given to the proposal.</returns>
    public string RegisterProposal(
        string caseId, string documentId, string orderId, IReadOnlyList<LineMatchDto> lineMatches, string? rationale, Actor actor)
    {
        var proposalId = NextProposalId(caseId);
        var proposal = new ProposalInputDto(proposalId, "agent", documentId, orderId, lineMatches);
        Apply(
            Change(caseId, AsOf(caseId), new { proposals = new[] { proposal } }),
            actor,
            rationale is null ? null : new ChangeNotes(ProposalRationales: new Dictionary<string, string> { [proposalId] = rationale }));
        return proposalId;
    }

    /// <summary>Records that a person confirmed one specific proposal. Reachable from the case page only.</summary>
    public CaseView ConfirmProposal(string caseId, string proposalId, string confirmedBy) =>
        Apply(
            Change(caseId, AsOf(caseId), new { confirmations = new[] { new ConfirmationInputDto(proposalId, "human", confirmedBy) } }),
            Actor.Human(confirmedBy));

    private string AsOf(string caseId) =>
        store.Load(caseId)?.Header.AsOf ?? throw new CaseNotFoundException(caseId);

    private string NextProposalId(string caseId)
    {
        var taken = store.Load(caseId)?.Proposals.Select(p => p.ProposalId).ToHashSet() ?? [];
        for (var number = taken.Count + 1; ; number++)
        {
            var candidate = $"PROP-{number:000}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static JsonElement Change(string caseId, string asOf, object part)
    {
        var envelope = JsonSerializer.SerializeToNode(
            new { schema_version = "1.0", case_id = caseId, step = 1, as_of = asOf, policy_ref = CoreV1.PolicyVersion })!.AsObject();
        foreach (var (name, value) in JsonSerializer.SerializeToNode(part, WireJson.Options)!.AsObject().ToList())
        {
            envelope[name] = value?.DeepClone();
        }

        return JsonSerializer.SerializeToElement(envelope);
    }

    /// <summary>
    /// Applies one change of input, given in the step-input contract: context, document, proposals and confirmations,
    /// in that order.
    /// </summary>
    /// <param name="requireDocument">Reject the change if the case would still have no document afterwards.</param>
    /// <exception cref="InputValidationException">The input was rejected; nothing was stored.</exception>
    public CaseView Apply(JsonElement input, Actor actor, ChangeNotes? notes = null, bool requireDocument = false)
    {
        var schemaIssues = _schemas.ValidateStepInput(input);
        if (schemaIssues.Count > 0)
        {
            throw new InputValidationException(schemaIssues);
        }

        var step = WireJson.Deserialize<StepInputDto>(input);
        var issues = new List<ValidationIssue>();
        var mapper = new DomainMapper(issues);

        var existing = store.Load(step.CaseId);
        var state = existing is null ? CaseState.Empty() : CaseState.From(existing);
        var revision = state.Revision + 1;
        var changes = new List<string>();

        // 1. Context: a full snapshot replaces the previous one.
        ContextRecord? contextRecord = null;
        if (step.Context is { } context)
        {
            state.SetContext(context, mapper.Context(context, "/context"));
            contextRecord = new ContextRecord(revision, context.ContextVersion, Compact(input.GetProperty("context")));
            changes.Add($"context {context.ContextVersion}");
        }

        if (state.ContextModel is null)
        {
            throw InputValidationException.Single("/context", "The case has no context yet; the first input of a case must carry one.");
        }

        // 2. Document: a new document, or a new version of the facts of one already imported.
        var documentRecords = new List<DocumentRecord>();
        if (step.Document is { } submitted)
        {
            var facts = submitted.Facts;
            var known = state.Documents.Any(d => d.DocumentId == facts.DocumentId);
            state.PutDocument(new DocumentState(
                facts.DocumentId, revision, revision, submitted.ContentSha256, facts,
                mapper.Document(facts, "/document/facts"), notes?.DocumentSourcePath));
            documentRecords.Add(new DocumentRecord(
                revision, facts.DocumentId, submitted.ContentSha256,
                Compact(input.GetProperty("document").GetProperty("facts")), notes?.DocumentSourcePath));
            changes.Add(known ? $"new facts for document {facts.DocumentId}" : $"document {facts.DocumentId}");
        }

        if (requireDocument && state.Documents.Count == 0)
        {
            issues.Add(new("/document", "The case has no document yet, so there is nothing to return a result for."));
        }

        if (issues.Count > 0)
        {
            throw new InputValidationException(issues);
        }

        // 3. Proposals: may only fill gaps the deterministic rules leave open.
        var proposalRecords = new List<ProposalRecord>();
        for (var i = 0; i < (step.Proposals?.Count ?? 0); i++)
        {
            var proposal = step.Proposals![i];
            var path = $"/proposals/{i}";
            if (state.Proposals.Any(p => p.ProposalId == proposal.ProposalId))
            {
                issues.Add(new($"{path}/proposal_id", $"Proposal '{proposal.ProposalId}' is already registered."));
                continue;
            }

            var document = state.Documents.FirstOrDefault(d => d.DocumentId == proposal.DocumentId);
            if (document is null)
            {
                issues.Add(new($"{path}/document_id", $"Case {step.CaseId} has no document '{proposal.DocumentId}'."));
                continue;
            }

            var lineMatches = proposal.LineMatches.Select(m => new LineMatch(m.DocumentLineId, m.OrderLineId)).ToList();
            var candidate = new MatchProposal(proposal.ProposalId, proposal.DocumentId, proposal.OrderId, lineMatches, ConfirmedByHuman: false);
            var problems = ProposalRules.Validate(state.ContextModel, document.Model, candidate);
            if (problems.Count > 0)
            {
                issues.AddRange(problems.Select(p => new ValidationIssue(path + p.Path, p.Message)));
                continue;
            }

            var rationale = notes?.ProposalRationales?.GetValueOrDefault(proposal.ProposalId);
            state.Proposals.Add(new ProposalState(proposal.ProposalId, proposal.DocumentId, proposal.OrderId, lineMatches, rationale, revision, null));
            proposalRecords.Add(new ProposalRecord(revision, proposal.ProposalId, proposal.DocumentId, proposal.OrderId, lineMatches, rationale));
            changes.Add($"proposal {proposal.ProposalId}");
        }

        // 4. Confirmations: a human confirms one specific, registered proposal.
        var confirmationRecords = new List<ConfirmationRecord>();
        for (var i = 0; i < (step.Confirmations?.Count ?? 0); i++)
        {
            var confirmation = step.Confirmations![i];
            var path = $"/confirmations/{i}";
            if (actor.IsAgent)
            {
                issues.Add(new(path, "A confirmation is a human decision and cannot be registered by the agent."));
                continue;
            }

            var index = state.Proposals.FindIndex(p => p.ProposalId == confirmation.ProposalId);
            if (index < 0)
            {
                issues.Add(new($"{path}/proposal_id", $"There is no registered proposal '{confirmation.ProposalId}' to confirm."));
                continue;
            }

            if (state.Proposals[index].Confirmation is { } earlier)
            {
                issues.Add(new($"{path}/proposal_id", $"Proposal '{confirmation.ProposalId}' was already confirmed by {earlier.ConfirmedBy}."));
                continue;
            }

            var record = new ConfirmationRecord(revision, confirmation.ProposalId, confirmation.ConfirmedBy);
            state.Proposals[index] = state.Proposals[index] with { Confirmation = record };
            confirmationRecords.Add(record);
            changes.Add($"confirmation of {confirmation.ProposalId} by {confirmation.ConfirmedBy}");
        }

        if (issues.Count > 0)
        {
            throw new InputValidationException(issues);
        }

        // 5. Conclusions for the new revision, then one atomic append.
        store.Append(new CaseCommit(
            step.CaseId,
            existing is null ? new CaseHeader(step.CaseId, step.PolicyRef, step.AsOf) : null,
            new RevisionRecord(revision, actor.Channel, actor.Kind, actor.Name, string.Join("; ", changes), clock.GetUtcNow()),
            contextRecord,
            documentRecords,
            proposalRecords,
            confirmationRecords,
            Evaluate(step.CaseId, state, revision)));

        return Get(step.CaseId)!;
    }

    /// <summary>
    /// Runs the engine for every document of the case, in import order: R6 lets a document be a duplicate of
    /// one imported before it, never of one imported after.
    /// </summary>
    private static List<EvaluationRecord> Evaluate(string caseId, CaseState state, int revision)
    {
        var contextVersion = state.Context!.ContextVersion;
        var records = new List<EvaluationRecord>(state.Documents.Count);
        var earlier = new List<EarlierDocument>(state.Documents.Count);

        foreach (var document in state.Documents)
        {
            var evaluation = InvoiceEvaluator.Evaluate(new EvaluationInput(
                state.ContextModel!, document.Model, [.. earlier], state.ProposalsFor(document.DocumentId)));
            var result = ResultMapper.ToResult(evaluation, document.DocumentId, contextVersion, revision);

            var assessment = evaluation.Assessment is null
                ? null
                : new AssessmentRecord(
                    AssessmentId(caseId, document.DocumentId, revision),
                    CoreV1.PolicyVersion,
                    contextVersion,
                    result.Decision!,
                    WireJson.Serialize(result));

            records.Add(new EvaluationRecord(
                revision, document.DocumentId, result.ImportDisposition, result.OriginalDocumentId, result.ExistingAssessmentRef, assessment));
            earlier.Add(new EarlierDocument(document.Model, assessment?.AssessmentId));
        }

        return records;
    }

    /// <summary>A conclusion is identified by what it is about and the input revision it was drawn from.</summary>
    private static string AssessmentId(string caseId, string documentId, int revision) => $"{caseId}/{documentId}/r{revision}";

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element, WireJson.Options);
}
