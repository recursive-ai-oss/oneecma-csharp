namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>Grapheme_Cluster_Break values (UAX #29 Table 2).</summary>
internal enum GraphemeBreak : byte
{
    /// <summary>Any other character.</summary>
    Other,

    /// <summary>CR.</summary>
    CR,

    /// <summary>Control.</summary>
    Control,

    /// <summary>Extend.</summary>
    Extend,

    /// <summary>L (Hangul leading jamo).</summary>
    L,

    /// <summary>LF.</summary>
    LF,

    /// <summary>LV.</summary>
    LV,

    /// <summary>LVT.</summary>
    LVT,

    /// <summary>Prepend.</summary>
    Prepend,

    /// <summary>Regional_Indicator.</summary>
    RegionalIndicator,

    /// <summary>SpacingMark.</summary>
    SpacingMark,

    /// <summary>T (Hangul trailing jamo).</summary>
    T,

    /// <summary>V (Hangul vowel jamo).</summary>
    V,

    /// <summary>ZWJ.</summary>
    ZWJ,
}

/// <summary>Word_Break values (UAX #29 Table 3).</summary>
internal enum WordBreak : byte
{
    /// <summary>Any other character.</summary>
    Other,

    /// <summary>ALetter.</summary>
    ALetter,

    /// <summary>CR.</summary>
    CR,

    /// <summary>Double_Quote.</summary>
    DoubleQuote,

    /// <summary>Extend.</summary>
    Extend,

    /// <summary>ExtendNumLet.</summary>
    ExtendNumLet,

    /// <summary>Format.</summary>
    Format,

    /// <summary>Hebrew_Letter.</summary>
    HebrewLetter,

    /// <summary>Katakana.</summary>
    Katakana,

    /// <summary>LF.</summary>
    LF,

    /// <summary>MidLetter.</summary>
    MidLetter,

    /// <summary>MidNum.</summary>
    MidNum,

    /// <summary>MidNumLet.</summary>
    MidNumLet,

    /// <summary>Newline.</summary>
    Newline,

    /// <summary>Numeric.</summary>
    Numeric,

    /// <summary>Regional_Indicator.</summary>
    RegionalIndicator,

    /// <summary>Single_Quote.</summary>
    SingleQuote,

    /// <summary>WSegSpace.</summary>
    WSegSpace,

    /// <summary>ZWJ.</summary>
    ZWJ,
}

/// <summary>Sentence_Break values (UAX #29 Table 4).</summary>
internal enum SentenceBreak : byte
{
    /// <summary>Any other character.</summary>
    Other,

    /// <summary>ATerm.</summary>
    ATerm,

    /// <summary>CR.</summary>
    CR,

    /// <summary>Close.</summary>
    Close,

    /// <summary>Extend.</summary>
    Extend,

    /// <summary>Format.</summary>
    Format,

    /// <summary>LF.</summary>
    LF,

    /// <summary>Lower.</summary>
    Lower,

    /// <summary>Numeric.</summary>
    Numeric,

    /// <summary>OLetter.</summary>
    OLetter,

    /// <summary>SContinue.</summary>
    SContinue,

    /// <summary>Sep.</summary>
    Sep,

    /// <summary>Sp.</summary>
    Sp,

    /// <summary>STerm.</summary>
    STerm,

    /// <summary>Upper.</summary>
    Upper,
}

/// <summary>Indic_Conjunct_Break values (UAX #44), used by grapheme rule GB9c.</summary>
internal enum IndicConjunctBreak : byte
{
    /// <summary>Not part of a conjunct.</summary>
    None,

    /// <summary>Consonant.</summary>
    Consonant,

    /// <summary>Extend.</summary>
    Extend,

    /// <summary>Linker (a virama).</summary>
    Linker,
}

/// <summary>
/// The code point property tables of UAX #29 (grapheme, word and sentence boundaries), Extended_Pictographic
/// and Indic_Conjunct_Break, from the pinned UCD. Each lookup is a binary search over ranges.
/// </summary>
internal sealed class BreakData
{
    private readonly PropertyTable _grapheme;
    private readonly PropertyTable _word;
    private readonly PropertyTable _sentence;
    private readonly PropertyTable _pictographic;
    private readonly PropertyTable _incb;

    private BreakData(PropertyTable g, PropertyTable w, PropertyTable s, PropertyTable p, PropertyTable i)
    {
        _grapheme = g;
        _word = w;
        _sentence = s;
        _pictographic = p;
        _incb = i;
    }

    private sealed class PropertyTable(string[] names, int[] firsts, int[] lasts, byte[] values)
    {
        public string[] Names { get; } = names;

        public int[] Firsts { get; } = firsts;

        public int[] Lasts { get; } = lasts;

        public byte[] Values { get; } = values;

        public byte Lookup(int cp)
        {
            int lo = 0;
            int hi = Firsts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (cp < Firsts[mid])
                {
                    hi = mid - 1;
                }
                else if (cp > Lasts[mid])
                {
                    lo = mid + 1;
                }
                else
                {
                    return Values[mid];
                }
            }

            return 0;
        }
    }

    /// <summary>Parses a break-properties blob.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a breaks blob or a UCD value has no enum member.</exception>
    internal static BreakData Open(byte[] data)
    {
        BlobReader r = BlobReader.Open(data, BlobReader.KindBreaks);
        int count = r.Varint();
        var tables = new Dictionary<string, PropertyTable>(StringComparer.Ordinal);
        for (int p = 0; p < count; p++)
        {
            string name = r.String();
            var names = new string[r.Varint()];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = r.String();
            }

            int ranges = r.Varint();
            var firsts = new int[ranges];
            var lasts = new int[ranges];
            var values = new byte[ranges];
            int prevEnd = -1;
            for (int i = 0; i < ranges; i++)
            {
                firsts[i] = prevEnd + 1 + r.Varint();
                lasts[i] = firsts[i] + r.Varint();
                values[i] = r.Byte();
                prevEnd = lasts[i];
            }

            tables[name] = new PropertyTable(names, firsts, lasts, values);
        }

        // The blob numbers values in its own order; remap them to the enums so lookups are a cast.
        return new BreakData(
            Remap<GraphemeBreak>(tables["Grapheme_Cluster_Break"]),
            Remap<WordBreak>(tables["Word_Break"]),
            Remap<SentenceBreak>(tables["Sentence_Break"]),
            tables["Extended_Pictographic"],
            Remap<IndicConjunctBreak>(tables["Indic_Conjunct_Break"]));
    }

    private static PropertyTable Remap<TEnum>(PropertyTable table)
        where TEnum : struct, Enum
    {
        // Re-number so that value index == enum value, by building a table indexed through the names.
        var map = new byte[table.Names.Length];
        for (int i = 0; i < map.Length; i++)
        {
            if (!Enum.TryParse(table.Names[i].Replace("_", "", StringComparison.Ordinal), out TEnum e))
            {
                throw new InvalidDataException($"Unknown break property value '{table.Names[i]}'.");
            }

            map[i] = Convert.ToByte(e, null);
        }

        return new PropertyTable(table.Names, table.Firsts, table.Lasts, table.Values.Select(v => map[v]).ToArray());
    }

    /// <summary>Grapheme_Cluster_Break of a code point.</summary>
    internal GraphemeBreak GraphemeClusterBreak(int cp) => (GraphemeBreak)_grapheme.Lookup(cp);

    /// <summary>Word_Break of a code point.</summary>
    internal WordBreak WordBreakOf(int cp) => (WordBreak)_word.Lookup(cp);

    /// <summary>Sentence_Break of a code point.</summary>
    internal SentenceBreak SentenceBreakOf(int cp) => (SentenceBreak)_sentence.Lookup(cp);

    /// <summary>Whether a code point has the Extended_Pictographic property (emoji-data.txt).</summary>
    internal bool IsExtendedPictographic(int cp) => _pictographic.Lookup(cp) != 0;

    /// <summary>Indic_Conjunct_Break of a code point.</summary>
    internal IndicConjunctBreak IndicConjunctBreakOf(int cp) => (IndicConjunctBreak)_incb.Lookup(cp);
}
