using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.TextServices;

/// <summary>Which UAX #29 boundary type to find.</summary>
internal enum SegmentGranularity
{
    /// <summary>Extended grapheme clusters (UAX #29 section 3).</summary>
    Grapheme,

    /// <summary>Words (UAX #29 section 4).</summary>
    Word,

    /// <summary>Sentences (UAX #29 section 5).</summary>
    Sentence,
}

/// <summary>
/// Text segmentation by UAX #29 (Unicode 17.0.0 data): grapheme clusters, words and sentences over UTF-16.
/// Every function reads forward from a boundary with a few fields of state, so segmenting a text is linear;
/// the one unbounded look-ahead (SB8) stops at the next sentence terminator, so it costs each character
/// once. Nothing allocates. Lone surrogates are treated as code points of class Other.
/// </summary>
/// <remarks>
/// Deliberate limits: boundaries are the default rules of UAX #29, without locale tailoring and without
/// the dictionary segmentation ICU applies to Han, Thai, Lao, Khmer and Burmese, so such text breaks the
/// way the plain rules say. No sentence-break suppression lists are applied (see docs/text-services.md).
/// </remarks>
internal static class TextSegmenter
{
    private static BreakData Data => CldrData.Breaks;

    /// <summary>
    /// Finds the end of the segment that starts at <paramref name="start"/>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">A boundary of the same granularity (0, or a value returned earlier).</param>
    /// <param name="granularity">The boundary type.</param>
    /// <param name="isWordLike">For words: true when the segment contains a letter, number, kana or ideograph (ECMA-402 <c>isWordLike</c>); false otherwise.</param>
    /// <returns>The end offset, greater than <paramref name="start"/> unless it is at the end of the text.</returns>
    internal static int NextBoundary(ReadOnlySpan<char> text, int start, SegmentGranularity granularity, out bool isWordLike)
    {
        isWordLike = false;
        if (start >= text.Length)
        {
            return text.Length;
        }

        return granularity switch
        {
            SegmentGranularity.Grapheme => NextGrapheme(text, start),
            SegmentGranularity.Word => NextWord(text, start, out isWordLike),
            _ => NextSentence(text, start),
        };
    }

    /// <summary>An enumerator over the segments of a text.</summary>
    internal static SegmentEnumerator Enumerate(ReadOnlySpan<char> text, SegmentGranularity granularity) => new(text, granularity);

    /// <summary>
    /// Finds the segment that contains a UTF-16 index (ECMA-402 <c>Segments.prototype.containing</c>), scanning from the start of the text.
    /// </summary>
    /// <returns>False when <paramref name="index"/> is outside the text.</returns>
    internal static bool TryFindSegment(ReadOnlySpan<char> text, int index, SegmentGranularity granularity, out int start, out int end, out bool isWordLike)
    {
        start = 0;
        end = 0;
        isWordLike = false;
        if (index < 0 || index >= text.Length)
        {
            return false;
        }

        while (start < text.Length)
        {
            end = NextBoundary(text, start, granularity, out isWordLike);
            if (index < end)
            {
                return true;
            }

            start = end;
        }

        return false;
    }

    // ---- Grapheme clusters -------------------------------------------------------------------------------

    private static int NextGrapheme(ReadOnlySpan<char> text, int start)
    {
        BreakData data = Data;
        int cp = Utf16.CodePointAt(text, start, out int len);
        int pos = start + len;
        GraphemeBreak prev = data.GraphemeClusterBreak(cp);
        int pict = data.IsExtendedPictographic(cp) ? 1 : 0;
        int incb = 0;
        UpdateIncb(data.IndicConjunctBreakOf(cp), ref incb);
        int ri = prev == GraphemeBreak.RegionalIndicator ? 1 : 0;
        while (pos < text.Length)
        {
            int next = Utf16.CodePointAt(text, pos, out int nlen);
            GraphemeBreak n = data.GraphemeClusterBreak(next);
            if (GraphemeBreakBetween(data, prev, n, next, pict, incb, ri))
            {
                return pos;
            }

            // Advance the state across the character we kept.
            pict = data.IsExtendedPictographic(next) ? 1 : (n == GraphemeBreak.Extend && pict == 1) ? 1 : (n == GraphemeBreak.ZWJ && pict == 1) ? 2 : 0;
            UpdateIncb(data.IndicConjunctBreakOf(next), ref incb);
            ri = n == GraphemeBreak.RegionalIndicator ? ri + 1 : 0;
            prev = n;
            pos += nlen;
        }

        return pos;
    }

