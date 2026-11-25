using System.Globalization;
using System.Text;
using InvoiceExceptions.Domain.Model;

namespace InvoiceExceptions.Domain.Tests.TestData;

/// <summary>Arithmetic of R4 written out independently from the engine (ROUND_HALF_UP to 2 decimals).</summary>
internal static class SpecMath
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Net(decimal quantity, decimal price) => Round(quantity * price);

    public static decimal Tax(decimal net, decimal rate) => Round(net * rate);

    public static decimal D(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}

/// <summary>Tables transcribed by hand from docs/BUSINESS_RULES.md and dataset/policies/core-v1.json (not from CoreV1.cs).</summary>
internal static class Spec
{
    public static readonly IReadOnlyList<CheckCode> DocumentChecks =
    [
        CheckCode.DocumentConflict, CheckCode.SupplierIdentity, CheckCode.BuyerIdentity, CheckCode.OrderMatch,
        CheckCode.CurrencyMatch, CheckCode.TotalsArithmetic, CheckCode.BankAccount,
    ];

    public static readonly IReadOnlyList<CheckCode> LineChecks =
    [
        CheckCode.LineMatch, CheckCode.QtyReceived, CheckCode.QtyOrderRemaining, CheckCode.UnitPrice, CheckCode.LineArithmetic,
    ];

    public static Severity FailSeverity(CheckCode code) => code switch
    {
        CheckCode.OrderMatch or CheckCode.LineMatch or CheckCode.UnitPrice => Severity.NeedsReview,
        _ => Severity.Blocked,
    };

    public static RequiredFact? RequiredFact(CheckCode code) => code switch
    {
        CheckCode.SupplierIdentity => Model.RequiredFact.SupplierRegistrationId,
        CheckCode.BuyerIdentity => Model.RequiredFact.BuyerRegistrationId,
        CheckCode.CurrencyMatch => Model.RequiredFact.DocumentCurrency,
        CheckCode.OrderMatch => Model.RequiredFact.OrderReference,
        CheckCode.LineMatch => Model.RequiredFact.LineMatch,
        CheckCode.QtyReceived => Model.RequiredFact.GoodsReceipts,
        CheckCode.BankAccount => Model.RequiredFact.BankAccount,
        _ => null,
    };

    /// <summary>Every depends_on target that appears in the check's outcome list in core-v1.json.</summary>
    public static IReadOnlyList<CheckCode> AllowedDependsOn(CheckCode code) => code switch
    {
        CheckCode.DocumentConflict => [CheckCode.SupplierIdentity, CheckCode.BuyerIdentity],
        CheckCode.SupplierIdentity or CheckCode.BuyerIdentity or CheckCode.CurrencyMatch or CheckCode.LineMatch => [CheckCode.OrderMatch],
        CheckCode.OrderMatch => [CheckCode.SupplierIdentity],
        CheckCode.QtyReceived or CheckCode.QtyOrderRemaining or CheckCode.UnitPrice => [CheckCode.OrderMatch, CheckCode.LineMatch],
        CheckCode.BankAccount => [CheckCode.SupplierIdentity],
        _ => [],
    };

    /// <summary>The decision rule of BUSINESS_RULES.md "Итог по приоритету".</summary>
    public static Decision Decide(IEnumerable<CheckResult> checks)
    {
        var list = checks.ToList();
        if (list.Any(c => c.Status == CheckStatus.Fail && c.Severity == Severity.Blocked))
        {
            return Decision.Blocked;
        }

        if (list.Any(c => c.Status == CheckStatus.Unknown))
        {
            return Decision.NeedsInformation;
        }

        if (list.Any(c => c.Status == CheckStatus.Fail && c.Severity == Severity.NeedsReview))
        {
            return Decision.NeedsReview;
        }

        return Decision.Clear;
    }
}

