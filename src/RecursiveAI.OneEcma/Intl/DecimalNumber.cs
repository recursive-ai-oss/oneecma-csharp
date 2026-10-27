using System.Globalization;
using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>The rounding modes of ECMA-402 NumberFormat v3 (<c>roundingMode</c>).</summary>
internal enum RoundingMode
{
    /// <summary>Toward positive infinity.</summary>
    Ceil,

    /// <summary>Toward negative infinity.</summary>
    Floor,

    /// <summary>Away from zero.</summary>
    Expand,

    /// <summary>Toward zero.</summary>
    Trunc,

    /// <summary>Nearest, ties toward positive infinity.</summary>
    HalfCeil,

    /// <summary>Nearest, ties toward negative infinity.</summary>
    HalfFloor,

    /// <summary>Nearest, ties away from zero (the default).</summary>
    HalfExpand,

    /// <summary>Nearest, ties toward zero.</summary>
    HalfTrunc,

    /// <summary>Nearest, ties to the even neighbour.</summary>
    HalfEven,
}

/// <summary>
/// An exact decimal value as ECMA-402 NumberFormat v3 handles it (ToIntlMathematicalValue): a sign, a
/// digit string and a power of ten, or NaN, an infinity, or negative zero. Doubles enter through their
/// shortest round-trip digits (<c>Number::toString</c>), BigInts and numeric strings exactly.
/// </summary>
internal readonly struct DecimalNumber
{
    /// <summary>Largest decimal exponent magnitude accepted from a numeric string, to bound memory.</summary>
    internal const int MaxExponent = 1_000_000;

    private DecimalNumber(bool negative, string digits, int exponent, byte kind)
    {
        IsNegative = negative;
        Digits = digits;
        Exponent = exponent;
        Kind = kind;
    }

    /// <summary>Not a number.</summary>
    internal static DecimalNumber NaN => new(false, "", 0, 1);

    /// <summary>Plus or minus infinity.</summary>
    internal static DecimalNumber Infinity(bool negative) => new(negative, "", 0, 2);

    /// <summary>Zero, optionally negative zero.</summary>
    internal static DecimalNumber Zero(bool negative = false) => new(negative, "", 0, 0);

    /// <summary>The sign bit: true for negative values and negative zero.</summary>
    internal bool IsNegative { get; }

    /// <summary>Significant digits with no leading or trailing zeros; empty for zero.</summary>
    internal string Digits { get; }

    /// <summary>The value is <c>Digits × 10^Exponent</c>.</summary>
    internal int Exponent { get; }

    private byte Kind { get; }

    /// <summary>True for NaN.</summary>
    internal bool IsNaN => Kind == 1;

    /// <summary>True for plus or minus infinity.</summary>
    internal bool IsInfinity => Kind == 2;

    /// <summary>True for finite values (including zero).</summary>
    internal bool IsFinite => Kind == 0;

    /// <summary>True for zero and negative zero.</summary>
    internal bool IsZero => Kind == 0 && Digits.Length == 0;

    /// <summary>floor(log10(|x|)) of a non-zero finite value.</summary>
    internal int Magnitude => Digits.Length + Exponent - 1;

    /// <summary>The same value with the opposite sign.</summary>
    internal DecimalNumber Negate() => new(!IsNegative, Digits, Exponent, Kind);

    /// <summary>The absolute value.</summary>
    internal DecimalNumber Abs() => new(false, Digits, Exponent, Kind);

    /// <summary>Multiplies by 10^<paramref name="shift"/>.</summary>
    internal DecimalNumber ScaleByPowerOfTen(int shift) => Kind != 0 || Digits.Length == 0 ? this : new(IsNegative, Digits, Exponent + shift, 0);

    /// <summary>Builds a value from digits (leading and trailing zeros are removed).</summary>
    internal static DecimalNumber FromDigits(bool negative, ReadOnlySpan<char> digits, int exponent)
    {
        int start = 0;
        while (start < digits.Length && digits[start] == '0')
        {
            start++;
        }

        int end = digits.Length;
        while (end > start && digits[end - 1] == '0')
        {
            end--;
            exponent++;
        }

        if (start == end)
        {
            return Zero(negative);
        }

        return new(negative, new string(digits[start..end]), exponent, 0);
    }

    /// <summary>A Number value: its shortest round-trip decimal digits, as <c>Number::toString</c> prints them.</summary>
    internal static DecimalNumber FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return NaN;
        }

        if (double.IsInfinity(value))
        {
            return Infinity(value < 0);
        }

        bool negative = double.IsNegative(value);
        if (value == 0)
        {
            return Zero(negative);
        }

        Span<char> buf = stackalloc char[40];
        // "R" is the shortest string that round-trips (.NET Core 3.0 and later), in either plain or E form.
        if (!Math.Abs(value).TryFormat(buf, out int n, "R", NumberFormatInfo.InvariantInfo))
        {
            throw new InvalidOperationException("double formatting overflow");
        }

        ReadOnlySpan<char> s = buf[..n];
        int exp = 0;
        int e = s.IndexOfAny('E', 'e');
        if (e >= 0)
        {
            exp = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo);
            s = s[..e];
        }

        Span<char> digits = stackalloc char[40];
        int dn = 0;
        int dot = s.IndexOf('.');
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '.')
            {
                digits[dn++] = s[i];
            }
        }

        if (dot >= 0)
        {
            exp -= s.Length - dot - 1;
        }

        return FromDigits(negative, digits[..dn], exp);
    }

    /// <summary>An integer value (a BigInt).</summary>
    internal static DecimalNumber FromBigInteger(BigInteger value)
    {
        bool negative = value.Sign < 0;
        return FromDigits(negative, BigInteger.Abs(value).ToString(NumberFormatInfo.InvariantInfo), 0);
    }

    /// <summary>
    /// Parses a StringNumericLiteral exactly (ECMA-262 7.1.4.1.1 grammar; ECMA-402 ToIntlMathematicalValue):
    /// decimal with optional exponent, <c>Infinity</c>, and <c>0x</c>/<c>0o</c>/<c>0b</c> integers. Anything
    /// else gives NaN; the empty string is zero.
    /// </summary>
    /// <exception cref="IntlException">The exponent is so large the result could not be held (RangeError).</exception>
    internal static DecimalNumber ParseNumericString(string text)
    {
        ReadOnlySpan<char> s = TrimJsWhitespace(text);
        if (s.Length == 0)
        {
            return Zero();
        }

        bool negative = false;
        int i = 0;
        if (s[0] is '+' or '-')
        {
            negative = s[0] == '-';
            i = 1;
        }
        else if (s.Length > 2 && s[0] == '0' && (s[1] | 0x20) is 'x' or 'o' or 'b')
        {
            int radix = (s[1] | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 };
            BigInteger acc = BigInteger.Zero;
            for (int k = 2; k < s.Length; k++)
            {
                int d = s[k] <= '9' ? s[k] - '0' : (s[k] | 0x20) - 'a' + 10;
                if (s[k] < '0' || d < 0 || d >= radix || (s[k] > '9' && (s[k] | 0x20) < 'a'))
                {
                    return NaN;
                }

                acc = (acc * radix) + d;
            }

            return FromBigInteger(acc);
        }

        ReadOnlySpan<char> rest = s[i..];
        if (rest.SequenceEqual("Infinity"))
        {
            return Infinity(negative);
        }

        var digits = new StringBuilder();
        int exp = 0;
        int p = 0;
        int intCount = 0, fracCount = 0;
        while (p < rest.Length && rest[p] is >= '0' and <= '9')
        {
            digits.Append(rest[p++]);
            intCount++;
        }

        if (p < rest.Length && rest[p] == '.')
        {
            p++;
            while (p < rest.Length && rest[p] is >= '0' and <= '9')
            {
                digits.Append(rest[p++]);
                fracCount++;
            }
        }

        if (intCount + fracCount == 0)
        {
            return NaN;
        }

        exp = -fracCount;
        if (p < rest.Length && (rest[p] | 0x20) == 'e')
        {
            p++;
            bool eneg = false;
            if (p < rest.Length && rest[p] is '+' or '-')
            {
                eneg = rest[p] == '-';
                p++;
            }

            int start = p;
            long ev = 0;
            while (p < rest.Length && rest[p] is >= '0' and <= '9')
            {
                ev = Math.Min(ev * 10 + (rest[p] - '0'), 10_000_000_000L);
                p++;
            }

            if (p == start)
            {
                return NaN;
            }

            if (ev > MaxExponent)
            {
                throw IntlException.Range("Number too large to format");
            }

            exp += (int)(eneg ? -ev : ev);
        }

        if (p != rest.Length)
        {
            return NaN;
        }

        DecimalNumber r = FromDigits(negative, digits.ToString(), exp);
        if (!r.IsZero && Math.Abs((long)r.Magnitude) > MaxExponent)
        {
            throw IntlException.Range("Number too large to format");
        }

        return r;
    }

    private static ReadOnlySpan<char> TrimJsWhitespace(string text)
    {
        int a = 0, b = text.Length;
        while (a < b && IsJsWhitespace(text[a]))
        {
            a++;
        }

        while (b > a && IsJsWhitespace(text[b - 1]))
        {
            b--;
        }

        return text.AsSpan(a, b - a);
    }

    private static bool IsJsWhitespace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A')
            or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    /// <summary>The value as a double (nearest; for tests and for the JS layer).</summary>
    internal double ToDouble()
    {
        if (IsNaN)
        {
            return double.NaN;
        }

        if (IsInfinity)
        {
            return IsNegative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (IsZero)
        {
            return IsNegative ? -0.0 : 0.0;
        }

        double v = double.Parse(Digits + "E" + Exponent.ToString(NumberFormatInfo.InvariantInfo), NumberStyles.Float, NumberFormatInfo.InvariantInfo);
        return IsNegative ? -v : v;
    }

    /// <summary>
    /// Compares the absolute values of two finite numbers.
    /// </summary>
    internal static int CompareMagnitude(DecimalNumber a, DecimalNumber b)
    {
        if (a.IsZero || b.IsZero)
        {
            return a.IsZero == b.IsZero ? 0 : a.IsZero ? -1 : 1;
        }

        if (a.Magnitude != b.Magnitude)
        {
            return a.Magnitude < b.Magnitude ? -1 : 1;
        }

        int n = Math.Max(a.Digits.Length, b.Digits.Length);
        for (int i = 0; i < n; i++)
        {
            char x = i < a.Digits.Length ? a.Digits[i] : '0';
            char y = i < b.Digits.Length ? b.Digits[i] : '0';
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>Numeric comparison of two finite numbers (negative zero equals zero).</summary>
    internal static int Compare(DecimalNumber a, DecimalNumber b)
    {
        bool an = a.IsNegative && !a.IsZero, bn = b.IsNegative && !b.IsZero;
        if (an != bn)
        {
            return an ? -1 : 1;
        }

        int c = CompareMagnitude(a, b);
        return an ? -c : c;
    }

    /// <summary>Plain-decimal text, for tests and diagnostics (no grouping).</summary>
    public override string ToString()
    {
        if (IsNaN)
        {
            return "NaN";
        }

        if (IsInfinity)
        {
            return IsNegative ? "-Infinity" : "Infinity";
        }

        string sign = IsNegative ? "-" : "";
        if (IsZero)
        {
            return sign + "0";
        }

        if (Exponent >= 0)
        {
            return sign + Digits + new string('0', Exponent);
        }

        int point = Digits.Length + Exponent;
        return point > 0
            ? sign + Digits[..point] + "." + Digits[point..]
            : sign + "0." + new string('0', -point) + Digits;
    }
}

/// <summary>A number rounded for display: integer and fraction digit strings, plus the rounding magnitude.</summary>
internal readonly struct RoundedDigits(string integer, string fraction, int roundingMagnitude)
{
    /// <summary>Integer digits (at least one digit; "0" for values below one).</summary>
    internal string Integer { get; } = integer;

    /// <summary>Fraction digits, possibly empty.</summary>
    internal string Fraction { get; } = fraction;

    /// <summary>The power of ten of the last digit the rounding kept (ECMA-402 [[RoundingMagnitude]]).</summary>
    internal int RoundingMagnitude { get; } = roundingMagnitude;

    /// <summary>True when every digit is zero.</summary>
    internal bool IsZero => Integer.AsSpan().Trim('0').Length == 0 && Fraction.AsSpan().Trim('0').Length == 0;

    /// <summary>The magnitude (floor log10) of the rounded value; 0 for zero.</summary>
    internal int Magnitude
    {
        get
        {
            ReadOnlySpan<char> i = Integer.AsSpan().TrimStart('0');
            if (i.Length > 0)
            {
                return i.Length - 1;
            }

            for (int k = 0; k < Fraction.Length; k++)
            {
                if (Fraction[k] != '0')
                {
                    return -(k + 1);
                }
            }

            return 0;
        }
    }
}

/// <summary>
/// The rounding algorithms of ECMA-402 section 15.5: ToRawPrecision, ToRawFixed (with a rounding
/// increment) and the unsigned rounding modes of GetUnsignedRoundingMode.
/// </summary>
internal static class DigitRounding
{
    // Unsigned rounding decisions after the sign has been applied to the mode (ECMA-402 GetUnsignedRoundingMode).
    private enum Unsigned
    {
        Infinity,
        Zero,
        HalfInfinity,
        HalfZero,
        HalfEven,
    }

    private static Unsigned ToUnsigned(RoundingMode mode, bool negative) => mode switch
    {
        RoundingMode.Ceil => negative ? Unsigned.Zero : Unsigned.Infinity,
        RoundingMode.Floor => negative ? Unsigned.Infinity : Unsigned.Zero,
        RoundingMode.Expand => Unsigned.Infinity,
        RoundingMode.Trunc => Unsigned.Zero,
        RoundingMode.HalfCeil => negative ? Unsigned.HalfZero : Unsigned.HalfInfinity,
        RoundingMode.HalfFloor => negative ? Unsigned.HalfInfinity : Unsigned.HalfZero,
        RoundingMode.HalfExpand => Unsigned.HalfInfinity,
        RoundingMode.HalfTrunc => Unsigned.HalfZero,
        _ => Unsigned.HalfEven,
    };

    /// <summary>
    /// Rounds |x| to a multiple of <c>increment × 10^position</c> and returns the quotient times the
    /// increment as a digit string without leading zeros ("0" for zero): the integer n with n×10^position ≈ |x|.
    /// </summary>
    private static string RoundToInteger(in DecimalNumber x, int position, RoundingMode mode, bool negative, int increment)
    {
        Unsigned u = ToUnsigned(mode, negative);
        string digits = x.Digits;
        int exp = x.Exponent;
        if (digits.Length == 0)
        {
            return "0";
        }

        if (increment > 1)
        {
            return RoundWithIncrement(digits, exp, position, u, increment);
        }

        if (exp >= position)
        {
            return exp - position == 0 ? digits : string.Concat(digits, new string('0', exp - position));
        }

        int drop = position - exp;
        string kept = drop >= digits.Length ? "" : digits[..^drop];
        // Digits are normalized (no trailing zero), so something non-zero is always dropped.
        int cmp; // comparison of the dropped part with one half
        if (drop > digits.Length)
        {
            cmp = -1;
        }
        else
        {
            char first = digits[digits.Length - drop];
            cmp = first > '5' ? 1 : first < '5' ? -1 : (digits.Length - drop + 1 < digits.Length ? 1 : 0);
        }

        bool up = u switch
        {
            Unsigned.Infinity => true,
            Unsigned.Zero => false,
            Unsigned.HalfInfinity => cmp >= 0,
            Unsigned.HalfZero => cmp > 0,
            _ => cmp > 0 || (cmp == 0 && kept.Length > 0 && ((kept[^1] - '0') & 1) == 1),
        };
        if (!up)
        {
            return kept.Length == 0 ? "0" : kept;
        }

        return Increment(kept);
    }

    private static string Increment(string kept)
    {
        if (kept.Length == 0)
        {
            return "1";
        }

        char[] c = kept.ToCharArray();
        int i = c.Length - 1;
        while (i >= 0 && c[i] == '9')
        {
            c[i] = '0';
            i--;
        }

        if (i < 0)
        {
            return "1" + new string(c);
        }

        c[i]++;
        return new string(c);
    }

    private static string RoundWithIncrement(string digits, int exp, int position, Unsigned u, int increment)
    {
        // value / 10^position = N / D with N = digits × 10^max(exp-position,0), D = 10^max(position-exp,0).
        BigInteger n = BigInteger.Parse(digits, NumberFormatInfo.InvariantInfo);
        BigInteger d = BigInteger.One;
        if (exp >= position)
        {
            n *= BigInteger.Pow(10, exp - position);
        }
        else
        {
            d = BigInteger.Pow(10, position - exp);
        }

        BigInteger unit = d * increment;
        BigInteger q = BigInteger.DivRem(n, unit, out BigInteger r);
        int cmp = (r * 2).CompareTo(unit);
        bool nonZero = !r.IsZero;
        bool up = u switch
        {
            Unsigned.Infinity => nonZero,
            Unsigned.Zero => false,
            Unsigned.HalfInfinity => cmp >= 0,
            Unsigned.HalfZero => cmp > 0,
            _ => cmp > 0 || (cmp == 0 && !q.IsEven),
        };
        if (up)
        {
            q += 1;
        }

        return (q * increment).ToString(NumberFormatInfo.InvariantInfo);
    }

    /// <summary>ToRawFixed (15.5.? ): rounds to <paramref name="maxFraction"/> places (multiple of the increment) and trims zeros down to <paramref name="minFraction"/>.</summary>
    internal static RoundedDigits ToRawFixed(in DecimalNumber x, int minFraction, int maxFraction, int increment, RoundingMode mode, bool negative)
    {
        string n = RoundToInteger(x, -maxFraction, mode, negative, increment);
        if (n.Length <= maxFraction)
        {
            n = new string('0', maxFraction - n.Length + 1) + n;
        }

        string integer = n[..(n.Length - maxFraction)];
        string fraction = maxFraction == 0 ? "" : n[(n.Length - maxFraction)..];
        int cut = maxFraction - minFraction;
        int end = fraction.Length;
        while (cut > 0 && end > 0 && fraction[end - 1] == '0')
        {
            end--;
            cut--;
        }

        return new RoundedDigits(integer, fraction[..end], -maxFraction);
    }

    /// <summary>ToRawPrecision: rounds to <paramref name="maxPrecision"/> significant digits and trims zeros down to <paramref name="minPrecision"/>.</summary>
    internal static RoundedDigits ToRawPrecision(in DecimalNumber x, int minPrecision, int maxPrecision, RoundingMode mode, bool negative)
    {
        int p = maxPrecision;
        string m;
        int e;
        if (x.IsZero)
        {
            m = new string('0', p);
            e = 0;
        }
        else
        {
            e = x.Magnitude;
            m = RoundToInteger(x, e - p + 1, mode, negative, 1);
            if (m.Length > p)
            {
                // Rounding carried into a new digit (999.9 -> 1000): one more integer digit, same count of significant ones.
                e++;
                m = m[..p];
            }
            else if (m.Length < p)
            {
                m = m.PadRight(p, '0');
            }
        }

        string integer, fraction;
        if (e >= p - 1)
        {
            integer = m + new string('0', e - p + 1);
            fraction = "";
        }
        else if (e >= 0)
        {
            integer = m[..(e + 1)];
            fraction = m[(e + 1)..];
        }
        else
        {
            integer = "0";
            fraction = new string('0', -(e + 1)) + m;
        }

        if (fraction.Length > 0 && maxPrecision > minPrecision)
        {
            int cut = maxPrecision - minPrecision;
            int end = fraction.Length;
            while (cut > 0 && end > 0 && fraction[end - 1] == '0')
            {
                end--;
                cut--;
            }

            fraction = fraction[..end];
        }

        return new RoundedDigits(integer, fraction, e - p + 1);
    }
}
