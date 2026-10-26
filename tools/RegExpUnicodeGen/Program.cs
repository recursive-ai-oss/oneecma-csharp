using System.Globalization;
using System.Text;

namespace RecursiveAI.OneEcma.RegExpUnicodeGen;

/// <summary>
/// Generates the embedded Unicode tables of the RegExp engine from UCD 17.0.0:
/// property sets for \p{..}, simple case folding, the uppercase mapping of the non-Unicode Canonicalize,
/// and the sequence sets behind the properties of strings. Output is deterministic (sorted, no timestamps).
/// Usage: oneecma-regexp-unicode-gen &lt;ucd-dir&gt; &lt;output.bin&gt;
/// The binary layout is documented in docs/regexp.md.
/// </summary>
internal static class Program
{
    private const int MaxCodePoint = 0x10FFFF;

    // ECMA-262 Table 65 (binary properties) as canonical name and optional alias.
    private static readonly string[] BinaryProperties =
    [
        "ASCII", "ASCII_Hex_Digit:AHex", "Alphabetic:Alpha", "Any", "Assigned", "Bidi_Control:Bidi_C", "Bidi_Mirrored:Bidi_M",
        "Case_Ignorable:CI", "Cased", "Changes_When_Casefolded:CWCF", "Changes_When_Casemapped:CWCM", "Changes_When_Lowercased:CWL",
        "Changes_When_NFKC_Casefolded:CWKCF", "Changes_When_Titlecased:CWT", "Changes_When_Uppercased:CWU", "Dash",
        "Default_Ignorable_Code_Point:DI", "Deprecated:Dep", "Diacritic:Dia", "Emoji", "Emoji_Component:EComp", "Emoji_Modifier:EMod",
        "Emoji_Modifier_Base:EBase", "Emoji_Presentation:EPres", "Extended_Pictographic:ExtPict", "Extender:Ext", "Grapheme_Base:Gr_Base",
        "Grapheme_Extend:Gr_Ext", "Hex_Digit:Hex", "IDS_Binary_Operator:IDSB", "IDS_Trinary_Operator:IDST", "ID_Continue:IDC",
        "ID_Start:IDS", "Ideographic:Ideo", "Join_Control:Join_C", "Logical_Order_Exception:LOE", "Lowercase:Lower", "Math",
        "Noncharacter_Code_Point:NChar", "Pattern_Syntax:Pat_Syn", "Pattern_White_Space:Pat_WS", "Quotation_Mark:QMark", "Radical",
        "Regional_Indicator:RI", "Sentence_Terminal:STerm", "Soft_Dotted:SD", "Terminal_Punctuation:Term", "Unified_Ideograph:UIdeo",
        "Uppercase:Upper", "Variation_Selector:VS", "White_Space:space", "XID_Continue:XIDC", "XID_Start:XIDS",
    ];

    private static readonly (string Group, string[] Members)[] GcGroups =
    [
        ("LC", ["Lu", "Ll", "Lt"]),
        ("L", ["Lu", "Ll", "Lt", "Lm", "Lo"]),
        ("M", ["Mn", "Mc", "Me"]),
        ("N", ["Nd", "Nl", "No"]),
        ("P", ["Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po"]),
        ("S", ["Sm", "Sc", "Sk", "So"]),
        ("Z", ["Zs", "Zl", "Zp"]),
        ("C", ["Cc", "Cf", "Cs", "Co", "Cn"]),
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: oneecma-regexp-unicode-gen <ucd-dir> <output.bin>");
            return 2;
        }

        string ucd = args[0];
        var file = new List<byte>();
        file.AddRange("OERU"u8.ToArray());
        file.Add(1);

        // --- property sets ---------------------------------------------------------------------------------
        var entries = new List<(byte Kind, string[] Names, List<(int, int)> A, List<(int, int)>? B)>();

        // General_Category: the 30 values from the derived file, Cn as the complement, then the groups.
        var gc = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (var (first, last, f) in Read(Path.Combine(ucd, "DerivedGeneralCategory.txt")))
        {
            if (!gc.TryGetValue(f[0], out var l)) { gc[f[0]] = l = []; }
            l.Add((first, last));
        }

