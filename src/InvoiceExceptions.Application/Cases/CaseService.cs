using System.Text.Json;
using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Documents;
using InvoiceExceptions.Application.Mapping;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Application.Validation;
using InvoiceExceptions.Domain;
using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Application.Cases;

/// <summary>
/// The use cases of the application. There is exactly one way to change a case - <see cref="ApplyChange"/> - and the
/// CLI adapter, the MCP tools and the case page all go through it.
/// <para>
/// A change is all-or-nothing: it is validated as a whole, becomes one new input revision, and every document the
/// change can affect is re-evaluated against that revision in the same transaction. A stored conclusion can
/// therefore never be out of date with respect to the stored input, an invalid input leaves no trace, and a
/// document the change does not touch keeps the conclusion it has - nothing is added to its history.
/// </para>
/// <para>
/// Registered facts are never replaced. Sending a document that is already there is an import like any other and
/// goes through R6: unchanged, it is a duplicate and changes nothing; changed, it is a conflicting version that is
/// stored next to the original. Nobody - in particular not the agent - can quietly rewrite facts to get another outcome.
/// </para>
/// </summary>
public sealed class CaseService(ICaseStore store, TimeProvider clock)
{
    private readonly SchemaValidator _schemas = SchemaValidator.Shared;

    public IReadOnlyList<string> ListCaseIds() => store.ListCaseIds();

    public CaseView? Get(string caseId) => store.Load(caseId) is { } record ? CaseViewBuilder.Build(record) : null;

    // The four things that can happen to a case, one at a time. Each of them only phrases its request in the
    // step-input contract and hands it to ApplyChange; none has logic of its own.

    /// <summary>Creates the case with its context, or replaces the context of an existing case with a new snapshot.</summary>
    public CaseView SetContext(string caseId, string asOf, JsonElement context, Actor actor) =>
        Apply(Envelope(caseId, asOf, new { context }), actor);

    /// <summary>Registers the facts read from a document.</summary>
    /// <param name="documentText">The document's own text, if the caller has it; the facts are then checked against it.</param>
    public ChangeOutcome SubmitDocument(
        string caseId,
        string contentSha256,
        JsonElement facts,
        Actor actor,
        string? sourcePath = null,
        IReadOnlyList<PageText>? documentText = null) =>
        ApplyChange(
            Envelope(caseId, AsOf(caseId), new { document = new { content_sha256 = contentSha256, facts } }),
            actor,
            new ChangeNotes(DocumentSourcePath: sourcePath, DocumentText: documentText));

    /// <summary>Registers an agent's proposal. It decides nothing until a human confirms it.</summary>
    /// <returns>The id given to the proposal.</returns>
    public string RegisterProposal(
        string caseId, string documentId, string orderId, IReadOnlyList<LineMatchDto> lineMatches, string? rationale, Actor actor)
    {
        var proposalId = NextProposalId(caseId);
        var proposal = new ProposalInputDto(proposalId, "agent", documentId, orderId, lineMatches);
        Apply(
            Envelope(caseId, AsOf(caseId), new { proposals = new[] { proposal } }),
            actor,
            rationale is null ? null : new ChangeNotes(ProposalRationales: new Dictionary<string, string> { [proposalId] = rationale }));
        return proposalId;
    }

    /// <summary>Records that a person confirmed one specific proposal. Reachable from the case page only.</summary>
    public CaseView ConfirmProposal(string caseId, string proposalId, string confirmedBy) =>
        Apply(
            Envelope(caseId, AsOf(caseId), new { confirmations = new[] { new ConfirmationInputDto(proposalId, "human", confirmedBy) } }),
            Actor.Human(confirmedBy));

    /// <summary><see cref="ApplyChange"/> for callers that only need the case as it is afterwards.</summary>
    public CaseView Apply(JsonElement input, Actor actor, ChangeNotes? notes = null, bool requireDocument = false) =>
        ApplyChange(input, actor, notes, requireDocument).View;

    /// <summary>
    /// Applies one change of input, given in the step-input contract: context, document, proposals and confirmations,
    /// in that order.
    /// </summary>
    /// <param name="requireDocument">Reject the change if the case would still have no document afterwards.</param>
    /// <exception cref="InputValidationException">The input was rejected; nothing was stored.</exception>
    public ChangeOutcome ApplyChange(JsonElement input, Actor actor, ChangeNotes? notes = null, bool requireDocument = false)
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

