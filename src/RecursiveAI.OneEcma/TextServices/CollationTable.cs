using System.Buffers;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.TextServices;

/// <summary>
/// One collation element in the collator's working scale (UTS #10 section 3): the root table's 16-bit
/// weights widened so that tailorings (UTS #35) can insert a weight between two neighbours. Primaries are
/// scaled by 4096 and secondaries and tertiaries by 256; a tailoring takes the next unit above its reference.
/// </summary>
/// <param name="Primary">Primary weight; 0 is ignorable at level 1.</param>
/// <param name="Secondary">Secondary weight.</param>
/// <param name="Tertiary">Tertiary weight as the table gives it (case and variant together).</param>
/// <param name="PlainTertiary">The tertiary weight without its case part, used when case is ordered on its own.</param>
/// <param name="Flags">Bit 0: variable (punctuation, UTS #10 section 4); bit 1: uppercase; bit 2: mixed case.</param>
internal readonly record struct Ce(uint Primary, uint Secondary, uint Tertiary, uint PlainTertiary, byte Flags)
{
    internal const byte VariableFlag = 1;
    internal const byte UpperFlag = 2;
    internal const byte MixedFlag = 4;
    internal const int PrimaryScale = 12;
    internal const int WeightScale = 8;

    /// <summary>True for punctuation and spaces, which "shifted" handling can ignore.</summary>
    internal bool IsVariable => (Flags & VariableFlag) != 0;

    /// <summary>True when the element comes from an uppercase letter.</summary>
    internal bool IsUpper => (Flags & UpperFlag) != 0;

    /// <summary>0 for lowercase or caseless, 1 for mixed (a contraction such as "Ch"), 2 for uppercase.</summary>
    internal int CaseClass => (Flags & UpperFlag) != 0 ? 2 : (Flags & MixedFlag) != 0 ? 1 : 0;

    /// <summary>The common secondary weight (0x20 in the root table) in working scale.</summary>
    internal static uint CommonSecondary => 0x20u << WeightScale;

    /// <summary>The common tertiary weight (0x02 in the root table) in working scale.</summary>
    internal static uint CommonTertiary => 0x02u << WeightScale;

    /// <summary>Widens a root table element.</summary>
    internal static Ce FromRoot(CollationElement e)
    {
        // Case follows the tertiary weight (UTS #10 DUCET): 8..0xC are the capital forms of 2..6, 0x1D of 0x1C
        // (squared), and normal-size kana (0xE, 0x11, 0x12) count as the capitals of small kana (0xD, 0xF, 0x10).
        uint plainValue = e.Tertiary switch
        {
            >= 8 and <= 0xC => (uint)e.Tertiary - 6,
            0xE => 0xD,
            0x11 => 0xF,
            0x12 => 0x10,
            0x1D => 0x1C,
            _ => 0,
        };
        bool upper = plainValue != 0;
        uint plain = (upper ? plainValue : (uint)e.Tertiary) << WeightScale;
        byte flags = (byte)((e.Variable ? VariableFlag : 0) | (upper ? UpperFlag : 0));
        return new Ce((uint)e.Primary << PrimaryScale, (uint)e.Secondary << WeightScale, (uint)e.Tertiary << WeightScale, plain, flags);
    }
}

/// <summary>A growable list of elements backed by pooled arrays.</summary>
internal sealed class CeBuffer : IDisposable
{
    private Ce[] _items = ArrayPool<Ce>.Shared.Rent(64);

    /// <summary>Number of elements.</summary>
    internal int Count { get; private set; }

    /// <summary>The elements.</summary>
    internal ReadOnlySpan<Ce> Span => _items.AsSpan(0, Count);

    /// <summary>Mutable view of the elements.</summary>
    internal Span<Ce> MutableSpan => _items.AsSpan(0, Count);

    /// <summary>Appends an element.</summary>
    internal void Add(Ce ce)
    {
        if (Count == _items.Length)
        {
            Ce[] bigger = ArrayPool<Ce>.Shared.Rent(_items.Length * 2);
            Array.Copy(_items, bigger, Count);
            ArrayPool<Ce>.Shared.Return(_items);
            _items = bigger;
        }

        _items[Count++] = ce;
    }

    /// <summary>Removes all elements.</summary>
    internal void Clear() => Count = 0;

    /// <inheritdoc />
    public void Dispose()
    {
        Ce[] items = _items;
        _items = [];
        if (items.Length > 0)
        {
            ArrayPool<Ce>.Shared.Return(items);
        }
    }
}

/// <summary>
/// The collation element table of one collation: the root table plus the entries that tailoring rules
/// defined. Entries are keyed on NFD text. <see cref="Append"/> implements the matching of UTS #10
/// section 3.3 (S2.1.1 to S2.1.3), including contractions that skip unblocked non-starters.
/// </summary>
internal sealed class CollationTable
{
    // Contractions are at most this many code points (the root table has none longer).
    private const int MaxContraction = CollationData.MaxPackedLength;