        var assigned = new List<(int, int)>();
        foreach (var kv in gc) { if (kv.Key != "Cn") { assigned.AddRange(kv.Value); } }
        gc["Cn"] = Complement(Normalize(assigned));
        foreach (var (group, members) in GcGroups)
        {
            gc[group] = Normalize(members.SelectMany(m => gc[m]).ToList());
        }

        var aliases = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(Path.Combine(ucd, "PropertyValueAliases.txt")))
        {
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) { continue; }
            string[] p = line.Split(';').Select(s => s.Trim()).ToArray();
            if (p[0] is "gc" or "sc")
            {
                if (!aliases.TryGetValue(p[0], out var l)) { aliases[p[0]] = l = []; }
                l.Add(p[1..]);
            }
        }

        foreach (string[] names in aliases["gc"].OrderBy(n => n[0], StringComparer.Ordinal))
        {
            entries.Add((0, names, gc[names[0]], null));
        }

        // Script and Script_Extensions: Scripts.txt lists long names; ScriptExtensions.txt lists long names too.
        var longToShort = aliases["sc"].ToDictionary(n => n[1], n => n[0], StringComparer.Ordinal);
        var sc = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (var (first, last, f) in Read(Path.Combine(ucd, "Scripts.txt")))
        {
            string s = longToShort[f[0]];
            if (!sc.TryGetValue(s, out var l)) { sc[s] = l = []; }
            l.Add((first, last));
        }

        var listed = new List<(int, int)>();
        foreach (var kv in sc) { listed.AddRange(kv.Value); }
        sc["Zzzz"] = Complement(Normalize(listed));

        // Script_Extensions defaults to the Script value; ScriptExtensions.txt replaces it for the listed code points.
        var scx = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (var kv in sc) { scx[kv.Key] = [.. kv.Value]; }
        foreach (var (first, last, f) in Read(Path.Combine(ucd, "ScriptExtensions.txt")))
        {
            string[] shorts = f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Remove the range from every script's default set, then add it to the listed ones.
            foreach (string k in scx.Keys.ToList())
            {
                scx[k] = Subtract(scx[k], first, last);
            }

            foreach (string s in shorts) { scx[s].Add((first, last)); }
        }

        foreach (string[] names in aliases["sc"].OrderBy(n => n[0], StringComparer.Ordinal))
        {
            entries.Add((1, names, Normalize(sc.GetValueOrDefault(names[0]) ?? []), Normalize(scx.GetValueOrDefault(names[0]) ?? [])));
        }

        // Binary properties.
        var byName = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (string name in new[] { "PropList.txt", "DerivedCoreProperties.txt", "emoji-data.txt", "DerivedNormalizationProps.txt" })
        {
            foreach (var (first, last, f) in Read(Path.Combine(ucd, name)))
            {
                if (f.Length != 1) { continue; }
                if (!byName.TryGetValue(f[0], out var l)) { byName[f[0]] = l = []; }
                l.Add((first, last));
            }
        }

        var mirrored = new List<(int, int)>();
        foreach (var (cp, _, f) in ReadUnicodeData(Path.Combine(ucd, "UnicodeData.txt")))
        {
            if (f[8] == "Y") { mirrored.Add((cp, cp)); }
        }

        foreach (string spec in BinaryProperties)
        {
            string[] names = spec.Split(':');
            List<(int, int)> ranges = names[0] switch
            {
                "ASCII" => [(0, 0x7F)],
                "Any" => [(0, MaxCodePoint)],
                "Assigned" => Complement(gc["Cn"]),
                "Bidi_Mirrored" => mirrored,
                _ => byName[names[0]],
            };
            entries.Add((2, names, Normalize(ranges), null));
        }

        var payload = new List<byte>();
        Varint(payload, (uint)entries.Count);
        foreach (var (kind, names, a, b) in entries)
        {
            payload.Add(kind);
            Varint(payload, (uint)names.Length);
            foreach (string n in names) { Utf8(payload, n); }
            var body = new List<byte>();
            Ranges(body, a);
            if (b is not null) { Ranges(body, b); }
            Varint(payload, (uint)body.Count);
            payload.AddRange(body);
        }

        file.AddRange(payload);

        // --- simple case folding (CaseFolding.txt statuses C and S) ---------------------------------------------
        var fold = new SortedDictionary<int, int>();
        foreach (string raw in File.ReadLines(Path.Combine(ucd, "CaseFolding.txt")))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) { continue; }
            string[] p = line.Split(';').Select(s => s.Trim()).ToArray();
            if (p[1] is "C" or "S")
            {
                fold[Hex(p[0])] = Hex(p[2]);
            }
        }

        Pairs(file, fold);

        // --- uppercase mapping used by Canonicalize without u/v: ECMA-262 22.2.2.7.3 steps 5-10 ----------------
        var upper = new Dictionary<int, int>();
        var simpleUpper = new Dictionary<int, int>();
        foreach (var (cp, _, f) in ReadUnicodeData(Path.Combine(ucd, "UnicodeData.txt")))
        {
            if (f[11].Length > 0) { simpleUpper[cp] = Hex(f[11]); }
        }

        // SpecialCasing.txt unconditional entries give the full mapping, which wins over the simple one.
        var fullUpper = new Dictionary<int, int[]>();
        foreach (string raw in File.ReadLines(Path.Combine(ucd, "SpecialCasing.txt")))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) { continue; }
            string[] p = line.Split(';').Select(s => s.Trim()).ToArray();
            if (p.Length >= 5 && p[4].Length == 0 || p.Length == 4)
            {
                fullUpper[Hex(p[0])] = p[3].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Hex).ToArray();
            }
        }

        var nonUnicode = new SortedDictionary<int, int>();
        for (int ch = 0; ch <= 0xFFFF; ch++)
        {
            if (ch is >= 0xD800 and <= 0xDFFF) { continue; }
            int[] u = fullUpper.TryGetValue(ch, out var fu) ? fu : simpleUpper.TryGetValue(ch, out int su) ? [su] : [ch];
            if (u.Length != 1 || u[0] > 0xFFFF) { continue; }
            int cu = u[0];
            if (ch >= 128 && cu < 128) { continue; }
            if (cu != ch) { nonUnicode[ch] = cu; }
        }

        Pairs(file, nonUnicode);

        // --- properties of strings --------------------------------------------------------------------------
        var sets = new SortedDictionary<string, (List<(int, int)> Singles, List<int[]> Seqs)>(StringComparer.Ordinal)
        {
            ["Basic_Emoji"] = ([], []),
            ["Emoji_Keycap_Sequence"] = ([], []),
            ["RGI_Emoji_Flag_Sequence"] = ([], []),
            ["RGI_Emoji_Modifier_Sequence"] = ([], []),
            ["RGI_Emoji_Tag_Sequence"] = ([], []),
            ["RGI_Emoji_ZWJ_Sequence"] = ([], []),
        };
        foreach (string name in new[] { "emoji-sequences.txt", "emoji-zwj-sequences.txt" })
        {
            foreach (string raw in File.ReadLines(Path.Combine(ucd, name)))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) { continue; }
                string[] p = line.Split(';').Select(s => s.Trim()).ToArray();
                var set = sets[p[1]];
                if (p[0].Contains("..", StringComparison.Ordinal))
                {
                    string[] r = p[0].Split("..");
                    set.Singles.Add((Hex(r[0]), Hex(r[1])));
                }
                else
                {
                    int[] cps = p[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Hex).ToArray();
                    if (cps.Length == 1) { set.Singles.Add((cps[0], cps[0])); } else { set.Seqs.Add(cps); }
                }
            }
        }

        Varint(file, (uint)sets.Count);
        foreach (var (name, (singles, seqs)) in sets)
        {
            Utf8(file, name);
            Ranges(file, Normalize(singles));
            var sorted = seqs.OrderBy(s => string.Join(' ', s.Select(c => c.ToString("X6", CultureInfo.InvariantCulture))), StringComparer.Ordinal).ToList();
            Varint(file, (uint)sorted.Count);
            foreach (int[] s in sorted)
            {
                Varint(file, (uint)s.Length);
                foreach (int c in s) { Varint(file, (uint)c); }
            }
        }

        File.WriteAllBytes(args[1], file.ToArray());
        Console.WriteLine($"wrote {args[1]}: {file.Count} bytes, {entries.Count} property sets, {fold.Count} folds, {nonUnicode.Count} uppercase pairs");
        return 0;
    }

    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static IEnumerable<(int First, int Last, string[] Fields)> Read(string path)
    {
        foreach (string raw in File.ReadLines(path))
        {
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) { continue; }
            string[] parts = line.Split(';').Select(p => p.Trim()).ToArray();
            int dots = parts[0].IndexOf("..", StringComparison.Ordinal);
            int first = Hex(dots >= 0 ? parts[0][..dots] : parts[0]);
            int last = dots >= 0 ? Hex(parts[0][(dots + 2)..]) : first;
            yield return (first, last, parts[1..]);
        }
    }

    /// <summary>UnicodeData.txt rows, expanding the First/Last range pairs. Fields are numbered from the name (index 0).</summary>
    private static IEnumerable<(int Cp, string Name, string[] Fields)> ReadUnicodeData(string path)
    {
        int rangeStart = -1;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0) { continue; }
            string[] p = line.Split(';');
            int cp = Hex(p[0]);
            string name = p[1];
            string[] f = p[1..];
            if (name.EndsWith(", First>", StringComparison.Ordinal)) { rangeStart = cp; continue; }
            if (name.EndsWith(", Last>", StringComparison.Ordinal))
            {
                for (int c = rangeStart; c <= cp; c++) { yield return (c, name, f); }
                continue;
            }

            yield return (cp, name, f);
        }
    }

    private static List<(int, int)> Normalize(List<(int First, int Last)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.First).ThenBy(r => r.Last).ToList();
        var result = new List<(int, int)>();
        foreach (var (f, l) in sorted)
        {
            if (result.Count > 0 && f <= result[^1].Item2 + 1)
            {
                if (l > result[^1].Item2) { result[^1] = (result[^1].Item1, l); }
            }
            else
            {
                result.Add((f, l));
            }
        }

        return result;
    }

    private static List<(int, int)> Complement(List<(int First, int Last)> normalized)
    {
        var result = new List<(int, int)>();
        int next = 0;
        foreach (var (f, l) in normalized)
        {
            if (f > next) { result.Add((next, f - 1)); }
            next = l + 1;
        }

        if (next <= MaxCodePoint) { result.Add((next, MaxCodePoint)); }
        return result;
    }

    private static List<(int, int)> Subtract(List<(int First, int Last)> ranges, int first, int last)
    {
        var result = new List<(int, int)>();
        foreach (var (f, l) in ranges)
        {
            if (l < first || f > last) { result.Add((f, l)); continue; }
            if (f < first) { result.Add((f, first - 1)); }
            if (l > last) { result.Add((last + 1, l)); }
        }

        return result;
    }

    private static void Varint(List<byte> o, uint v)
    {
        while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; }
        o.Add((byte)v);
    }

    private static void Utf8(List<byte> o, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        Varint(o, (uint)b.Length);
        o.AddRange(b);
    }

    /// <summary>Ranges as a count, then (gap from the previous end, length - 1) pairs.</summary>
    private static void Ranges(List<byte> o, List<(int First, int Last)> normalized)
    {
        Varint(o, (uint)normalized.Count);
        int prev = 0;
        foreach (var (f, l) in normalized)
        {
            Varint(o, (uint)(f - prev));
            Varint(o, (uint)(l - f));
            prev = l + 1;
        }
    }

    /// <summary>Sorted (key, value) pairs as a count, then (key gap, zigzag value delta).</summary>
    private static void Pairs(List<byte> o, SortedDictionary<int, int> map)
    {
        Varint(o, (uint)map.Count);
        int prev = 0;
        foreach (var (k, v) in map)
        {
            Varint(o, (uint)(k - prev));
            int d = v - k;
            Varint(o, (uint)((d << 1) ^ (d >> 31)));
            prev = k;
        }
    }
}