        // The documents whose conclusion this change can alter. Everything else keeps the conclusion it already has.
        var affected = new HashSet<string>(StringComparer.Ordinal);

        // 1. Context: a full snapshot replaces the previous one. The same snapshot sent again is not a change.
        ContextRecord? contextRecord = null;
        if (step.Context is { } context)
        {
            var payload = Compact(input.GetProperty("context"));
            if (existing?.Contexts is not [.., { } current] || current.PayloadJson != payload)
            {
                state.SetContext(context, mapper.Context(context, "/context"));
                contextRecord = new ContextRecord(revision, context.ContextVersion, payload);
                changes.Add($"context {context.ContextVersion}");
            }
        }

        if (state.ContextModel is null)
        {
            throw InputValidationException.Single("/context", "The case has no context yet; the first input of a case must carry one.");
        }

        // 2. Document: an import, classified by R6 against everything imported before it - earlier versions of the
        //    same document included.
        var documentRecords = new List<DocumentRecord>();
        ResultDto? resentDuplicate = null;
        if (step.Document is { } submitted)
        {
            var facts = submitted.Facts;
            var version = new DocumentState(
                facts.DocumentId, revision, submitted.ContentSha256, facts,
                mapper.Document(facts, "/document/facts"), notes?.DocumentSourcePath);
            if (notes?.DocumentText is { } pages)
            {
                issues.AddRange(Unbacked(facts, pages));
            }

            if (issues.Count > 0)
            {
                throw new InputValidationException(issues);
            }

            var known = state.Document(facts.DocumentId) is not null;
            var evaluation = known ? Run(state, version, existing?.Evaluations ?? []) : null;
            if (evaluation?.Disposition == ImportDisposition.Duplicate)
            {
                // Nothing about the document is stored: its facts and its conclusion stay exactly as they are.
                resentDuplicate = ResultMapper.ToResult(evaluation, facts.DocumentId, state.Context!.ContextVersion, revision);
                changes.Add($"document {facts.DocumentId} sent again unchanged (duplicate of {evaluation.OriginalDocumentId})");
            }
            else
            {
                state.Imports.Add(version);
                documentRecords.Add(new DocumentRecord(
                    revision, facts.DocumentId, submitted.ContentSha256,
                    Compact(input.GetProperty("document").GetProperty("facts")), notes?.DocumentSourcePath));
                changes.Add(known ? $"document {facts.DocumentId} sent again with different facts" : $"document {facts.DocumentId}");
                affected.Add(facts.DocumentId);
            }
        }

        if (requireDocument && state.Imports.Count == 0)
        {
            throw InputValidationException.Single("/document", "The case has no document yet, so there is nothing to return a result for.");
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

            var document = state.Document(proposal.DocumentId);
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
            affected.Add(proposal.DocumentId);
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
            affected.Add(state.Proposals[index].DocumentId);
        }

        if (issues.Count > 0)
        {
            throw new InputValidationException(issues);
        }

        // 5. Conclusions for the new revision, then one atomic append.
        store.Append(new CaseCommit(
            step.CaseId,
            existing is null ? new CaseHeader(step.CaseId, step.PolicyRef, step.AsOf) : null,
            new RevisionRecord(
                revision, actor.Channel, actor.Kind, actor.Name,
                changes.Count == 0 ? "input sent again unchanged" : string.Join("; ", changes),
                clock.GetUtcNow()),
            contextRecord,
            documentRecords,
            proposalRecords,
            confirmationRecords,
            // A new context touches every document.
            Evaluate(step.CaseId, state, revision, existing?.Evaluations ?? [], contextRecord is null ? affected : null)));