    // GB9c state: 0 none, 1 = Consonant [Extend Linker]* (no linker yet), 2 = ... with at least one Linker.
    private static void UpdateIncb(IndicConjunctBreak b, ref int state)
    {
        switch (b)
        {
            case IndicConjunctBreak.Consonant:
                state = 1;
                break;
            case IndicConjunctBreak.Linker when state >= 1:
                state = 2;
                break;
            case IndicConjunctBreak.Extend when state >= 1:
                break;
            default:
                state = 0;
                break;
        }
    }

    private static bool GraphemeBreakBetween(BreakData data, GraphemeBreak prev, GraphemeBreak next, int nextCp, int pict, int incb, int ri)
    {
        // GB3
        if (prev == GraphemeBreak.CR && next == GraphemeBreak.LF)
        {
            return false;
        }

        // GB4, GB5
        if (prev is GraphemeBreak.Control or GraphemeBreak.CR or GraphemeBreak.LF
            || next is GraphemeBreak.Control or GraphemeBreak.CR or GraphemeBreak.LF)
        {
            return true;
        }

        // GB6, GB7, GB8: Hangul syllable sequences
        if (prev == GraphemeBreak.L && next is GraphemeBreak.L or GraphemeBreak.V or GraphemeBreak.LV or GraphemeBreak.LVT)
        {
            return false;
        }

        if (prev is GraphemeBreak.LV or GraphemeBreak.V && next is GraphemeBreak.V or GraphemeBreak.T)
        {
            return false;
        }

        if (prev is GraphemeBreak.LVT or GraphemeBreak.T && next == GraphemeBreak.T)
        {
            return false;
        }

        // GB9, GB9a, GB9b
        if (next is GraphemeBreak.Extend or GraphemeBreak.ZWJ or GraphemeBreak.SpacingMark || prev == GraphemeBreak.Prepend)
        {
            return false;
        }

        // GB9c: Consonant [Extend Linker]* Linker [Extend Linker]* x Consonant
        if (incb == 2 && data.IndicConjunctBreakOf(nextCp) == IndicConjunctBreak.Consonant)
        {
            return false;
        }

        // GB11: ExtPict Extend* ZWJ x ExtPict
        if (pict == 2 && data.IsExtendedPictographic(nextCp))
        {
            return false;
        }

        // GB12, GB13: regional indicators pair up from the start of the run.
        if (prev == GraphemeBreak.RegionalIndicator && next == GraphemeBreak.RegionalIndicator && (ri & 1) == 1)
        {
            return false;
        }

        // GB999
        return true;
    }

    // ---- Words -------------------------------------------------------------------------------------------

    private static bool IsAhLetter(WordBreak w) => w is WordBreak.ALetter or WordBreak.HebrewLetter;

    private static bool IsMidNumLetQ(WordBreak w) => w is WordBreak.MidNumLet or WordBreak.SingleQuote;

    private static bool IsNewline(WordBreak w) => w is WordBreak.Newline or WordBreak.CR or WordBreak.LF;

    private static bool IsIgnorable(WordBreak w) => w is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;

