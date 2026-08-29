using System.Data.SqlTypes;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace GlutenFree.Databricks.AdoNet;

/// <summary>
/// An arbitrary-precision, base-10 decimal value (BigDecimal-style: a
/// <see cref="BigInteger"/> unscaled value and a non-negative scale), offering a friendlier
/// alternative to <see cref="SqlDecimal"/> for Databricks <c>DECIMAL</c> values whose
/// precision exceeds <see cref="decimal"/>'s ~28 significant digits.
/// </summary>
/// <remarks>
/// The numeric value is <c>UnscaledValue × 10⁻ˢᶜᵃˡᵉ</c>. Equality and comparison are
/// numeric (<c>1.50 == 1.5</c>); trailing zeros are preserved by <see cref="ToString()"/>.
/// </remarks>
public readonly struct DatabricksDecimal
    : IEquatable<DatabricksDecimal>, IComparable<DatabricksDecimal>, IComparable, IFormattable
{
    /// <summary>The value zero (scale 0).</summary>
    public static readonly DatabricksDecimal Zero = new(BigInteger.Zero, 0);

    /// <summary>The value one (scale 0).</summary>
    public static readonly DatabricksDecimal One = new(BigInteger.One, 0);

    /// <summary>Creates a value from an unscaled integer and a scale: <c>unscaledValue × 10⁻ˢᶜᵃˡᵉ</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="scale"/> is negative.</exception>
    public DatabricksDecimal(BigInteger unscaledValue, int scale)
    {
        if (scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be non-negative.");
        }

        UnscaledValue = unscaledValue;
        Scale = scale;
    }

    /// <summary>The unscaled integer value.</summary>
    public BigInteger UnscaledValue { get; }

    /// <summary>The number of digits to the right of the decimal point.</summary>
    public int Scale { get; }

    /// <summary>The number of significant decimal digits in <see cref="UnscaledValue"/> (at least 1).</summary>
    public int Precision
    {
        get
        {
            var abs = BigInteger.Abs(UnscaledValue);
            if (abs.IsZero)
            {
                return 1;
            }

            var digits = (int)Math.Ceiling(BigInteger.Log10(abs));
            // Log10 of an exact power of ten needs a correction.
            return BigInteger.Pow(10, digits) <= abs ? digits + 1 : digits;
        }
    }

    /// <summary>-1, 0, or 1 depending on the sign of the value.</summary>
    public int Sign => UnscaledValue.Sign;

    /// <summary>True when the value is numerically zero.</summary>
    public bool IsZero => UnscaledValue.IsZero;

    /// <summary>Parses an invariant-culture decimal string, e.g. <c>-12345.678</c>.</summary>
    /// <exception cref="FormatException">When the input is not a plain decimal number.</exception>
    public static DatabricksDecimal Parse(string value)
        => TryParse(value, out var result)
            ? result
            : throw new FormatException($"'{value}' is not a valid decimal value.");

    /// <summary>Attempts to parse an invariant-culture decimal string.</summary>
    public static bool TryParse(string? value, out DatabricksDecimal result)
    {
        result = Zero;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        var negative = false;
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        var pointIndex = span.IndexOf('.');
        ReadOnlySpan<char> integerPart, fractionPart;
        if (pointIndex < 0)
        {
            integerPart = span;
            fractionPart = [];
        }
        else
        {
            integerPart = span[..pointIndex];
            fractionPart = span[(pointIndex + 1)..];
        }

        if (integerPart.Length == 0 && fractionPart.Length == 0)
        {
            return false;
        }

        Span<char> digits = fractionPart.Length + integerPart.Length <= 256
            ? stackalloc char[integerPart.Length + fractionPart.Length]
            : new char[integerPart.Length + fractionPart.Length];
        integerPart.CopyTo(digits);
        fractionPart.CopyTo(digits[integerPart.Length..]);

        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        var unscaled = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        result = new DatabricksDecimal(negative ? -unscaled : unscaled, fractionPart.Length);
        return true;
    }

    /// <summary>Creates a value from a <see cref="decimal"/> without loss.</summary>
    public static DatabricksDecimal FromDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var unscaled = new BigInteger((uint)bits[2]) << 64
            | new BigInteger((uint)bits[1]) << 32
            | new BigInteger((uint)bits[0]);
        var scale = (bits[3] >> 16) & 0xFF;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        return new DatabricksDecimal(negative ? -unscaled : unscaled, scale);
    }

    /// <summary>Creates a value from a <see cref="SqlDecimal"/> without loss.</summary>
    public static DatabricksDecimal FromSqlDecimal(SqlDecimal value) => Parse(value.ToString());

    /// <summary>
    /// Converts to <see cref="decimal"/>.
    /// </summary>
    /// <exception cref="OverflowException">When the value does not fit in a <see cref="decimal"/>.</exception>
    public decimal ToDecimal() => decimal.Parse(ToString(), CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts to <see cref="SqlDecimal"/>.
    /// </summary>
    /// <exception cref="OverflowException">When the value exceeds 38 significant digits.</exception>
    public SqlDecimal ToSqlDecimal() => SqlDecimal.Parse(ToString());

    /// <summary>Returns a value with trailing fractional zeros removed (e.g. <c>1.50</c> → <c>1.5</c>).</summary>
    public DatabricksDecimal Normalize()
    {
        var (unscaled, scale) = (UnscaledValue, Scale);
        while (scale > 0 && !unscaled.IsZero && unscaled % 10 == 0)
        {
            unscaled /= 10;
            scale--;
        }

        if (unscaled.IsZero)
        {
            scale = 0;
        }

        return new DatabricksDecimal(unscaled, scale);
    }

    /// <summary>Returns the absolute value.</summary>
    public DatabricksDecimal Abs() => UnscaledValue.Sign < 0 ? new(-UnscaledValue, Scale) : this;

    /// <summary>
    /// Divides by <paramref name="divisor"/>, producing a result with exactly
    /// <paramref name="scale"/> fractional digits.
    /// </summary>
    /// <exception cref="DivideByZeroException">When <paramref name="divisor"/> is zero.</exception>
    /// <exception cref="ArgumentException">For unsupported rounding modes.</exception>
    public DatabricksDecimal Divide(
        DatabricksDecimal divisor, int scale, MidpointRounding rounding = MidpointRounding.ToEven)
    {
        if (divisor.IsZero)
        {
            throw new DivideByZeroException();
        }

        if (rounding is not (MidpointRounding.ToEven or MidpointRounding.AwayFromZero))
        {
            throw new ArgumentException($"Rounding mode '{rounding}' is not supported.", nameof(rounding));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(scale);

        // result = (a.U / 10^a.S) / (b.U / 10^b.S) = a.U * 10^(scale + b.S - a.S) / b.U, rounded.
        var exponent = scale + divisor.Scale - Scale;
        var numerator = UnscaledValue;
        var denominator = divisor.UnscaledValue;
        if (exponent >= 0)
        {
            numerator *= BigInteger.Pow(10, exponent);
        }
        else
        {
            denominator *= BigInteger.Pow(10, -exponent);
        }

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (!remainder.IsZero)
        {
            var doubledRemainder = BigInteger.Abs(remainder) * 2;
            var absDenominator = BigInteger.Abs(denominator);
            var resultNegative = numerator.Sign * denominator.Sign < 0;
            var roundAway = doubledRemainder > absDenominator
                || (doubledRemainder == absDenominator
                    && (rounding == MidpointRounding.AwayFromZero || !(quotient % 2).IsZero));
            if (roundAway)
            {
                quotient += resultNegative ? BigInteger.MinusOne : BigInteger.One;
            }
        }

        return new DatabricksDecimal(quotient, scale);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (Scale == 0)
        {
            return UnscaledValue.ToString(CultureInfo.InvariantCulture);
        }

        var negative = UnscaledValue.Sign < 0;
        var digits = BigInteger.Abs(UnscaledValue).ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(digits.Length + 3);
        if (negative)
        {
            builder.Append('-');
        }

        if (digits.Length <= Scale)
        {
            builder.Append("0.").Append('0', Scale - digits.Length).Append(digits);
        }
        else
        {
            builder
                .Append(digits, 0, digits.Length - Scale)
                .Append('.')
                .Append(digits, digits.Length - Scale, Scale);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    /// <remarks>Format strings are not supported; the invariant canonical form is always produced.</remarks>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool Equals(DatabricksDecimal other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DatabricksDecimal other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var normalized = Normalize();
        return HashCode.Combine(normalized.UnscaledValue, normalized.Scale);
    }

    /// <inheritdoc />
    public int CompareTo(DatabricksDecimal other)
    {
        var (left, right) = Align(this, other);
        return left.CompareTo(right);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        DatabricksDecimal other => CompareTo(other),
        _ => throw new ArgumentException($"Object must be of type {nameof(DatabricksDecimal)}.", nameof(obj)),
    };

    private static (BigInteger Left, BigInteger Right) Align(DatabricksDecimal a, DatabricksDecimal b)
        => a.Scale == b.Scale
            ? (a.UnscaledValue, b.UnscaledValue)
            : a.Scale < b.Scale
                ? (a.UnscaledValue * BigInteger.Pow(10, b.Scale - a.Scale), b.UnscaledValue)
                : (a.UnscaledValue, b.UnscaledValue * BigInteger.Pow(10, a.Scale - b.Scale));

    /// <summary>Adds two values; the result carries the larger scale.</summary>
    public static DatabricksDecimal operator +(DatabricksDecimal a, DatabricksDecimal b)
    {
        var (left, right) = Align(a, b);
        return new DatabricksDecimal(left + right, Math.Max(a.Scale, b.Scale));
    }

    /// <summary>Subtracts two values; the result carries the larger scale.</summary>
    public static DatabricksDecimal operator -(DatabricksDecimal a, DatabricksDecimal b)
    {
        var (left, right) = Align(a, b);
        return new DatabricksDecimal(left - right, Math.Max(a.Scale, b.Scale));
    }

    /// <summary>Multiplies two values; the result scale is the sum of the operand scales.</summary>
    public static DatabricksDecimal operator *(DatabricksDecimal a, DatabricksDecimal b)
        => new(a.UnscaledValue * b.UnscaledValue, a.Scale + b.Scale);

    /// <summary>Negates the value.</summary>
    public static DatabricksDecimal operator -(DatabricksDecimal value)
        => new(-value.UnscaledValue, value.Scale);

    /// <summary>Numeric equality (scale-insensitive).</summary>
    public static bool operator ==(DatabricksDecimal a, DatabricksDecimal b) => a.Equals(b);

    /// <summary>Numeric inequality (scale-insensitive).</summary>
    public static bool operator !=(DatabricksDecimal a, DatabricksDecimal b) => !a.Equals(b);

    /// <summary>Less-than comparison.</summary>
    public static bool operator <(DatabricksDecimal a, DatabricksDecimal b) => a.CompareTo(b) < 0;

    /// <summary>Less-than-or-equal comparison.</summary>
    public static bool operator <=(DatabricksDecimal a, DatabricksDecimal b) => a.CompareTo(b) <= 0;

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(DatabricksDecimal a, DatabricksDecimal b) => a.CompareTo(b) > 0;

    /// <summary>Greater-than-or-equal comparison.</summary>
    public static bool operator >=(DatabricksDecimal a, DatabricksDecimal b) => a.CompareTo(b) >= 0;

    /// <summary>Lossless conversion from <see cref="decimal"/>.</summary>
    public static implicit operator DatabricksDecimal(decimal value) => FromDecimal(value);

    /// <summary>Lossless conversion from <see cref="long"/>.</summary>
    public static implicit operator DatabricksDecimal(long value) => new(value, 0);

    /// <summary>Conversion to <see cref="decimal"/>; throws <see cref="OverflowException"/> when out of range.</summary>
    public static explicit operator decimal(DatabricksDecimal value) => value.ToDecimal();
}
