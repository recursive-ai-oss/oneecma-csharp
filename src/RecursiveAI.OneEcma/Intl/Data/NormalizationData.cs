namespace RecursiveAI.OneEcma.Intl.Data;

/// <summary>
/// UAX #15 data from UCD 17.0.0: canonical combining classes, the fully expanded canonical and
/// compatibility decompositions, and the primary composites. Hangul syllables are handled by the
/// algorithm of Unicode section 3.12, so they are not in the tables.
/// </summary>
internal sealed class NormalizationData
{
    // Highest code point with a non-zero combining class is U+1E94A; the table covers everything below U+1F000.
    private const int CccLimit = 0x1F000;

    private readonly byte[] _ccc;
    private readonly Dictionary<int, int> _canonical;
    private readonly Dictionary<int, int> _compatibility;
    private readonly int[] _pool;
    private readonly Dictionary<long, int> _compose;
    private readonly HashSet<int> _composeSecond;
    private readonly HashSet<int> _excluded;

    private NormalizationData(
        byte[] ccc,
        Dictionary<int, int> canonical,
        Dictionary<int, int> compatibility,
        int[] pool,
        Dictionary<long, int> compose,
        HashSet<int> second,
        HashSet<int> excluded)
    {
        _ccc = ccc;
        _canonical = canonical;
        _compatibility = compatibility;
        _pool = pool;
        _compose = compose;
        _composeSecond = second;
        _excluded = excluded;
    }

    /// <summary>Parses a normalization blob.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a normalization blob.</exception>
    internal static NormalizationData Open(byte[] data)
    {
        BlobReader r = BlobReader.Open(data, BlobReader.KindNormalization);
        var ccc = new byte[CccLimit];
        int ranges = r.Varint();
        int prevEnd = -1;
        for (int i = 0; i < ranges; i++)
        {
            int first = prevEnd + 1 + r.Varint();
            int last = first + r.Varint();
            byte value = r.Byte();
            for (int c = first; c <= last; c++)
            {
                ccc[c] = value;
            }

            prevEnd = last;
        }

        int count = r.Varint();
        var oneStep = new Dictionary<int, (bool Compat, int[] Target)>(count);
        var excluded = new HashSet<int>();
        var compose = new Dictionary<long, int>();
        var second = new HashSet<int>();
        int cp = 0;
        for (int i = 0; i < count; i++)
        {
            cp += r.Varint();
            int flags = r.Byte();
            var target = new int[r.Byte()];
            for (int k = 0; k < target.Length; k++)
            {
                target[k] = r.Varint();
            }

            oneStep[cp] = ((flags & 1) != 0, target);
            if ((flags & 2) != 0)
            {
                excluded.Add(cp);
            }
            else if ((flags & 1) == 0 && target.Length == 2)
            {
                // UAX #15 section 3.11: primary composites are canonical pairs not excluded from composition.
                compose[Pair(target[0], target[1])] = cp;
                second.Add(target[1]);
            }
        }

        var pool = new List<int>();
        var canonical = new Dictionary<int, int>();
        var compat = new Dictionary<int, int>();
        foreach (int key in oneStep.Keys)
        {
            var canonicalOut = new List<int>();
            Expand(oneStep, key, false, canonicalOut);
            if (!oneStep[key].Compat)
            {
                canonical[key] = (pool.Count << 5) | canonicalOut.Count;
                pool.AddRange(canonicalOut);
            }

            var compatOut = new List<int>();
            Expand(oneStep, key, true, compatOut);
            compat[key] = (pool.Count << 5) | compatOut.Count;
            pool.AddRange(compatOut);
        }

        return new NormalizationData(ccc, canonical, compat, [.. pool], compose, second, excluded);
    }

    // Depth is bounded by the data (at most four levels), not by any input.
    private static void Expand(Dictionary<int, (bool Compat, int[] Target)> map, int cp, bool compat, List<int> output)
    {
        if (map.TryGetValue(cp, out var m) && (compat || !m.Compat))
        {
            foreach (int t in m.Target)
            {
                Expand(map, t, compat, output);
            }
        }
        else
        {
            output.Add(cp);
        }
    }

    private static long Pair(int first, int second) => ((long)first << 21) | (uint)second;

    /// <summary>Canonical_Combining_Class of a code point (0 for lone surrogates and unassigned code points).</summary>
    internal int CombiningClass(int cp) => (uint)cp < CccLimit ? _ccc[cp] : 0;

    /// <summary>
    /// The full decomposition of a code point other than a Hangul syllable: canonical, or compatibility
    /// (which includes the canonical mappings). Returns an empty span when the code point does not decompose.
    /// </summary>
    internal ReadOnlySpan<int> Decomposition(int cp, bool compatibility)
    {
        Dictionary<int, int> table = compatibility ? _compatibility : _canonical;
        if (!table.TryGetValue(cp, out int packed))
        {
            return default;
        }

        return _pool.AsSpan(packed >> 5, packed & 31);
    }

    /// <summary>The primary composite of a starter and a following character, or -1 (UAX #15 section 3.11, including Hangul).</summary>
    internal int Compose(int first, int second)
    {
        // Unicode 3.12: <L, V> -> LV and <LV, T> -> LVT.
        int l = first - HangulLBase;
        if ((uint)l < HangulLCount)
        {
            int v = second - HangulVBase;
            if ((uint)v < HangulVCount)
            {
                return HangulSBase + (((l * HangulVCount) + v) * HangulTCount);
            }

            return -1;
        }

        int s = first - HangulSBase;
        if ((uint)s < HangulSCount && s % HangulTCount == 0)
        {
            int t = second - HangulTBase;
            if (t > 0 && t < HangulTCount)
            {
                return first + t;
            }

            return -1;
        }

        return _compose.TryGetValue(Pair(first, second), out int composite) ? composite : -1;
    }

    /// <summary>True when the code point is excluded from NFC composition and has a canonical mapping (NFC_Quick_Check=No by exclusion).</summary>
    internal bool IsCompositionExcluded(int cp) => _excluded.Contains(cp);

    /// <summary>True when the code point can be the second character of a primary composite (NFC_Quick_Check=Maybe).</summary>
    internal bool CanComposeBackward(int cp) =>
        _composeSecond.Contains(cp) || (cp >= HangulVBase && cp < HangulVBase + HangulVCount) || (cp > HangulTBase && cp < HangulTBase + HangulTCount);

    internal const int HangulSBase = 0xAC00;
    internal const int HangulLBase = 0x1100;
    internal const int HangulVBase = 0x1161;
    internal const int HangulTBase = 0x11A7;
    internal const int HangulLCount = 19;
    internal const int HangulVCount = 21;
    internal const int HangulTCount = 28;
    internal const int HangulNCount = HangulVCount * HangulTCount;
    internal const int HangulSCount = HangulLCount * HangulNCount;
}