internal static class Expect
{
    public static CheckResult Check(this Assessment assessment, CheckCode code, string objectId) =>
        Assert.Single(assessment.Checks, c => c.Code == code && c.ObjectId == objectId);

    public static CheckResult Doc(this Assessment assessment, CheckCode code) => assessment.Check(code, Scenario.DocumentId);

    public static LineResult Line(this Assessment assessment, string lineId) =>
        Assert.Single(assessment.Lines, l => l.DocumentLineId == lineId);

    public static string Describe(CheckResult c) =>
        $"{c.Code}@{c.ObjectId}: {c.Status} severity={c.Severity?.ToString() ?? "-"} depends_on={c.DependsOn?.ToString() ?? "-"}";

    private static string Describe(CheckResult c, CheckStatus status, Severity? severity, CheckCode? dependsOn) =>
        $"{c.Code}@{c.ObjectId}: {status} severity={severity?.ToString() ?? "-"} depends_on={dependsOn?.ToString() ?? "-"}";

    public static void Pass(CheckResult c) => Assert.Equal(Describe(c, CheckStatus.Pass, null, null), Describe(c));

    public static void Fail(CheckResult c, Severity severity) => Assert.Equal(Describe(c, CheckStatus.Fail, severity, null), Describe(c));

    public static void RootUnknown(CheckResult c) => Assert.Equal(Describe(c, CheckStatus.Unknown, null, null), Describe(c));

    public static void DependsOn(CheckResult c, CheckCode on) => Assert.Equal(Describe(c, CheckStatus.Unknown, null, on), Describe(c));

    public static void NotApplicable(CheckResult c) => Assert.Equal(Describe(c, CheckStatus.NotApplicable, null, null), Describe(c));

    /// <summary>All checks except the listed (code, object) pairs pass.</summary>
    public static void AllOthersPass(Assessment assessment, params (CheckCode Code, string ObjectId)[] except)
    {
        var offenders = assessment.Checks
            .Where(c => !except.Contains((c.Code, c.ObjectId)))
            .Where(c => c.Status != CheckStatus.Pass)
            .Select(Describe)
            .ToList();
        Assert.True(offenders.Count == 0, "Unexpected non-pass checks:\n" + string.Join("\n", offenders));
    }

    /// <summary>Questions as a sorted multiset of "required_fact -> [targets]".</summary>
    public static string Questions(Assessment assessment) =>
        string.Join(" | ", assessment.Questions
            .Select(q => $"{q.RequiredFact}->[{string.Join(",", q.TargetObjectIds)}]")
            .Order(StringComparer.Ordinal));

    public static string Questions(params (RequiredFact Fact, string Target)[] expected) =>
        string.Join(" | ", expected.Select(q => $"{q.Fact}->[{q.Target}]").Order(StringComparer.Ordinal));

    /// <summary>A full, order-sensitive rendering of a result, used to compare two evaluations for equality.</summary>
    public static string Canonical(Evaluation evaluation)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{evaluation.Disposition}|{evaluation.OriginalDocumentId}|{evaluation.ExistingAssessmentRef}\n");
        var a = evaluation.Assessment;
        if (a is null)
        {
            return sb.Append("no assessment").ToString();
        }

        sb.Append(CultureInfo.InvariantCulture, $"{a.MatchedOrderId}|{a.OrderMatchSource}|{a.Decision}|{a.Totals}\n");
        foreach (var l in a.Lines)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{l}\n");
        }

        foreach (var c in a.Checks)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{Describe(c)}|{c.Explanation}|{Dict(c.Actual)}|{Dict(c.Expected)}|{string.Join(",", c.EvidenceRefs)}\n");
        }

        foreach (var q in a.Questions)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{q.RequiredFact}|{string.Join(",", q.TargetObjectIds)}|{q.Text}\n");
        }

        return sb.ToString();
    }

    private static string Dict(IReadOnlyDictionary<string, string?> d) =>
        string.Join(";", d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}
