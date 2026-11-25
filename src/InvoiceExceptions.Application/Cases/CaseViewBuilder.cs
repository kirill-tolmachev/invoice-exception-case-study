using InvoiceExceptions.Application.Contracts;
using InvoiceExceptions.Application.Mapping;
using InvoiceExceptions.Application.Storage;

namespace InvoiceExceptions.Application.Cases;

/// <summary>Builds the read model from the stored record. Conclusions are read back as stored, never recomputed.</summary>
internal static class CaseViewBuilder
{
    public static CaseView Build(CaseRecord record)
    {
        var currentRevision = record.Revisions.Count == 0 ? 0 : record.Revisions[^1].Revision;
        var latestContext = record.Contexts.Count == 0 ? null : record.Contexts[^1];
        var confirmations = record.Confirmations.ToDictionary(c => c.ProposalId);

        var documents = record.Documents
            .GroupBy(d => d.DocumentId)
            .Select(versions =>
            {
                var first = versions.First();
                var latest = versions.Last();
                var history = record.Evaluations
                    .Where(e => e.DocumentId == latest.DocumentId)
                    .Select(e => Evaluation(e, ContextVersionAt(record, e.Revision)))
                    .ToList();
                var proposals = record.Proposals
                    .Where(p => p.DocumentId == latest.DocumentId)
                    .Select(p => new ProposalView(
                        p.ProposalId,
                        p.DocumentId,
                        p.OrderId,
                        [.. p.LineMatches.Select(m => new LineMatchDto(m.DocumentLineId, m.OrderLineId))],
                        p.Rationale,
                        p.Revision,
                        confirmations.TryGetValue(p.ProposalId, out var c) ? new ConfirmationView(c.ConfirmedBy, c.Revision) : null))
                    .ToList();

                return new DocumentView(
                    latest.DocumentId,
                    latest.ContentSha256,
                    latest.SourcePath,
                    first.Revision,
                    latest.Revision,
                    WireJson.Deserialize<FactsDto>(latest.FactsJson),
                    history[^1],
                    history,
                    proposals);
            })
            .OrderBy(d => d.ImportedAtRevision)
            .ToList();

        return new CaseView(
            record.Header.CaseId,
            record.Header.PolicyVersion,
            record.Header.AsOf,
            currentRevision,
            latestContext?.ContextVersion,
            latestContext is null ? null : WireJson.Deserialize<ContextDto>(latestContext.PayloadJson),
            documents,
            [.. record.Revisions.Select(r => new RevisionView(r.Revision, r.Channel, r.Actor, r.ActorName, r.Summary, r.RecordedAt))]);
    }

    private static EvaluationView Evaluation(EvaluationRecord evaluation, string contextVersion) =>
        evaluation.Assessment is { } assessment
            ? new EvaluationView(evaluation.Revision, assessment.AssessmentId, WireJson.Deserialize<ResultDto>(assessment.ResultJson))
            : new EvaluationView(evaluation.Revision, null, ResultMapper.DuplicateResult(
                evaluation.DocumentId, contextVersion, evaluation.Revision, evaluation.OriginalDocumentId!, evaluation.ExistingAssessmentRef));

    private static string ContextVersionAt(CaseRecord record, int revision) =>
        record.Contexts.Last(c => c.Revision <= revision).ContextVersion;
}
