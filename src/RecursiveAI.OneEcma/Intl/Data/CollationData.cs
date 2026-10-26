using System.Text;

namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>One collation element of the root table (UTS #10 section 3): three weights and the variable flag.</summary>
/// <param name="Primary">Primary weight; 0 means ignorable at level 1.</param>
/// <param name="Secondary">Secondary weight (accents).</param>
/// <param name="Tertiary">Tertiary weight (case and variants).</param>
/// <param name="Variable">True for elements marked <c>*</c> in the table (spaces, punctuation, symbols).</param>
internal readonly record struct CollationElement(int Primary, int Secondary, int Tertiary, bool Variable);

/// <summary>
/// The CLDR root collation (<c>allkeys_CLDR.txt</c>), default alternate handling non-ignorable, plus the
/// implicit-weight rules for code points the table does not list (Han, Tangut, Nushu, Khitan, unassigned).
/// The table is keyed on NFD text: a caller normalizes first, then calls <see cref="MatchLongest"/>.
/// Tailorings of the supported locales are in supplemental data (<c>collationTailoring</c>) as rule text.
/// </summary>
internal sealed class CollationData
{
    private readonly Dictionary<int, CollationElement[]> _singles;
    private readonly Dictionary<string, CollationElement[]> _contractions;
    private readonly HashSet<int> _contractionStarts;
    private readonly int _maxContraction;
    private readonly (int Base, int Shift, int Mode, int Origin)[] _classes;
    private readonly (int First, int Last, byte Class)[] _runs;

    private CollationData(
        string version,
        Dictionary<int, CollationElement[]> singles,
        Dictionary<string, CollationElement[]> contractions,
        HashSet<int> starts,
        int maxContraction,
        (int, int, int, int)[] classes,
        (int, int, byte)[] runs)
    {
        Version = version;
        _singles = singles;
        _contractions = contractions;
        _contractionStarts = starts;
        _maxContraction = maxContraction;
        _classes = classes;
        _runs = runs;
    }

    /// <summary>The UCA version of the table, for example <c>17.0.0</c>.</summary>
    internal string Version { get; }

    /// <summary>Number of explicit single-code-point entries.</summary>
    internal int SingleCount => _singles.Count;

    /// <summary>Number of contraction entries.</summary>
    internal int ContractionCount => _contractions.Count;

    /// <summary>Parses a collation blob.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a collation blob.</exception>
    internal static CollationData Open(byte[] data)
    {
        BlobReader r = BlobReader.Open(data, BlobReader.KindCollation);
        string version = r.String();
        var classes = new (int, int, int, int)[r.Varint()];
        for (int i = 0; i < classes.Length; i++)
        {
            int b = r.Byte() | (r.Byte() << 8);
            int shift = r.Byte();
            int mode = r.Byte();
            classes[i] = (b, shift, mode, r.Varint());
        }

        var runs = new (int, int, byte)[r.Varint()];
        int prevEnd = -1;
        for (int i = 0; i < runs.Length; i++)
        {
            int first = prevEnd + 1 + r.Varint();
            int last = first + r.Varint();
            runs[i] = (first, last, r.Byte());
            prevEnd = last;
        }

        int singleCount = r.Varint();
        var singles = new Dictionary<int, CollationElement[]>(singleCount);
        int cp = 0;
        for (int i = 0; i < singleCount; i++)
        {
            cp += r.Varint();
            singles[cp] = ReadElements(ref r);
        }

        int contractionCount = r.Varint();
        var contractions = new Dictionary<string, CollationElement[]>(contractionCount, StringComparer.Ordinal);
        var starts = new HashSet<int>();
        int maxLen = 1;
        for (int i = 0; i < contractionCount; i++)
        {
            int len = r.Byte();
            var key = new StringBuilder();
            for (int k = 0; k < len; k++)
            {
                int c = r.Varint();
                if (k == 0)
                {
                    starts.Add(c);
                }

                key.Append(char.ConvertFromUtf32(c));
            }

            maxLen = Math.Max(maxLen, len);
            contractions[key.ToString()] = ReadElements(ref r);
        }

        return new CollationData(version, singles, contractions, starts, maxLen, classes, runs);
    }

    private static CollationElement[] ReadElements(ref BlobReader r)
    {
        var ces = new CollationElement[r.Byte()];
        for (int i = 0; i < ces.Length; i++)
        {
            int p = r.Varint();
            int s = r.Varint();
            byte t = r.Byte();
            ces[i] = new CollationElement(p, s, t & 0x7F, (t & 0x80) != 0);
        }

        return ces;
    }

    /// <summary>
    /// Finds the collation elements of the longest table entry that starts at <c>text[0]</c> (UTS #10
    /// section 3.3, contiguous matches only). Code points the table lacks get implicit weights.
    /// </summary>
    /// <param name="text">Normalized (NFD) code points; must not be empty.</param>
    /// <param name="elements">The elements of the matched sequence.</param>
    /// <returns>How many code points of <paramref name="text"/> the match consumed (at least 1).</returns>
    internal int MatchLongest(ReadOnlySpan<int> text, out CollationElement[] elements)
    {
        if (_contractionStarts.Contains(text[0]))
        {
            for (int len = Math.Min(_maxContraction, text.Length); len >= 2; len--)
            {
                var key = new StringBuilder();
                foreach (int c in text[..len])
                {
                    key.Append(char.ConvertFromUtf32(c));
                }

                if (_contractions.TryGetValue(key.ToString(), out CollationElement[]? hit))
                {
                    elements = hit;
                    return len;
                }
            }
        }

        elements = _singles.TryGetValue(text[0], out CollationElement[]? one) ? one : ImplicitElements(text[0]);
        return 1;
    }

    /// <summary>Implicit weights for a code point without an entry (UTS #10 section 10.1.3, Table 16).</summary>
    internal CollationElement[] ImplicitElements(int cp)
    {
        var (b, shift, mode, origin) = _classes[0];
        int lo = 0;
        int hi = _runs.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (cp < _runs[mid].First)
            {
                hi = mid - 1;
            }
            else if (cp > _runs[mid].Last)
            {
                lo = mid + 1;
            }
            else
            {
                (b, shift, mode, origin) = _classes[_runs[mid].Class];
                break;
            }
        }

        int aaaa = b + (shift == 0 ? 0 : cp >> shift);
        int bbbb = (mode == 0 ? cp - origin : cp & 0x7FFF) | 0x8000;
        return [new CollationElement(aaaa, 0x20, 0x02, false), new CollationElement(bbbb, 0, 0, false)];
    }
}
