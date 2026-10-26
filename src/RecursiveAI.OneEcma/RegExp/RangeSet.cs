namespace RecursiveAI.OneEcma.RegExp;

/// <summary>
/// An immutable set of code points stored as sorted, non-adjacent inclusive ranges. The set algebra here is
/// what ECMA-262 22.2.2.9 calls union, intersection, subtraction and CharacterComplement on a CharSet.
/// </summary>
internal sealed class RangeSet
{
    internal const int MaxCodePoint = 0x10FFFF;

    internal static readonly RangeSet Empty = new([]);

    // Flat pairs: R[2i] is the first and R[2i+1] the last code point of range i.
    private readonly int[] _r;

    private RangeSet(int[] pairs)
    {
        _r = pairs;
    }

    /// <summary>Number of ranges.</summary>
    internal int RangeCount => _r.Length / 2;

    internal bool IsEmpty => _r.Length == 0;

    internal int First(int i) => _r[2 * i];

    internal int Last(int i) => _r[(2 * i) + 1];

    /// <summary>Total number of code points in the set.</summary>
    internal long Count()
    {
        long n = 0;
        for (int i = 0; i < _r.Length; i += 2)
        {
            n += _r[i + 1] - _r[i] + 1;
        }

        return n;
    }

    internal bool Contains(int cp)
    {
        int lo = 0;
        int hi = (_r.Length / 2) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < _r[2 * mid])
            {
                hi = mid - 1;
            }
            else if (cp > _r[(2 * mid) + 1])
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

    /// <summary>The single code point of a one-element set, or -1.</summary>
    internal int SingleOrMinusOne() => _r.Length == 2 && _r[0] == _r[1] ? _r[0] : -1;

    internal static RangeSet Single(int cp) => new([cp, cp]);

    internal static RangeSet Range(int lo, int hi) => new([lo, hi]);

    /// <summary>Wraps pairs that are already sorted and normalized (used by the table reader).</summary>
    internal static RangeSet FromNormalized(int[] pairs) => new(pairs);

    /// <summary>Builds a set from arbitrary ranges; sorts and merges them.</summary>
    internal static RangeSet FromRanges(List<(int First, int Last)> ranges)
    {
        if (ranges.Count == 0)
        {
            return Empty;
        }

        ranges.Sort(static (a, b) => a.First != b.First ? a.First.CompareTo(b.First) : a.Last.CompareTo(b.Last));
        var o = new List<int>(ranges.Count * 2);
        int curFirst = ranges[0].First;
        int curLast = ranges[0].Last;
        for (int i = 1; i < ranges.Count; i++)
        {
            var (f, l) = ranges[i];
            if (f <= curLast + 1)
            {
                if (l > curLast)
                {
                    curLast = l;
                }
            }
            else
            {
                o.Add(curFirst);
                o.Add(curLast);
                curFirst = f;
                curLast = l;
            }
        }

        o.Add(curFirst);
        o.Add(curLast);
        return new RangeSet(o.ToArray());
    }

    internal static RangeSet Union(RangeSet a, RangeSet b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        var list = new List<(int, int)>(a.RangeCount + b.RangeCount);
        a.AddTo(list);
        b.AddTo(list);
        return FromRanges(list);
    }

    internal static RangeSet Intersect(RangeSet a, RangeSet b)
    {
        var o = new List<int>();
        int i = 0;
        int j = 0;
        while (i < a._r.Length && j < b._r.Length)
        {
            int lo = Math.Max(a._r[i], b._r[j]);
            int hi = Math.Min(a._r[i + 1], b._r[j + 1]);
            if (lo <= hi)
            {
                o.Add(lo);
                o.Add(hi);
            }

            if (a._r[i + 1] < b._r[j + 1])
            {
                i += 2;
            }
            else
            {
                j += 2;
            }
        }

        return new RangeSet(o.ToArray());
    }

    /// <summary>The members of <paramref name="a"/> that are not members of <paramref name="b"/>.</summary>
    internal static RangeSet Subtract(RangeSet a, RangeSet b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return a;
        }

        var o = new List<int>();
        int j = 0;
        for (int i = 0; i < a._r.Length; i += 2)
        {
            int lo = a._r[i];
            int hi = a._r[i + 1];
            while (j < b._r.Length && b._r[j + 1] < lo)
            {
                j += 2;
            }

            int k = j;
            while (lo <= hi)
            {
                if (k >= b._r.Length || b._r[k] > hi)
                {
                    o.Add(lo);
                    o.Add(hi);
                    break;
                }

                if (b._r[k] > lo)
                {
                    o.Add(lo);
                    o.Add(b._r[k] - 1);
                }

                lo = b._r[k + 1] + 1;
                k += 2;
            }
        }

        return new RangeSet(o.ToArray());
    }

    /// <summary>Everything in 0..<paramref name="max"/> that is not in this set.</summary>
    internal RangeSet ComplementWithin(int max) => Subtract(Range(0, max), this);

    internal void AddTo(List<(int, int)> list)
    {
        for (int i = 0; i < _r.Length; i += 2)
        {
            list.Add((_r[i], _r[i + 1]));
        }
    }
}
