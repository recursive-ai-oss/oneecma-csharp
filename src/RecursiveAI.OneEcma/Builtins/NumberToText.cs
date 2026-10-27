using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Number.prototype.toFixed, toExponential and toPrecision (ECMA-262 21.1.3.3, 21.1.3.2, 21.1.3.5). Every digit comes from exact
/// integer arithmetic on the double's mantissa and exponent, so ties and long expansions are decided by the real value of x
/// (the spec's "if there are two such n, pick the larger n").
/// </summary>
internal static class NumberToText
{
    private static readonly System.Globalization.NumberFormatInfo Inv = System.Globalization.NumberFormatInfo.InvariantInfo;

    /// <summary>Splits a finite non-negative double into mantissa and binary exponent: x = m × 2^e.</summary>
    private static void Split(double x, out BigInteger m, out int e)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0)
        {
            m = frac;
            e = -1074;
        }
        else
        {
            m = frac | (1L << 52);
            e = exp - 1075;
        }
    }

    /// <summary>round-half-up(m × 2^e × 10^pow10), exactly.</summary>
    private static BigInteger RoundScaled(BigInteger m, int e, int pow10)
    {
        BigInteger num = m;
        BigInteger den = BigInteger.One;
        if (e >= 0)
        {
            num <<= e;
        }
        else
        {
            den <<= -e;
        }

        if (pow10 >= 0)
        {
            num *= BigInteger.Pow(10, pow10);
        }
        else
        {
            den *= BigInteger.Pow(10, -pow10);
        }

        return ((num * 2) + den) / (den * 2);
    }

    internal static string ToFixed(double x, int f)
    {
        if (!double.IsFinite(x) || Math.Abs(x) >= 1e21)
        {
            return Ops.NumberToStr(x);
        }

        bool neg = x < 0;
        Split(Math.Abs(x), out BigInteger m, out int e);
        BigInteger n = RoundScaled(m, e, f);
        string digits = n.ToString(Inv);
        if (f != 0)
        {
            if (digits.Length <= f)
            {
                digits = new string('0', f + 1 - digits.Length) + digits;
            }

            digits = digits[..^f] + "." + digits[^f..];
        }

        return neg ? "-" + digits : digits;
    }

    /// <summary>The sign of m × 2^e − 10^k, exactly.</summary>
    private static int CompareToPow10(BigInteger m, int e, int k)
    {
        BigInteger left = m;
        BigInteger right = BigInteger.One;
        if (e >= 0)
        {
            left <<= e;
        }
        else
        {
            right <<= -e;
        }

        if (k >= 0)
        {
            right *= BigInteger.Pow(10, k);
        }
        else
        {
            left *= BigInteger.Pow(10, -k);
        }

        return left.CompareTo(right);
    }

    /// <summary>Finds n and e with 10^(p-1) ≤ n &lt; 10^p and n × 10^(e-p+1) closest to x; x &gt; 0.</summary>
    private static string DigitsFor(double x, int p, out int e10)
    {
        Split(x, out BigInteger m, out int e);
        // The decimal exponent of x itself: 10^est <= x < 10^(est+1), decided exactly (rounding to p digits comes after).
        int est = (int)Math.Floor(Math.Log10(x));
        while (CompareToPow10(m, e, est + 1) >= 0)
        {
            est++;
        }

        while (CompareToPow10(m, e, est) < 0)
        {
            est--;
        }

        BigInteger n = RoundScaled(m, e, p - 1 - est);
        BigInteger hi = BigInteger.Pow(10, p);
        if (n >= hi)
        {
            // Rounding carried into a new digit: 9.99..9 rounded up to 10.0..0.
            est++;
            n /= 10;
        }

        e10 = est;
        return n.ToString(Inv);
    }

    /// <summary>toExponential; <paramref name="f"/> is -1 when fractionDigits is undefined (shortest round-trip digits).</summary>
    internal static string ToExponential(double x, int f)
    {
        var sb = new StringBuilder();
        if (x < 0)
        {
            sb.Append('-');
            x = -x;
        }

        string digits;
        int e;
        if (x == 0)
        {
            digits = new string('0', Math.Max(f, 0) + 1);
            e = 0;
        }
        else if (f < 0)
        {
            NumberConv.DigitsAndExponent(x.ToString("R", Inv), out digits, out int n);
            e = n - 1;
        }
        else
        {
            digits = DigitsFor(x, f + 1, out e);
        }

        sb.Append(digits[0]);
        if (digits.Length > 1)
        {
            sb.Append('.').Append(digits, 1, digits.Length - 1);
        }

        return sb.Append('e').Append(e >= 0 ? '+' : '-').Append(Math.Abs(e).ToString(Inv)).ToString();
    }

    internal static string ToPrecision(double x, int p)
    {
        var sb = new StringBuilder();
        if (x < 0)
        {
            sb.Append('-');
            x = -x;
        }

        string m;
        int e;
        if (x == 0)
        {
            m = new string('0', p);
            e = 0;
        }
        else
        {
            m = DigitsFor(x, p, out e);
        }

        if (e < -6 || e >= p)
        {
            sb.Append(m[0]);
            if (p != 1)
            {
                sb.Append('.').Append(m, 1, m.Length - 1);
            }

            return sb.Append('e').Append(e >= 0 ? '+' : '-').Append(Math.Abs(e).ToString(Inv)).ToString();
        }

        if (e == p - 1)
        {
            return sb.Append(m).ToString();
        }

        if (e >= 0)
        {
            return sb.Append(m, 0, e + 1).Append('.').Append(m, e + 1, m.Length - e - 1).ToString();
        }

        return sb.Append("0.").Append('0', -(e + 1)).Append(m).ToString();
    }
}
