using System.Text;

namespace RecursiveAI.OneEcma.RegExp;

/// <summary>How a <c>\p{..}</c> expression resolved: which kind of table and which entry.</summary>
internal readonly record struct PropertyRef(PropertyKind Kind, int Index)
{
    /// <summary>True for the binary properties of strings (ECMA-262 Table 66), which can only appear with the v flag.</summary>
    internal bool IsStrings => Kind == PropertyKind.Strings;
}

internal enum PropertyKind
{
    /// <summary>A code point set: General_Category value or binary property.</summary>
    Set,
    Script,
    ScriptExtensions,
    Strings,
}

/// <summary>
/// Unicode data behind the RegExp engine, decoded lazily from the embedded table: property sets, simple case
/// folding and the uppercase mapping used by Canonicalize without the u or v flag. See docs/regexp.md for the
/// table layout and the generator that writes it.
/// </summary>
internal static class RegExpUnicode
{
    private const string ResourceName = "oneecma-regexp/regexp-unicode.bin";

    private static readonly Lazy<UnicodeTables> TablesLazy = new(UnicodeTables.Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<char[]> FoldBmp = new(() => BuildBmp(Data.Fold), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<char[]> UpperBmp = new(() => BuildBmp(Data.Upper), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RangeSet> FoldKeys = new(() => KeysOf(Data.Fold), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RangeSet> AffectedUnicode = new(() => AffectedOf(Data.Fold), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RangeSet> AffectedBmp = new(() => AffectedOf(Data.Upper), LazyThreadSafetyMode.ExecutionAndPublication);

    private static UnicodeTables Data => TablesLazy.Value;

    /// <summary>
    /// Canonicalize (ECMA-262 22.2.2.7.3) when the ignoreCase flag is set: simple case folding for u and v
    /// patterns, the toUppercase-based mapping on code units otherwise.
    /// </summary>
    internal static int Canonicalize(int ch, bool unicode)
    {
        if (unicode)
        {
            if (ch < 0x80)
            {
                return ch is >= 'A' and <= 'Z' ? ch + 32 : ch;
            }

            return ch <= 0xFFFF ? FoldBmp.Value[ch] : Lookup(Data.Fold, ch);
        }

        return ch <= 0xFFFF ? UpperBmp.Value[ch] : ch;
    }

    /// <summary>The Canonicalize result for every BMP code point, as a table the matcher indexes directly.</summary>
    internal static char[] CanonicalizeTable(bool unicode) => unicode ? FoldBmp.Value : UpperBmp.Value;

    /// <summary>scf(cp) from CaseFolding.txt (statuses C and S).</summary>
    internal static int SimpleFold(int cp) => Canonicalize(cp, unicode: true);

    /// <summary>True if some other code point has the same Canonicalize result as <paramref name="cp"/>.</summary>
    internal static bool HasCaseVariants(int cp, bool unicode) => (unicode ? AffectedUnicode.Value : AffectedBmp.Value).Contains(cp);

    /// <summary>
    /// The set A plus the canonical form of each of its members. A character matches the original set under
    /// Canonicalize exactly when its canonical form is in the result (22.2.2.7.1 CharacterSetMatcher step l).
    /// </summary>
    internal static RangeSet AddCanonicalImages(RangeSet a, bool unicode)
    {
        var pairs = unicode ? Data.Fold : Data.Upper;
        List<(int, int)>? add = null;
        for (int i = 0; i < pairs.Keys.Length; i++)
        {
            if (a.Contains(pairs.Keys[i]))
            {
                int v = pairs.Values[i];
                if (!a.Contains(v))
                {
                    (add ??= []).Add((v, v));
                }
            }
        }

        return add is null ? a : RangeSet.Union(a, RangeSet.FromRanges(add));
    }

    /// <summary>MaybeSimpleCaseFolding (22.2.2.9.5) for a set of single code points: maps each member to scf(member).</summary>
    internal static RangeSet SimpleFoldSet(RangeSet a)
    {
        var pairs = Data.Fold;
        List<(int, int)>? remove = null;
        List<(int, int)>? add = null;
        for (int i = 0; i < pairs.Keys.Length; i++)
        {
            int k = pairs.Keys[i];
            if (a.Contains(k))
            {
                (remove ??= []).Add((k, k));
                (add ??= []).Add((pairs.Values[i], pairs.Values[i]));
            }
        }

        if (remove is null)
        {
            return a;
        }

        return RangeSet.Union(RangeSet.Subtract(a, RangeSet.FromRanges(remove)), RangeSet.FromRanges(add!));
    }

    /// <summary>AllCharacters (22.2.2.9.4) for the given flag combination.</summary>
    internal static RangeSet AllCharacters(bool unicode, bool unicodeSets, bool ignoreCase)
    {
        if (unicodeSets && ignoreCase)
        {
            // Code points that do not have a simple case folding mapping, i.e. scf(c) = c.
            return FoldKeys.Value.ComplementWithin(RangeSet.MaxCodePoint);
        }

        return RangeSet.Range(0, unicode || unicodeSets ? RangeSet.MaxCodePoint : 0xFFFF);
    }

    /// <summary>WordCharacters (22.2.2.9.3): the ASCII word characters plus, for u/v with i, those that fold onto them.</summary>
    internal static RangeSet WordCharacters(bool unicode, bool ignoreCase)
    {
        var basic = RangeSet.FromRanges([('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z')]);
        if (!ignoreCase)
        {
            return basic;
        }

        var pairs = unicode ? Data.Fold : Data.Upper;
        List<(int, int)>? extra = null;
        for (int i = 0; i < pairs.Keys.Length; i++)
        {
            int k = pairs.Keys[i];
            if (!basic.Contains(k) && basic.Contains(pairs.Values[i]))
            {
                (extra ??= []).Add((k, k));
            }
        }

        return extra is null ? basic : RangeSet.Union(basic, RangeSet.FromRanges(extra));
    }

    /// <summary>ID_Start or ID_Continue, for group names (RegExpIdentifierStart / RegExpIdentifierPart).</summary>
    internal static bool IsIdentifierChar(int cp, bool start)
    {
        if (cp == '$' || cp == '_')
        {
            return true;
        }

        if (!start && (cp == 0x200C || cp == 0x200D))
        {
            return true;
        }

        if (cp < 0x80)
        {
            return cp is >= 'a' and <= 'z' or >= 'A' and <= 'Z' || (!start && cp is >= '0' and <= '9');
        }

        return GetSet(Data.BinaryByName[start ? "ID_Start" : "ID_Continue"]).Contains(cp);
    }

    /// <summary>
    /// Resolves the text of a <c>\p{..}</c> or <c>\P{..}</c> expression. Names are exact: the spec allows no loose
    /// matching, no Is prefix and only the aliases of its tables.
    /// </summary>
    internal static bool TryResolveProperty(string name, string? value, bool unicodeSets, out PropertyRef result)
    {
        result = default;
        var t = Data;
        if (value is not null)
        {
            Dictionary<string, int>? names = name switch
            {
                "General_Category" or "gc" => t.GcByName,
                "Script" or "sc" => t.ScriptByName,
                "Script_Extensions" or "scx" => t.ScriptByName,
                _ => null,
            };
            if (names is null || !names.TryGetValue(value, out int idx))
            {
                return false;
            }

            result = new PropertyRef(name is "General_Category" or "gc" ? PropertyKind.Set : name is "Script" or "sc" ? PropertyKind.Script : PropertyKind.ScriptExtensions, idx);
            return true;
        }

        if (t.GcByName.TryGetValue(name, out int g))
        {
            result = new PropertyRef(PropertyKind.Set, g);
            return true;
        }

        if (t.BinaryByName.TryGetValue(name, out var b))
        {
            result = b;
            return true;
        }

        if (unicodeSets && t.StringsByName.TryGetValue(name, out int s))
        {
            result = new PropertyRef(PropertyKind.Strings, s);
            return true;
        }

        // Property-of-strings names without the v flag are reported by the caller as invalid.
        return false;
    }

    /// <summary>True if <paramref name="name"/> names a property of strings (so the caller can word the error).</summary>
    internal static bool IsStringsPropertyName(string name) => Data.StringsByName.ContainsKey(name);

    /// <summary>The code points of a non-strings property.</summary>
    internal static RangeSet GetSet(PropertyRef p)
    {
        var t = Data;
        return p.Kind switch
        {
            PropertyKind.Set => t.DecodeSet(p.Index, 0),
            PropertyKind.Script => t.DecodeSet(p.Index, 0),
            PropertyKind.ScriptExtensions => t.DecodeSet(p.Index, 1),
            _ => throw new ArgumentException("A property of strings has no plain code point set.", nameof(p)),
        };
    }

    /// <summary>The members of a property of strings: single code points and the multi-code-point sequences.</summary>
    internal static (RangeSet Singles, string[] Sequences) GetStrings(PropertyRef p)
    {
        return Data.StringSet(p.Index);
    }

    private static int Lookup(Pairs pairs, int key)
    {
        int i = Array.BinarySearch(pairs.Keys, key);
        return i >= 0 ? pairs.Values[i] : key;
    }

    private static char[] BuildBmp(Pairs pairs)
    {
        var map = new char[0x10000];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = (char)i;
        }

        for (int i = 0; i < pairs.Keys.Length; i++)
        {
            if (pairs.Keys[i] <= 0xFFFF && pairs.Values[i] <= 0xFFFF)
            {
                map[pairs.Keys[i]] = (char)pairs.Values[i];
            }
        }

        return map;
    }

    private static RangeSet KeysOf(Pairs pairs)
    {
        var l = new List<(int, int)>(pairs.Keys.Length);
        foreach (int k in pairs.Keys)
        {
            l.Add((k, k));
        }

        return RangeSet.FromRanges(l);
    }

    private static RangeSet AffectedOf(Pairs pairs)
    {
        var l = new List<(int, int)>(pairs.Keys.Length * 2);
        for (int i = 0; i < pairs.Keys.Length; i++)
        {
            l.Add((pairs.Keys[i], pairs.Keys[i]));
            l.Add((pairs.Values[i], pairs.Values[i]));
        }

        return RangeSet.FromRanges(l);
    }

    private sealed class Pairs(int[] keys, int[] values)
    {
        internal int[] Keys { get; } = keys;

        internal int[] Values { get; } = values;
    }

    private sealed class UnicodeTables
    {
        private readonly byte[] _data;
        private readonly List<(PropertyKind Kind, int Offset)> _entries = [];
        private readonly Dictionary<int, RangeSet>[] _cache = [[], []];
        private readonly int[] _stringOffsets;
        private readonly string[] _stringNames;
        private (RangeSet, string[])?[] _stringCache;

        private UnicodeTables(byte[] data)
        {
            _data = data;
            int pos = 5;
            int count = Varint(ref pos);
            GcByName = new Dictionary<string, int>(StringComparer.Ordinal);
            ScriptByName = new Dictionary<string, int>(StringComparer.Ordinal);
            BinaryByName = new Dictionary<string, PropertyRef>(StringComparer.Ordinal);
            for (int e = 0; e < count; e++)
            {
                byte kind = data[pos++];
                int nn = Varint(ref pos);
                var names = new string[nn];
                for (int i = 0; i < nn; i++)
                {
                    names[i] = Utf8(ref pos);
                }

                int bodyLen = Varint(ref pos);
                int idx = _entries.Count;
                _entries.Add((kind == 0 ? PropertyKind.Set : kind == 1 ? PropertyKind.Script : PropertyKind.Set, pos));
                foreach (string n in names)
                {
                    switch (kind)
                    {
                        case 0:
                            GcByName[n] = idx;
                            break;
                        case 1:
                            ScriptByName[n] = idx;
                            break;
                        default:
                            BinaryByName[n] = new PropertyRef(PropertyKind.Set, idx);
                            break;
                    }
                }

                pos += bodyLen;
            }

            Fold = ReadPairs(ref pos);
            Upper = ReadPairs(ref pos);
            int ns = Varint(ref pos);
            _stringOffsets = new int[ns + 1];
            _stringNames = new string[ns + 1];
            StringsByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < ns; i++)
            {
                _stringNames[i] = Utf8(ref pos);
                _stringOffsets[i] = pos;
                StringsByName[_stringNames[i]] = i;
                SkipStringSet(ref pos);
            }

            // RGI_Emoji is the union of the six sets; it takes the last slot.
            _stringNames[ns] = "RGI_Emoji";
            StringsByName["RGI_Emoji"] = ns;
            _stringCache = new (RangeSet, string[])?[ns + 1];
        }

        internal Dictionary<string, int> GcByName { get; }

        internal Dictionary<string, int> ScriptByName { get; }

        internal Dictionary<string, PropertyRef> BinaryByName { get; }

        internal Dictionary<string, int> StringsByName { get; }

        internal Pairs Fold { get; }

        internal Pairs Upper { get; }

        internal static UnicodeTables Load()
        {
            using Stream? s = typeof(RegExpUnicode).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("The RegExp Unicode table is missing from the assembly.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] data = ms.ToArray();
            if (data.Length < 5 || data[0] != 'O' || data[1] != 'E' || data[2] != 'R' || data[3] != 'U' || data[4] != 1)
            {
                throw new InvalidDataException("Not a RegExp Unicode table of a supported version.");
            }

            return new UnicodeTables(data);
        }

        internal RangeSet DecodeSet(int entry, int which)
        {
            var cache = _cache[which];
            lock (cache)
            {
                if (cache.TryGetValue(entry, out var set))
                {
                    return set;
                }

                int pos = _entries[entry].Offset;
                if (which == 1)
                {
                    SkipRanges(ref pos);
                }

                set = ReadRanges(ref pos);
                cache[entry] = set;
                return set;
            }
        }

        internal (RangeSet, string[]) StringSet(int index)
        {
            lock (_stringCache)
            {
                if (_stringCache[index] is { } cached)
                {
                    return cached;
                }

                (RangeSet, string[]) result;
                if (index == _stringNames.Length - 1)
                {
                    var singles = RangeSet.Empty;
                    var all = new SortedSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < index; i++)
                    {
                        var (s, seqs) = ReadStringSet(_stringOffsets[i]);
                        singles = RangeSet.Union(singles, s);
                        all.UnionWith(seqs);
                    }

                    result = (singles, [.. all]);
                }
                else
                {
                    result = ReadStringSet(_stringOffsets[index]);
                }

                _stringCache[index] = result;
                return result;
            }
        }

        private (RangeSet, string[]) ReadStringSet(int pos)
        {
            var singles = ReadRanges(ref pos);
            int n = Varint(ref pos);
            var seqs = new string[n];
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                int len = Varint(ref pos);
                sb.Clear();
                for (int j = 0; j < len; j++)
                {
                    sb.Append(char.ConvertFromUtf32(Varint(ref pos)));
                }

                seqs[i] = sb.ToString();
            }

            return (singles, seqs);
        }

        private void SkipStringSet(ref int pos)
        {
            SkipRanges(ref pos);
            int n = Varint(ref pos);
            for (int i = 0; i < n; i++)
            {
                int len = Varint(ref pos);
                for (int j = 0; j < len; j++)
                {
                    Varint(ref pos);
                }
            }
        }

        private RangeSet ReadRanges(ref int pos)
        {
            int n = Varint(ref pos);
            var pairs = new int[n * 2];
            int prev = 0;
            for (int i = 0; i < n; i++)
            {
                int first = prev + Varint(ref pos);
                int last = first + Varint(ref pos);
                pairs[2 * i] = first;
                pairs[(2 * i) + 1] = last;
                prev = last + 1;
            }

            return RangeSet.FromNormalized(pairs);
        }

        private void SkipRanges(ref int pos)
        {
            int n = Varint(ref pos);
            for (int i = 0; i < n * 2; i++)
            {
                Varint(ref pos);
            }
        }

        private Pairs ReadPairs(ref int pos)
        {
            int n = Varint(ref pos);
            var keys = new int[n];
            var values = new int[n];
            int prev = 0;
            for (int i = 0; i < n; i++)
            {
                int k = prev + Varint(ref pos);
                uint z = (uint)Varint(ref pos);
                int d = (int)(z >> 1) ^ -(int)(z & 1);
                keys[i] = k;
                values[i] = k + d;
                prev = k;
            }

            return new Pairs(keys, values);
        }

        private int Varint(ref int pos)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = _data[pos++];
                result |= (uint)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return (int)result;
                }

                shift += 7;
            }
        }

        private string Utf8(ref int pos)
        {
            int len = Varint(ref pos);
            string s = Encoding.UTF8.GetString(_data, pos, len);
            pos += len;
            return s;
        }
    }
}