    private static int NextWord(ReadOnlySpan<char> text, int start, out bool isWordLike)
    {
        BreakData data = Data;
        int cp = Utf16.CodePointAt(text, start, out int len);
        int pos = start + len;
        WordBreak raw = data.WordBreakOf(cp);

        // p1 and p2 are the classes of the last two characters once Extend, Format and ZWJ are folded into
        // their base (WB4); a run that starts the text or follows a line break stands for itself.
        WordBreak p1 = IsIgnorable(raw) ? WordBreak.Other : raw;
        WordBreak p2 = WordBreak.Other;
        int ri = raw == WordBreak.RegionalIndicator ? 1 : 0;
        isWordLike = IsWordLikeChar(raw, cp);
        int connectors = raw == WordBreak.ExtendNumLet ? 1 : 0;
        while (pos < text.Length)
        {
            int next = Utf16.CodePointAt(text, pos, out int nlen);
            WordBreak n = data.WordBreakOf(next);
            if (WordBreakBetween(data, text, raw, n, next, pos + nlen, p1, p2, ri))
            {
                return pos;
            }

            if (!IsIgnorable(n))
            {
                p2 = p1;
                p1 = n;
                ri = n == WordBreak.RegionalIndicator ? ri + 1 : 0;
            }

            isWordLike |= IsWordLikeChar(n, next);

            // A run of two or more connectors ("__") counts as a word although a lone "_" does not.
            if (n == WordBreak.ExtendNumLet && ++connectors >= 2)
            {
                isWordLike = true;
            }

            raw = n;
            pos += nlen;
        }

        return pos;
    }

    private static bool WordBreakBetween(BreakData data, ReadOnlySpan<char> text, WordBreak prevRaw, WordBreak n, int nextCp, int afterNext, WordBreak p1, WordBreak p2, int ri)
    {
        // WB3
        if (prevRaw == WordBreak.CR && n == WordBreak.LF)
        {
            return false;
        }

        // WB3a, WB3b
        if (IsNewline(prevRaw) || IsNewline(n))
        {
            return true;
        }

        // WB3c
        if (prevRaw == WordBreak.ZWJ && data.IsExtendedPictographic(nextCp))
        {
            return false;
        }

        // WB3d
        if (prevRaw == WordBreak.WSegSpace && n == WordBreak.WSegSpace)
        {
            return false;
        }

        // WB4
        if (IsIgnorable(n))
        {
            return false;
        }

        // WB5
        if (IsAhLetter(p1) && IsAhLetter(n))
        {
            return false;
        }

        // WB6, WB7, WB7a-c, WB11, WB12 need the first real character after the next one.
        WordBreak after = n is WordBreak.MidLetter or WordBreak.MidNum or WordBreak.MidNumLet or WordBreak.SingleQuote or WordBreak.DoubleQuote
            ? ClassAfter(data, text, afterNext)
            : WordBreak.Other;

        if (IsAhLetter(p1) && (n == WordBreak.MidLetter || IsMidNumLetQ(n)) && IsAhLetter(after))
        {
            return false;
        }

        if (IsAhLetter(p2) && (p1 == WordBreak.MidLetter || IsMidNumLetQ(p1)) && IsAhLetter(n))
        {
            return false;
        }

        if (p1 == WordBreak.HebrewLetter && n == WordBreak.SingleQuote)
        {
            return false;
        }

        if (p1 == WordBreak.HebrewLetter && n == WordBreak.DoubleQuote && after == WordBreak.HebrewLetter)
        {
            return false;
        }

        if (p2 == WordBreak.HebrewLetter && p1 == WordBreak.DoubleQuote && n == WordBreak.HebrewLetter)
        {
            return false;
        }

        // WB8, WB9, WB10
        if (p1 == WordBreak.Numeric && (n == WordBreak.Numeric || IsAhLetter(n)))
        {
            return false;
        }

        if (IsAhLetter(p1) && n == WordBreak.Numeric)
        {
            return false;
        }

        // WB11, WB12
        if (p2 == WordBreak.Numeric && (p1 == WordBreak.MidNum || IsMidNumLetQ(p1)) && n == WordBreak.Numeric)
        {
            return false;
        }

        if (p1 == WordBreak.Numeric && (n == WordBreak.MidNum || IsMidNumLetQ(n)) && after == WordBreak.Numeric)
        {
            return false;
        }

        // WB13
        if (p1 == WordBreak.Katakana && n == WordBreak.Katakana)
        {
            return false;
        }

        // WB13a, WB13b
        if ((IsAhLetter(p1) || p1 is WordBreak.Numeric or WordBreak.Katakana or WordBreak.ExtendNumLet) && n == WordBreak.ExtendNumLet)
        {
            return false;
        }

        if (p1 == WordBreak.ExtendNumLet && (IsAhLetter(n) || n is WordBreak.Numeric or WordBreak.Katakana))
        {
            return false;
        }

        // WB15, WB16
        if (p1 == WordBreak.RegionalIndicator && n == WordBreak.RegionalIndicator && (ri & 1) == 1)
        {
            return false;
        }

        // WB999
        return true;
    }

