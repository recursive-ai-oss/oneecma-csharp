using System.Globalization;

namespace RecursiveAI.OneEcma.LocaleDataGen;

/// <summary>
/// Packs the CLDR root collation (allkeys_CLDR.txt, UTS #35 "Root Data Files") and the implicit-weight
/// ranges of UTS #10 section 10.1.3 into one blob. Layout after the container header:
/// varint-prefixed version string; varint implicit class count and per class: u16 base, byte shift,
/// byte mode, varint origin; varint run count and per run: varint gap, varint length-1, byte class;
/// varint single-code-point entry count and per entry: varint code point delta, byte CE count, CEs;
/// varint contraction count and per entry: byte key length, varint code points, byte CE count, CEs.
/// A CE is varint primary, varint secondary, byte tertiary (bit 7 set when the element is variable).
/// </summary>
internal static class Collation
{
    private const int Max = 0x110000;

    public static byte[] Write(string allkeys, string ucd)
    {
        string version = "";
        var singles = new SortedDictionary<int, List<(int P, int S, int T, bool Var)>>();
        var contractions = new List<(int[] Key, List<(int P, int S, int T, bool Var)> Ces)>();
        foreach (string raw in File.ReadLines(allkeys))
        {
            if (raw.StartsWith("@version ", StringComparison.Ordinal))
            {
                version = raw[9..].Trim();
                continue;
            }

            if (raw.Length == 0 || raw[0] == '#' || raw[0] == '@')
            {
                continue;
            }

            int semi = raw.IndexOf(';', StringComparison.Ordinal);
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            int[] key = raw[..semi].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            string body = hash >= 0 ? raw[(semi + 1)..hash] : raw[(semi + 1)..];
            var ces = new List<(int, int, int, bool)>();
            foreach (string ce in body.Split(']', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string c = ce.TrimStart('[');
                bool variable = c[0] == '*';
                string[] w = c[1..].Split('.');
                ces.Add((
                    int.Parse(w[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(w[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(w[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    variable));
            }

            if (key.Length == 1)
            {
                singles[key[0]] = ces;
            }
            else
            {
                contractions.Add((key, ces));
            }
        }

        contractions.Sort((a, b) =>
        {
            for (int i = 0; i < Math.Min(a.Key.Length, b.Key.Length); i++)
            {
                int c = a.Key[i].CompareTo(b.Key[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Key.Length.CompareTo(b.Key.Length);
        });

        var file = new List<byte>();
        Bin.Magic(file, Format.KindCollation);
        Bin.Utf8(file, version);
        WriteImplicit(file, ucd);

        Bin.Varint(file, (uint)singles.Count);
        int prev = 0;
        foreach (var (cp, ces) in singles)
        {
            Bin.Varint(file, (uint)(cp - prev));
            prev = cp;
            WriteCes(file, ces);
        }

        Bin.Varint(file, (uint)contractions.Count);
        foreach (var (key, ces) in contractions)
        {
            file.Add((byte)key.Length);
            foreach (int cp in key)
            {
                Bin.Varint(file, (uint)cp);
            }

            WriteCes(file, ces);
        }

        return file.ToArray();
    }

    private static void WriteCes(List<byte> file, List<(int P, int S, int T, bool Var)> ces)
    {
        file.Add((byte)ces.Count);
        foreach (var (p, s, t, v) in ces)
        {
            if (t > 0x7F)
            {
                throw new InvalidOperationException("tertiary weight does not fit the packed form");
            }

            Bin.Varint(file, (uint)p);
            Bin.Varint(file, (uint)s);
            file.Add((byte)(t | (v ? 0x80 : 0)));
        }
    }

    // UTS #10 Table "Computing Implicit Weights": AAAA is a base (plus cp >> shift for Han and unassigned),
    // BBBB is (cp - origin) | 0x8000 for the siniform scripts and (cp & 0x7FFF) | 0x8000 otherwise.
    private static void WriteImplicit(List<byte> file, string ucd)
    {
        (int Base, int Shift, int Mode, int Origin)[] classes =
        [
            (0xFBC0, 15, 1, 0),      // 0: unassigned and everything else
            (0xFB00, 0, 0, 0x17000), // 1: Tangut, Tangut Supplement
            (0xFB01, 0, 0, 0x18800), // 2: Tangut Components, Components Supplement
            (0xFB02, 0, 0, 0x1B170), // 3: Nushu
            (0xFB03, 0, 0, 0x18B00), // 4: Khitan Small Script
            (0xFB40, 15, 1, 0),      // 5: core Han
            (0xFB80, 15, 1, 0),      // 6: other unified ideographs
        ];

        var cn = new bool[Max];
        foreach (var (first, last, fields) in Ucd.Read(Path.Combine(ucd, "DerivedGeneralCategory.txt")))
        {
            if (fields[0] == "Cn")
            {
                for (int cp = first; cp <= last; cp++)
                {
                    cn[cp] = true;
                }
            }
        }

        var unified = new bool[Max];
        foreach (var (first, last, fields) in Ucd.Read(Path.Combine(ucd, "PropList.txt")))
        {
            if (fields[0] == "Unified_Ideograph")
            {
                for (int cp = first; cp <= last; cp++)
                {
                    unified[cp] = true;
                }
            }
        }

        var cls = new byte[Max];
        foreach (var (first, last, fields) in Ucd.Read(Path.Combine(ucd, "Blocks.txt")))
        {
            byte c = fields[0] switch
            {
                "Tangut" or "Tangut Supplement" => 1,
                "Tangut Components" or "Tangut Components Supplement" => 2,
                "Nushu" => 3,
                "Khitan Small Script" => 4,
                _ => 0,
            };
            bool coreHanBlock = fields[0] is "CJK Unified Ideographs" or "CJK Compatibility Ideographs";
            for (int cp = first; cp <= last; cp++)
            {
                if (cn[cp])
                {
                    continue;
                }

                if (c != 0)
                {
                    cls[cp] = c;
                }
                else if (unified[cp])
                {
                    cls[cp] = coreHanBlock ? (byte)5 : (byte)6;
                }
            }
        }

        // Unified ideographs outside any block row above (none today) still follow the Han rule.
        for (int cp = 0; cp < Max; cp++)
        {
            if (unified[cp] && cls[cp] == 0)
            {
                cls[cp] = 6;
            }
        }

        Bin.Varint(file, (uint)classes.Length);
        foreach (var (b, shift, mode, origin) in classes)
        {
            file.Add((byte)b);
            file.Add((byte)(b >> 8));
            file.Add((byte)shift);
            file.Add((byte)mode);
            Bin.Varint(file, (uint)origin);
        }

        var runs = new List<(int First, int Len, byte Class)>();
        for (int cp = 0; cp < Max;)
        {
            int end = cp;
            while (end + 1 < Max && cls[end + 1] == cls[cp])
            {
                end++;
            }

            if (cls[cp] != 0)
            {
                runs.Add((cp, end - cp + 1, cls[cp]));
            }

            cp = end + 1;
        }

        Bin.Varint(file, (uint)runs.Count);
        int prevEnd = -1;
        foreach (var (first, len, c) in runs)
        {
            Bin.Varint(file, (uint)(first - prevEnd - 1));
            Bin.Varint(file, (uint)(len - 1));
            file.Add(c);
            prevEnd = first + len - 1;
        }
    }
}
