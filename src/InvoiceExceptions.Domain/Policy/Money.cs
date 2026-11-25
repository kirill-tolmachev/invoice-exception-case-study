namespace InvoiceExceptions.Domain.Policy;

/// <summary>
/// Decimal arithmetic of core-v1. <see cref="decimal"/> is a base-10 type, so "0.35" is exactly 0.35;
/// binary floating point never appears anywhere in the solution.
/// </summary>
public static class Money
{
    /// <summary>ROUND_HALF_UP to two decimals: ties go away from zero (2.345 -> 2.35, 2.344 -> 2.34).</summary>
    public static decimal Round(decimal value) =>
        Math.Round(value, CoreV1.MoneyScale, MidpointRounding.AwayFromZero);

    /// <summary>round(quantity x unit price, 2)</summary>
    public static decimal NetAmount(decimal quantity, decimal unitNetPrice) => Round(quantity * unitNetPrice);

    /// <summary>round(net x rate, 2); the rate is a fraction such as 0.20.</summary>
    public static decimal TaxAmount(decimal netAmount, decimal taxRate) => Round(netAmount * taxRate);
}