    // The class of the first character at or after `from` that is not Extend, Format or ZWJ (WB4).
    private static WordBreak ClassAfter(BreakData data, ReadOnlySpan<char> text, int from)
    {
        for (int i = from; i < text.Length;)
        {
            int cp = Utf16.CodePointAt(text, i, out int len);
            WordBreak w = data.WordBreakOf(cp);
            if (!IsIgnorable(w))
            {
                return w;
            }

            i += len;
        }

        return WordBreak.Other;
    }

    // ECMA-402 isWordLike: the segment has a letter, a number, kana or an ideograph. Hiragana and Han
    // have no Word_Break value of their own, so they are recognized by the ranges below.
    private static bool IsWordLikeChar(WordBreak w, int cp) =>
        w is WordBreak.ALetter or WordBreak.HebrewLetter or WordBreak.Numeric or WordBreak.Katakana
        || (w == WordBreak.Other && IsHanOrHiragana(cp));

    private static bool IsHanOrHiragana(int cp) =>
        cp is (>= 0x3041 and <= 0x3096) or (>= 0x309D and <= 0x309F) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xF900 and <= 0xFAFF) or 0x3005 or 0x3007 or (>= 0x3021 and <= 0x3029) or (>= 0x3038 and <= 0x303B)
            or (>= 0x1B001 and <= 0x1B11F) or 0x1F200 or (>= 0x20000 and <= 0x323AF) or (>= 0x2F800 and <= 0x2FA1D);

    // ---- Sentences ---------------------------------------------------------------------------------------

    private static bool IsParaSep(SentenceBreak s) => s is SentenceBreak.Sep or SentenceBreak.CR or SentenceBreak.LF;

    private static bool IsSaTerm(SentenceBreak s) => s is SentenceBreak.ATerm or SentenceBreak.STerm;

    private static int NextSentence(ReadOnlySpan<char> text, int start)
    {
        BreakData data = Data;
        int cp = Utf16.CodePointAt(text, start, out int len);
        int pos = start + len;
        SentenceBreak raw = data.SentenceBreakOf(cp);

        // p1 is the class of the last character with Extend and Format folded into it (SB5).
        SentenceBreak p1 = raw is SentenceBreak.Extend or SentenceBreak.Format ? SentenceBreak.Other : raw;

        // 0 none; 1 SATerm; 2 SATerm Close+; 3 SATerm Close* Sp+ (SB8a to SB11 prefixes).
        int state = 0;
        bool termIsA = false;
        SentenceBreak beforeTerm = SentenceBreak.Other;
        if (IsSaTerm(p1))
        {
            state = 1;
            termIsA = p1 == SentenceBreak.ATerm;
        }

        while (pos < text.Length)
        {
            int next = Utf16.CodePointAt(text, pos, out int nlen);
            SentenceBreak n = data.SentenceBreakOf(next);

            // SB3, SB4
            if (raw == SentenceBreak.CR && n == SentenceBreak.LF)
            {
                goto Keep;
            }

            if (IsParaSep(raw))
            {
                return pos;
            }

            // SB5
            if (n is SentenceBreak.Extend or SentenceBreak.Format)
            {
                goto Keep;
            }

            // SB6
            if (p1 == SentenceBreak.ATerm && n == SentenceBreak.Numeric)
            {
                goto Keep;
            }

            // SB7
            if (p1 == SentenceBreak.ATerm && n == SentenceBreak.Upper && beforeTerm is SentenceBreak.Upper or SentenceBreak.Lower)
            {
                goto Keep;
            }

            if (state is >= 1 and <= 3)
            {
                // SB8: ATerm Close* Sp* x ( not(OLetter | Upper | Lower | ParaSep | SATerm) )* Lower
                if (termIsA && LowerFollows(data, text, pos))
                {
                    goto Keep;
                }

                // SB8a
                if (n is SentenceBreak.SContinue or SentenceBreak.ATerm or SentenceBreak.STerm)
                {
                    goto Keep;
                }

                // SB9
                if (state <= 2 && (n == SentenceBreak.Close || n == SentenceBreak.Sp || IsParaSep(n)))
                {
                    goto Keep;
                }

                // SB10
                if (n == SentenceBreak.Sp || IsParaSep(n))
                {
                    goto Keep;
                }

                // SB11
                return pos;
            }

            // SB998
        Keep:
            if (n is SentenceBreak.Extend or SentenceBreak.Format)
            {
                // Folded into the previous character: the state does not change.
            }
            else
            {
                if (IsSaTerm(n))
                {
                    beforeTerm = p1;
                    state = 1;
                    termIsA = n == SentenceBreak.ATerm;
                }
                else if (state is 1 or 2 && n == SentenceBreak.Close)
                {
                    state = 2;
                }
                else if (state is >= 1 and <= 3 && n == SentenceBreak.Sp)
                {
                    state = 3;
                }
                else
                {
                    state = 0;
                }

                p1 = n;
            }

            raw = n;
            pos += nlen;
        }

        return pos;
    }

    // SB8 look-ahead: skip everything that is not OLetter, Upper, Lower, ParaSep or SATerm; true if a Lower comes first.
    private static bool LowerFollows(BreakData data, ReadOnlySpan<char> text, int from)
    {
        for (int i = from; i < text.Length;)
        {
            int cp = Utf16.CodePointAt(text, i, out int len);
            SentenceBreak s = data.SentenceBreakOf(cp);
            if (s == SentenceBreak.Lower)
            {
                return true;
            }

            if (s is SentenceBreak.OLetter or SentenceBreak.Upper or SentenceBreak.Sep or SentenceBreak.CR or SentenceBreak.LF
                or SentenceBreak.ATerm or SentenceBreak.STerm)
            {
                return false;
            }

            i += len;
        }

        return false;
    }
}