    // Bound on how many non-starters after a contraction start are examined for a discontiguous match,
    // so that a hostile run of combining marks cannot make matching quadratic. Real text has a few.
    private const int MaxDiscontiguousScan = 64;

    private readonly CollationData _root;
    private readonly NormalizationData _normalization = CldrData.Normalization;
    private readonly Dictionary<int, Ce[]> _singles = [];
    private readonly Dictionary<ulong, Ce[]> _contractions = [];
    private readonly HashSet<int> _starts = [];
    private readonly HashSet<int> _suppressed = [];

    internal CollationTable(CollationData root) => _root = root;

    /// <summary>The root table underneath the tailorings.</summary>
    internal CollationData Root => _root;

    /// <summary>Defines (or replaces) the elements of an NFD sequence of one to three code points.</summary>
    internal void Define(ReadOnlySpan<int> key, Ce[] elements)
    {
        if (key.Length is 0 or > MaxContraction)
        {
            throw new NotSupportedException($"A tailored sequence of {key.Length} code points is not supported (1 to {MaxContraction}).");
        }

        if (key.Length == 1)
        {
            _singles[key[0]] = elements;
            return;
        }

        _contractions[CollationData.PackKey(key)] = elements;
        _starts.Add(key[0]);
    }

    /// <summary>Removes every root contraction that starts with the given code point ([suppressContractions]).</summary>
    internal void SuppressRootContractions(int firstCodePoint) => _suppressed.Add(firstCodePoint);

    private bool TryContraction(ReadOnlySpan<int> key, out Ce[]? tailored, out CollationElement[]? root)
    {
        root = null;
        if (_contractions.TryGetValue(CollationData.PackKey(key), out tailored))
        {
            return true;
        }

        if (!_suppressed.Contains(key[0]) && _root.TryGetContraction(key, out CollationElement[] r))
        {
            root = r;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Appends the elements of NFD text. The span is reordered in place when a non-starter joins a
    /// contraction that it does not directly follow (S2.1.3 removes it from its position).
    /// </summary>
    internal void Append(Span<int> cps, CeBuffer output)
    {
        int n = cps.Length;
        int i = 0;
        Span<int> key = stackalloc int[MaxContraction];
        while (i < n)
        {
            int cp = cps[i];
            int len = 1;
            Ce[]? tailored = null;
            CollationElement[]? rootElements = null;
            bool starts = _starts.Contains(cp) || (_root.IsContractionStart(cp) && !_suppressed.Contains(cp));
            bool matched = false;
            if (starts)
            {
                for (int l = Math.Min(MaxContraction, n - i); l >= 2; l--)
                {
                    if (TryContraction(cps.Slice(i, l), out tailored, out rootElements))
                    {
                        len = l;
                        matched = true;
                        break;
                    }
                }
            }

            if (!matched && !_singles.TryGetValue(cp, out tailored) && !_root.TryGetSingle(cp, out rootElements))
            {
                rootElements = _root.ImplicitElements(cp);
            }

            if (starts && len < MaxContraction)
            {
                // S2.1.2/S2.1.3: a following non-starter C that is not blocked from S joins it when S+C is in the table.
                int j = i + len;
                int lastSkipped = 0;
                int scanned = 0;
                while (j < n && len < MaxContraction && scanned++ < MaxDiscontiguousScan)
                {
                    int c = cps[j];
                    int cc = _normalization.CombiningClass(c);
                    if (cc == 0)
                    {
                        break;
                    }

                    if (cc > lastSkipped)
                    {
                        cps.Slice(i, len).CopyTo(key);
                        key[len] = c;
                        if (TryContraction(key[..(len + 1)], out Ce[]? t2, out CollationElement[]? r2))
                        {
                            // Move C next to S; the characters it skipped keep their order after it.
                            for (int k = j; k > i + len; k--)
                            {
                                cps[k] = cps[k - 1];
                            }

                            cps[i + len] = c;
                            len++;
                            j++;
                            tailored = t2;
                            rootElements = r2;
                            continue;
                        }
                    }

                    lastSkipped = cc;
                    j++;
                }
            }

            if (tailored is not null)
            {
                foreach (Ce ce in tailored)
                {
                    output.Add(ce);
                }
            }
            else
            {
                foreach (CollationElement e in rootElements!)
                {
                    output.Add(Ce.FromRoot(e));
                }
            }

            i += len;
        }
    }

    /// <summary>Elements of a (decomposed) string under this table, as UTS #35 resets and relations see them.</summary>
    internal Ce[] ElementsOf(ReadOnlySpan<char> text)
    {
        using var cps = new CodePointBuffer(text.Length + 4);
        Normalizer.Decompose(text, false, cps);
        using var ces = new CeBuffer();
        Append(cps.Span, ces);
        return ces.Span.ToArray();
    }
}
