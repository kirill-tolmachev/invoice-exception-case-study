using System.Text;

namespace InvoiceExceptions.Domain.Policy;

/// <summary>
/// The three normalizations of core-v1. Comparisons in the rules always go through these; raw strings are never compared,
/// with one deliberate exception: units of measure, which the policy does not normalize.
/// </summary>
public static class Normalize
{
    /// <summary>registration_id, document number, PO reference, SKU: trim + uppercase. Separators and leading zeros are kept.</summary>
    public static string Identifier(string value) => Upper(value.Trim());

    public static string? OptionalIdentifier(string? value) => value is null ? null : Identifier(value);

    /// <summary>Legal names, aliases, line descriptions: trim, collapse inner whitespace, case-fold.</summary>
    public static string Name(string value) => CaseFold(CollapseWhitespace(value));

    /// <summary>IBAN: remove all whitespace + uppercase.</summary>
    public static string Iban(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return Upper(builder.ToString());
    }

    public static string? OptionalIban(string? value) => value is null ? null : Iban(value);

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    // .NET has no Unicode full case folding. Upper-then-lower covers the one-to-one foldings
    // ("ſ" -> "s", final sigma, etc.); the sharp s is the one expansion that realistically occurs in
    // European company and product names, so it is handled explicitly on both sides.
    private static string CaseFold(string value) =>
        value.ToUpperInvariant().ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal);

    private static string Upper(string value) =>
        value.ToUpperInvariant().Replace("ß", "SS", StringComparison.Ordinal);
}