/// <summary>One segment: UTF-16 offsets into the text, and for words whether it is word-like.</summary>
/// <param name="Start">Start offset.</param>
/// <param name="End">End offset (exclusive).</param>
/// <param name="IsWordLike">For word granularity: the segment has a letter, number, kana or ideograph.</param>
internal readonly record struct TextSegment(int Start, int End, bool IsWordLike);

/// <summary>Iterates the segments of a text. Obtain one from <see cref="TextSegmenter.Enumerate"/>; usable with <c>foreach</c>.</summary>
internal ref struct SegmentEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private readonly SegmentGranularity _granularity;
    private int _end;

    internal SegmentEnumerator(ReadOnlySpan<char> text, SegmentGranularity granularity)
    {
        _text = text;
        _granularity = granularity;
        _end = 0;
        Current = default;
    }

    /// <summary>The current segment.</summary>
    public TextSegment Current { get; private set; }

    /// <summary>Advances to the next segment.</summary>
    public bool MoveNext()
    {
        int start = _end;
        if (start >= _text.Length)
        {
            return false;
        }

        _end = TextSegmenter.NextBoundary(_text, start, _granularity, out bool wordLike);
        Current = new TextSegment(start, _end, wordLike);
        return true;
    }

    /// <summary>Allows <c>foreach</c>.</summary>
    public readonly SegmentEnumerator GetEnumerator() => this;
}
