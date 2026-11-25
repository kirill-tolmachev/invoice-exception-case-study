using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Domain.Policy;

namespace InvoiceExceptions.Domain.Rules;

/// <summary>Turns a set of checks into the one outcome the business acts on. This is the only place a decision is made.</summary>
internal static class DecisionPolicy
{
    /// <summary>
    /// Priority: a broken rule blocks; otherwise anything unknown asks for information; otherwise anything a human
    /// must judge goes to review; otherwise the invoice is clear. Checks that are not applicable are ignored.
    /// </summary>
    public static Decision Decide(IReadOnlyCollection<CheckResult> checks)
    {
        if (checks.Any(c => c.Status == CheckStatus.Fail && c.Severity == Severity.Blocked))
        {
            return Decision.Blocked;
        }

        if (checks.Any(c => c.Status == CheckStatus.Unknown))
        {
            return Decision.NeedsInformation;
        }

        if (checks.Any(c => c.Status == CheckStatus.Fail))
        {
            return Decision.NeedsReview;
        }

        return Decision.Clear;
    }

    /// <summary>One question per missing fact. Checks that are merely waiting for another check ask nothing.</summary>
    public static IReadOnlyList<Question> Questions(IEnumerable<CheckResult> checks) =>
    [
        .. checks.Where(c => c.IsRootUnknown).Select(c =>
        {
            var fact = CoreV1.RequiredFactOf(c.Code)!.Value;
            return new Question(fact, [c.ObjectId], CoreV1.QuestionText(fact));
        }),
    ];
}
