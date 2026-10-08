using System.Numerics;

namespace LogCarver.Core;

/// <summary>
/// A DECIMAL/NUMERIC value whose precision (29-38 digits) exceeds what
/// System.Decimal can represent (its mantissa tops out around 28-29
/// digits) - SQL Server's own on-disk format for this precision range
/// uses a 128-bit unsigned magnitude (4 little-endian uint32 "groups"),
/// which doesn't fit System.Decimal's 96-bit one. BigInteger has no such
/// ceiling, so it's used here instead - found for real 2026-10-08 via an
/// independent cross-check agent hitting a DECIMAL(38,10) column:
/// undo generation was silently refusing this precision range entirely
/// (an honest, safely-degrading refusal, not corruption - but a real,
/// fixable gap given .NET already ships BigInteger for exactly this).
/// </summary>
/// <param name="UnscaledValue">
/// The full-precision integer magnitude, already signed (negative for a
/// negative decimal value) - i.e. the real value is UnscaledValue /
/// 10^Scale, matching System.Decimal's own (lo,mid,hi,sign,scale)
/// convention just without the 96-bit ceiling.
/// </param>
/// <param name="Scale">Digits after the decimal point.</param>
public readonly record struct HighPrecisionDecimal(BigInteger UnscaledValue, int Scale)
{
    /// <summary>
    /// Formats as a plain decimal literal (no scientific notation, no
    /// thousands separator) - e.g. "-123456789012345.1234567891". Safe to
    /// embed directly into generated SQL text the same way
    /// System.Decimal's own ToString(InvariantCulture) already is for the
    /// 1-28 digit case.
    /// </summary>
    public override string ToString()
    {
        bool negative = UnscaledValue.Sign < 0;
        string digits = BigInteger.Abs(UnscaledValue).ToString();
        if (Scale <= 0)
            return negative ? $"-{digits}" : digits;

        // Left-pad so there are always at least Scale+1 digits (a leading
        // "0" before the decimal point when the magnitude itself has
        // fewer digits than the scale, e.g. unscaled=5, scale=3 -> "0.005",
        // not ".005" or a malformed split).
        if (digits.Length <= Scale)
            digits = digits.PadLeft(Scale + 1, '0');

        string wholePart = digits[..^Scale];
        string fractionPart = digits[^Scale..];
        return negative ? $"-{wholePart}.{fractionPart}" : $"{wholePart}.{fractionPart}";
    }
}
