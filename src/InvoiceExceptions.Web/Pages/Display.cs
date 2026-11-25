using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace InvoiceExceptions.Web.Pages;

/// <summary>
/// Small rendering helpers shared by the pages. Every piece of text that reaches the browser through them goes through
/// <see cref="HtmlContentBuilder.Append(string)"/>, which HTML-encodes it: document and agent text is untrusted.
/// Only the fixed markup written here is appended as HTML.
/// </summary>
public static class Display
{
    /// <summary>A value, or a visible "not known yet" marker when it is null. Never renders null as 0 or as an empty cell.</summary>
    public static IHtmlContent Value(string? value, string unknown = "not known yet") =>
        value is null
            ? Tag("span", "unknown", unknown)
            : new HtmlString(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(value));

    /// <summary>A label with a status class. The text always carries the meaning; the class only adds colour.</summary>
    public static IHtmlContent Badge(string text, string cssClass) => Tag("span", "badge " + cssClass, text);

    public static IHtmlContent Status(string status) => Badge(Labels.Status(status), "status-" + Labels.Css(status));

    public static IHtmlContent Decision(string? decision) =>
        decision is null ? Badge("no decision", "decision-none") : Badge(Labels.Decision(decision), "decision-" + Labels.Css(decision));

    public static IHtmlContent MatchSource(string? source) =>
        source is null ? Tag("span", "unknown", "not matched") : Badge(Labels.MatchSource(source), "source-" + Labels.Css(source));

    /// <summary>
    /// Shows the difference between two figures as text ("differs by +0.02"), or nothing when they are equal
    /// or cannot be compared.
    /// </summary>
    public static IHtmlContent Difference(string? value, string? reference, string what)
    {
        if (Numbers.Delta(value, reference) is not { } delta || delta == 0m)
        {
            return HtmlString.Empty;
        }

        return Tag("span", "delta", $"{(delta > 0 ? "+" : "")}{delta.ToString(CultureInfo.InvariantCulture)} vs {what}");
    }

    /// <summary>"≠ order" marker for two texts that should agree, e.g. units.</summary>
    public static IHtmlContent TextDifference(string? value, string? reference, string what) =>
        value is null || reference is null || string.Equals(value, reference, StringComparison.Ordinal)
            ? HtmlString.Empty
            : Tag("span", "delta", "differs from " + what);

    private static IHtmlContent Tag(string name, string cssClass, string text) =>
        new HtmlContentBuilder()
            .AppendHtml($"<{name} class=\"{cssClass}\">")
            .Append(text)
            .AppendHtml($"</{name}>");
}

public static class Numbers
{
    public static decimal? Parse(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;

    public static decimal? Delta(string? value, string? reference) =>
        Parse(value) is { } a && Parse(reference) is { } b ? a - b : null;

    public static bool Differ(string? value, string? reference) => Delta(value, reference) is { } d && d != 0m;

    /// <summary>True when <paramref name="value"/> is known to exceed <paramref name="limit"/>.</summary>
    public static bool Exceeds(string? value, string? limit) => Delta(value, limit) is > 0m;
}

/// <summary>Plain-English labels for the wire values of the result contract.</summary>
public static class Labels
{
    public static string Decision(string decision) => decision switch
    {
        "clear" => "Clear",
        "needs_information" => "Needs information",
        "needs_review" => "Needs review",
        "blocked" => "Blocked",
        _ => decision,
    };

    public static string DecisionMeaning(string? decision) => decision switch
    {
        "clear" => "No check failed and nothing is unknown. The invoice may proceed.",
        "needs_information" => "Some facts are missing, so some checks could not be decided. See the open questions.",
        "needs_review" => "A check failed that a person has to look at. Nothing is blocked outright.",
        "blocked" => "A check failed that blocks the invoice. It must not proceed as it stands.",
        _ => "",
    };

    public static string Status(string status) => status switch
    {
        "pass" => "Pass",
        "fail" => "Fail",
        "unknown" => "Unknown",
        "not_applicable" => "Not applicable",
        _ => status,
    };

    public static string MatchSource(string source) => source switch
    {
        "deterministic" => "Deterministic",
        "agent_proposal" => "Agent proposal (unconfirmed)",
        "human_confirmation" => "Confirmed by a person",
        _ => source,
    };

    public static string Disposition(string disposition) => disposition switch
    {
        "new" => "New document",
        "duplicate" => "Duplicate",
        "conflict" => "Conflict with an earlier document",
        _ => disposition,
    };

    public static string MissingReason(string reason) => reason.Replace('_', ' ');

    /// <summary>A CSS-safe token for a wire value; anything unexpected collapses to "other".</summary>
    public static string Css(string value) =>
        value.Length > 0 && value.All(c => c is (>= 'a' and <= 'z') or '_') ? value.Replace('_', '-') : "other";
}
