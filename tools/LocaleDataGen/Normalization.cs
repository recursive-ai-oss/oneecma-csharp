using System.Globalization;

namespace RecursiveAI.OneEcma.LocaleDataGen;

/// <summary>
/// Packs the data of UAX #15 normalization from UnicodeData.txt and DerivedNormalizationProps.txt.
/// Layout after the container header: varint range count and per range: varint gap since the previous end,
/// varint length-1, byte canonical combining class (only non-zero classes are stored); varint mapping count
/// and per mapping: varint code point delta, byte flags (bit 0 compatibility mapping, bit 1
/// Full_Composition_Exclusion), byte length, varint code points. Mappings are the single-step values of
/// UnicodeData.txt; Hangul syllables are left to the algorithm of Unicode section 3.12.
/// </summary>
internal static class Normalization
{
    public static byte[] Write(string ucd)
    {
        var ccc = new List<(int First, int Last, int Value)>();
        var maps = new SortedDictionary<int, (bool Compat, int[] Target)>();
        int rangeStart = -1;
        foreach (string line in File.ReadLines(Path.Combine(ucd, "UnicodeData.txt")))
        {
            string[] f = line.Split(';');
            int cp = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (f[1].EndsWith(", First>", StringComparison.Ordinal))
            {
                rangeStart = cp;
                continue;
            }

            int first = f[1].EndsWith(", Last>", StringComparison.Ordinal) ? rangeStart : cp;
            int value = int.Parse(f[3], CultureInfo.InvariantCulture);
            if (value != 0)
            {
                ccc.Add((first, cp, value));
            }

            string d = f[5];
            if (d.Length > 0)
            {
                bool compat = d[0] == '<';
                string body = compat ? d[(d.IndexOf('>', StringComparison.Ordinal) + 1)..] : d;
                int[] target = body.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                maps[cp] = (compat, target);
            }
        }

        // Full_Composition_Exclusion (UAX #44): canonical mappings that never compose in NFC.
        var excluded = new HashSet<int>();
        foreach (var (first, last, fields) in Ucd.Read(Path.Combine(ucd, "DerivedNormalizationProps.txt")))
        {
            if (fields[0] == "Full_Composition_Exclusion")
            {
                for (int c = first; c <= last; c++)
                {
                    excluded.Add(c);
                }
            }
        }

        var file = new List<byte>();
        Bin.Magic(file, Format.KindNormalization);
        var merged = new List<(int First, int Last, int Value)>();
        foreach (var r in ccc.OrderBy(r => r.First))
        {
            if (merged.Count > 0 && merged[^1].Value == r.Value && merged[^1].Last + 1 == r.First)
            {
                merged[^1] = (merged[^1].First, r.Last, r.Value);
            }
            else
            {
                merged.Add(r);
            }
        }

        Bin.Varint(file, (uint)merged.Count);
        int prevEnd = -1;
        foreach (var (first, last, value) in merged)
        {
            Bin.Varint(file, (uint)(first - prevEnd - 1));
            Bin.Varint(file, (uint)(last - first));
            file.Add((byte)value);
            prevEnd = last;
        }

        Bin.Varint(file, (uint)maps.Count);
        int prev = 0;
        foreach (var (cp, (compat, target)) in maps)
        {
            Bin.Varint(file, (uint)(cp - prev));
            prev = cp;
            file.Add((byte)((compat ? 1 : 0) | (excluded.Contains(cp) ? 2 : 0)));
            file.Add((byte)target.Length);
            foreach (int t in target)
            {
                Bin.Varint(file, (uint)t);
            }
        }

        return file.ToArray();
    }
}
