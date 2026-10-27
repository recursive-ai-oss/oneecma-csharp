using System.Text;

namespace RecursiveAI.OneEcma.Unicode;

/// <summary>
/// The Unicode Default Case Conversion algorithm (Unicode 3.13) used by String.prototype.toLowerCase and toUpperCase
/// (ECMA-262 22.1.3.28 and 22.1.3.30): full mappings from SpecialCasing (unconditional entries), simple mappings
/// from UnicodeData, and the Final_Sigma context rule. Language-sensitive entries (tr, az, lt) are not applied;
/// they belong to the locale-aware variants.
/// </summary>
internal static class CaseMapping
{
    /// <summary>toUppercase of a string; lone surrogates pass through.</summary>
    internal static string ToUpper(string s) => Map(s, upper: true);

    /// <summary>toLowercase of a string, with the Final_Sigma rule.</summary>
    internal static string ToLower(string s) => Map(s, upper: false);

    private static string Map(string s, bool upper)
    {
        int i = 0;
        // Fast path: ASCII that needs no change returns the same instance.
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 0x80 || (upper ? c is >= 'a' and <= 'z' : c is >= 'A' and <= 'Z'))
            {
                break;
            }
        }

        if (i == s.Length)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 8);
        sb.Append(s, 0, i);
        while (i < s.Length)
        {
            char c = s[i];
            int cp = c;
            int width = 1;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(c, s[i + 1]);
                width = 2;
            }

            if (cp < 0x80)
            {
                sb.Append(upper ? (c is >= 'a' and <= 'z' ? (char)(c - 32) : c) : (c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
            }
            else if (!upper && cp == 0x03A3)
            {
                sb.Append(IsFinalSigma(s, i) ? '\u03C2' : '\u03C3');
            }
            else
            {
                Append(sb, cp, upper);
            }

            i += width;
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, int cp, bool upper)
    {
        ReadOnlySpan<int> keys = upper ? CaseData.UpperKeys : CaseData.LowerKeys;
        int idx = keys.BinarySearch(cp);
        if (idx < 0)
        {
            AppendCodePoint(sb, cp);
            return;
        }

        int v = (upper ? CaseData.UpperValues : CaseData.LowerValues)[idx];
        if (v >= 0)
        {
            AppendCodePoint(sb, v);
            return;
        }

        ReadOnlySpan<int> seq = upper ? CaseData.UpperSeq : CaseData.LowerSeq;
        int off = -v - 1;
        int len = seq[off];
        for (int k = 1; k <= len; k++)
        {
            AppendCodePoint(sb, seq[off + k]);
        }
    }

    private static void AppendCodePoint(StringBuilder sb, int cp)
    {
        if (cp < 0x10000)
        {
            sb.Append((char)cp);
        }
        else
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
    }

    /// <summary>
    /// Final_Sigma (Unicode Table 3-17): C is preceded by a sequence of a cased letter and then zero or more case-ignorable
    /// characters, and is not followed by a sequence of zero or more case-ignorable characters and then a cased letter.
    /// </summary>
    private static bool IsFinalSigma(string s, int index)
    {
        bool before = false;
        for (int i = index; i > 0;)
        {
            int cp = CodePointBefore(s, ref i);
            if (InRanges(CaseData.CaseIgnorableRanges, cp))
            {
                continue;
            }

            before = InRanges(CaseData.CasedRanges, cp);
            break;
        }

        if (!before)
        {
            return false;
        }

        for (int i = index + 1; i < s.Length;)
        {
            int cp = CodePointAt(s, ref i);
            if (InRanges(CaseData.CaseIgnorableRanges, cp))
            {
                continue;
            }

            return !InRanges(CaseData.CasedRanges, cp);
        }

        return true;
    }

    private static int CodePointBefore(string s, ref int i)
    {
        char lo = s[--i];
        if (char.IsLowSurrogate(lo) && i > 0 && char.IsHighSurrogate(s[i - 1]))
        {
            i--;
            return char.ConvertToUtf32(s[i], lo);
        }

        return lo;
    }

    private static int CodePointAt(string s, ref int i)
    {
        char hi = s[i++];
        if (char.IsHighSurrogate(hi) && i < s.Length && char.IsLowSurrogate(s[i]))
        {
            return char.ConvertToUtf32(hi, s[i++]);
        }

        return hi;
    }

    private static bool InRanges(ReadOnlySpan<int> ranges, int cp)
    {
        int lo = 0;
        int hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < ranges[mid * 2])
            {
                hi = mid - 1;
            }
            else if (cp > ranges[(mid * 2) + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
