namespace RecursiveAI.OneEcma.Unicode;

/// <summary>
/// Character classes the lexer needs, answered from the generated Unicode tables rather than the BCL's
/// categories (which follow the runtime's Unicode version, not ours).
/// </summary>
internal static class CharClass
{
    private static readonly bool[] AsciiIdStart = BuildAscii(start: true);
    private static readonly bool[] AsciiIdContinue = BuildAscii(start: false);

    /// <summary>ECMA-262 12.7 <c>IdentifierStartChar</c>: UnicodeIDStart, <c>$</c>, <c>_</c>.</summary>
    internal static bool IsIdStart(int cp) =>
        cp < 128 ? AsciiIdStart[cp] : InRanges(UnicodeTables.IdStartRanges, cp);

    /// <summary>ECMA-262 12.7 <c>IdentifierPartChar</c>: UnicodeIDContinue, <c>$</c>, ZWNJ, ZWJ (and <c>_</c>, which ID_Continue includes).</summary>
    internal static bool IsIdContinue(int cp) =>
        cp < 128 ? AsciiIdContinue[cp] : cp is 0x200C or 0x200D || InRanges(UnicodeTables.IdContinueRanges, cp);

    /// <summary>ECMA-262 12.2 <c>WhiteSpace</c>: TAB, VT, FF, SP, NBSP, ZWNBSP and any Zs code point.</summary>
    internal static bool IsWhiteSpace(char c) =>
        c is ' ' or '\t' or '\v' or '\f' or '\u00A0' or '\uFEFF' || (c >= 0x1680 && InRanges(UnicodeTables.SpaceSeparatorRanges, c));

    private static bool InRanges(ReadOnlySpan<int> ranges, int cp)
    {
        int lo = 0;
        int hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
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

    private static bool[] BuildAscii(bool start)
    {
        var t = new bool[128];
        for (int c = 0; c < 128; c++)
        {
            bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            bool digit = c >= '0' && c <= '9';
            t[c] = letter || c == '$' || c == '_' || (!start && digit);
        }

        return t;
    }
}
