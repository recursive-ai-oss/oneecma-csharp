using System.Globalization;
using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Number-to-string and string-to-number conversions of ECMA-262 (6.1.6.1.20 Number::toString and 7.1.4.1.1 StringToNumber).</summary>
internal static class NumberConv
{
    private static readonly NumberFormatInfo Inv = NumberFormatInfo.InvariantInfo;

    /// <summary>Number::toString(x, 10): shortest round-trip digits, then the spec's exponent rules.</summary>
    internal static string ToString(double m)
    {
        if (double.IsNaN(m))
        {
            return "NaN";
        }

        if (m == 0)
        {
            return "0";
        }

        if (double.IsInfinity(m))
        {
            return m > 0 ? "Infinity" : "-Infinity";
        }

        if (m == Math.Floor(m) && Math.Abs(m) < 1e15)
        {
            return ((long)m).ToString(Inv);
        }

        bool neg = m < 0;
        string r = Math.Abs(m).ToString("R", Inv);
        DigitsAndExponent(r, out string digits, out int n);
        int k = digits.Length;
        var sb = new StringBuilder();
        if (neg)
        {
            sb.Append('-');
        }

        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            int e = n - 1;
            sb.Append(digits[0]);
            if (k > 1)
            {
                sb.Append('.').Append(digits, 1, k - 1);
            }

            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(Inv));
        }

        return sb.ToString();
    }

    /// <summary>Splits .NET's round-trip text into significant digits and the exponent n such that value = 0.digits × 10^n.</summary>
    internal static void DigitsAndExponent(string r, out string digits, out int n)
    {
        int exp = 0;
        int ePos = r.IndexOfAny(['E', 'e']);
        string mant = r;
        if (ePos >= 0)
        {
            exp = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, Inv);
            mant = r[..ePos];
        }

        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string all = intPart + frac;
        int pointPos = intPart.Length + exp;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }

        all = all[lead..];
        pointPos -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0)
        {
            all = "0";
        }

        digits = all;
        n = pointPos;
    }

    internal static bool IsJsWhitespace(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' or '\u00A0' or '\uFEFF' or '\u2028' or '\u2029' || (c > 127 && char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);

    internal static string TrimJs(string s)
    {
        int a = 0;
        int b = s.Length;
        while (a < b && IsJsWhitespace(s[a]))
        {
            a++;
        }

        while (b > a && IsJsWhitespace(s[b - 1]))
        {
            b--;
        }

        return a == 0 && b == s.Length ? s : s[a..b];
    }

    /// <summary>StringToNumber (7.1.4.1.1).</summary>
    internal static double StringToNumber(string str)
    {
        string s = TrimJs(str);
        if (s.Length == 0)
        {
            return 0;
        }

        if (s.Length > 2 && s[0] == '0')
        {
            int radix = s[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0)
            {
                double v = 0;
                for (int i = 2; i < s.Length; i++)
                {
                    int d = DigitValue(s[i]);
                    if (d < 0 || d >= radix)
                    {
                        return double.NaN;
                    }

                    v = (v * radix) + d;
                }

                return radix == 16 && v > 9007199254740992d ? ParseBig(s[2..], 16) : v;
            }
        }

        int p = 0;
        bool neg = false;
        if (s[0] is '+' or '-')
        {
            neg = s[0] == '-';
            p = 1;
        }

        if (string.CompareOrdinal(s, p, "Infinity", 0, 8) == 0 && s.Length == p + 8)
        {
            return neg ? double.NegativeInfinity : double.PositiveInfinity;
        }

        int start = p;
        int digits = 0;
        while (p < s.Length && s[p] is >= '0' and <= '9')
        {
            p++;
            digits++;
        }

        if (p < s.Length && s[p] == '.')
        {
            p++;
            while (p < s.Length && s[p] is >= '0' and <= '9')
            {
                p++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return double.NaN;
        }

        if (p < s.Length && s[p] is 'e' or 'E')
        {
            p++;
            if (p < s.Length && s[p] is '+' or '-')
            {
                p++;
            }

            int ed = 0;
            while (p < s.Length && s[p] is >= '0' and <= '9')
            {
                p++;
                ed++;
            }

            if (ed == 0)
            {
                return double.NaN;
            }
        }

        if (p != s.Length)
        {
            return double.NaN;
        }

        double result = double.Parse(s.AsSpan(start), NumberStyles.Float, Inv);
        return neg ? -result : result;
    }

    private static double ParseBig(string digits, int radix)
    {
        BigInteger b = BigInteger.Zero;
        foreach (char c in digits)
        {
            b = (b * radix) + DigitValue(c);
        }

        return (double)b;
    }

    internal static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>StringToBigInt (7.1.14); returns false when the text is not a StringIntegerLiteral.</summary>
    internal static bool TryStringToBigInt(string str, out BigInteger result)
    {
        string s = TrimJs(str);
        result = BigInteger.Zero;
        if (s.Length == 0)
        {
            return true;
        }

        int radix = 10;
        int p = 0;
        bool neg = false;
        if (s.Length > 2 && s[0] == '0' && s[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            radix = s[1] is 'x' or 'X' ? 16 : s[1] is 'o' or 'O' ? 8 : 2;
            p = 2;
        }
        else if (s[0] is '+' or '-')
        {
            neg = s[0] == '-';
            p = 1;
        }

        if (p >= s.Length)
        {
            return false;
        }

        BigInteger v = BigInteger.Zero;
        for (; p < s.Length; p++)
        {
            int d = DigitValue(s[p]);
            if (d < 0 || d >= radix)
            {
                return false;
            }

            v = (v * radix) + d;
        }

        result = neg ? -v : v;
        return true;
    }

    /// <summary>Number::toString for an arbitrary radix (used by Number.prototype.toString and BigInt).</summary>
    internal static string ToRadixString(double value, int radix)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0)
        {
            return ToString(value);
        }

        bool neg = value < 0;
        double v = Math.Abs(value);
        double ip = Math.Floor(v);
        double fp = v - ip;
        var sb = new StringBuilder();
        if (ip == 0)
        {
            sb.Append('0');
        }
        else
        {
            var tmp = new StringBuilder();
            BigInteger bi = new(ip);
            while (bi > 0)
            {
                tmp.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(bi % radix)]);
                bi /= radix;
            }

            sb.Append(tmp);
        }

        if (fp > 0)
        {
            sb.Append('.');
            double delta = Math.Max(0.5 * (BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v) + 1) - v), double.Epsilon);
            int count = 0;
            do
            {
                fp *= radix;
                delta *= radix;
                int d = (int)fp;
                sb.Append("0123456789abcdefghijklmnopqrstuvwxyz"[d]);
                fp -= d;
                if (fp > 0.5 || (fp == 0.5 && (d & 1) == 1))
                {
                    if (fp + delta > 1)
                    {
                        // Round up and propagate the carry.
                        int i = sb.Length - 1;
                        while (true)
                        {
                            if (sb[i] == '.')
                            {
                                break;
                            }

                            int dv = DigitValue(sb[i]) + 1;
                            if (dv < radix)
                            {
                                sb[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[dv];
                                break;
                            }

                            sb.Length = i;
                            i--;
                        }

                        break;
                    }
                }
            }
            while (fp >= delta && ++count < 1100);
        }

        return neg ? "-" + sb : sb.ToString();
    }
}