        return new ChangeOutcome(Get(step.CaseId)!, resentDuplicate);
    }

    /// <summary>
    /// Re-evaluates the current version of the affected documents (all of them when <paramref name="affected"/> is null).
    /// Documents are taken in the order their current version was registered, so that a conclusion another document
    /// refers to already exists when it is needed.
    /// </summary>
    private static List<EvaluationRecord> Evaluate(
        string caseId, CaseState state, int revision, IReadOnlyList<EvaluationRecord> stored, IReadOnlySet<string>? affected)
    {
        var contextVersion = state.Context!.ContextVersion;
        var evaluations = new List<EvaluationRecord>(stored);
        var records = new List<EvaluationRecord>();

        foreach (var document in state.Documents.OrderBy(d => d.Revision))
        {
            if (affected is not null && !affected.Contains(document.DocumentId))
            {
                continue;
            }

            var evaluation = Run(state, document, evaluations);
            var result = ResultMapper.ToResult(evaluation, document.DocumentId, contextVersion, revision);
            var record = new EvaluationRecord(
                revision,
                document.DocumentId,
                result.ImportDisposition,
                result.OriginalDocumentId,
                result.ExistingAssessmentRef,
                evaluation.Assessment is null
                    ? null
                    : new AssessmentRecord(
                        AssessmentId(caseId, document.DocumentId, revision),
                        CoreV1.PolicyVersion,
                        contextVersion,
                        result.Decision!,
                        WireJson.Serialize(result)));

            records.Add(record);
            evaluations.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Runs the engine for one version of a document. R6 lets it be a duplicate of, or conflict with, anything
    /// imported before it - never something imported after.
    /// </summary>
    private static Evaluation Run(CaseState state, DocumentState version, IReadOnlyList<EvaluationRecord> evaluations)
    {
        var earlier = state.Imports
            .Where(import => import.Revision < version.Revision)
            .Select(import => new EarlierDocument(
                import.Model,
                StandingAssessment(state, import, evaluations),
                SameDocument: import.DocumentId == version.DocumentId))
            .ToList();

        return InvoiceEvaluator.Evaluate(new EvaluationInput(
            state.ContextModel!, version.Model, earlier, state.ProposalsFor(version.DocumentId)));
    }

    /// <summary>
    /// The conclusion that stands for a registered version: the assessment of the last evaluation made while that
    /// version was the current one. Null if that evaluation found it to be a duplicate.
    /// </summary>
    private static string? StandingAssessment(CaseState state, DocumentState import, IReadOnlyList<EvaluationRecord> evaluations)
    {
        var supersededAt = state.Imports
            .FirstOrDefault(i => i.DocumentId == import.DocumentId && i.Revision > import.Revision)?.Revision ?? int.MaxValue;
        return evaluations
            .LastOrDefault(e => e.DocumentId == import.DocumentId && e.Revision >= import.Revision && e.Revision < supersededAt)
            ?.Assessment?.AssessmentId;
    }

    /// <summary>What in the facts is not backed by the document they claim to come from.</summary>
    private static IEnumerable<ValidationIssue> Unbacked(FactsDto facts, IReadOnlyList<PageText> pages)
    {
        var review = FactsReviewer.Review(facts, pages);
        for (var i = 0; i < review.Evidence.Count; i++)
        {
            if (review.Evidence[i].Status == EvidenceStatus.NotFound)
            {
                yield return new ValidationIssue(
                    $"/document/facts/evidence/{i}/excerpt",
                    $"The excerpt for {review.Evidence[i].FieldPath} was not found on page {review.Evidence[i].Page} of the document. Copy it verbatim from the text of that page.");
            }
        }

        foreach (var note in review.Notes)
        {
            yield return new ValidationIssue("/document/facts" + note.FieldPath, note.Message);
        }
    }

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

    private static JsonElement Envelope(string caseId, string asOf, object part)
    {
        var envelope = JsonSerializer.SerializeToNode(
            new { schema_version = "1.0", case_id = caseId, step = 1, as_of = asOf, policy_ref = CoreV1.PolicyVersion })!.AsObject();
        foreach (var (name, value) in JsonSerializer.SerializeToNode(part, WireJson.Options)!.AsObject().ToList())
        {
            envelope[name] = value?.DeepClone();
        }

        return JsonSerializer.SerializeToElement(envelope);
    }

    /// <summary>A conclusion is identified by what it is about and the input revision it was drawn from.</summary>
    private static string AssessmentId(string caseId, string documentId, int revision) => $"{caseId}/{documentId}/r{revision}";

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element, WireJson.Options);
}
