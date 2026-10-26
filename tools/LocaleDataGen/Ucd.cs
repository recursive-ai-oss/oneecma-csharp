using System.Globalization;

namespace RecursiveAI.OneEcma.LocaleDataGen;

/// <summary>Reads the semicolon-separated range files of the Unicode Character Database.</summary>
internal static class Ucd
{
    /// <summary>Yields (first, last, fields) for each data line; fields are trimmed and exclude the comment.</summary>
    public static IEnumerable<(int First, int Last, string[] Fields)> Read(string path)
    {
        foreach (string raw in File.ReadLines(path))
        {
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(';').Select(p => p.Trim()).ToArray();
            string range = parts[0];
            int dots = range.IndexOf("..", StringComparison.Ordinal);
            int first = int.Parse(dots >= 0 ? range[..dots] : range, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int last = dots >= 0 ? int.Parse(range[(dots + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : first;
            yield return (first, last, parts[1..]);
        }
    }
}

/// <summary>
/// Break-property tables for UAX #29: Grapheme_Cluster_Break, Word_Break, Sentence_Break,
/// Extended_Pictographic and Indic_Conjunct_Break. Each property is a list of ranges with a value index;
/// code points not listed have value 0 (the property's default).
/// </summary>
internal static class Breaks
{
    public static byte[] Write(string ucd)
    {
        var file = new List<byte>();
        Bin.Magic(file, Format.KindBreaks);
        var props = new List<(string Name, string DefaultValue, List<(int, int, string)> Ranges)>
        {
            ("Grapheme_Cluster_Break", "Other", Load(Path.Combine(ucd, "GraphemeBreakProperty.txt"), null)),
            ("Word_Break", "Other", Load(Path.Combine(ucd, "WordBreakProperty.txt"), null)),
            ("Sentence_Break", "Other", Load(Path.Combine(ucd, "SentenceBreakProperty.txt"), null)),
            ("Extended_Pictographic", "No", Load(Path.Combine(ucd, "emoji-data.txt"), "Extended_Pictographic")),
            ("Indic_Conjunct_Break", "None", LoadInCb(Path.Combine(ucd, "DerivedCoreProperties.txt"))),
        };
        Bin.Varint(file, (uint)props.Count);
        foreach (var (name, def, ranges) in props)
        {
            Bin.Utf8(file, name);

            // Value 0 is the default; the others follow in name order so the table is stable.
            var values = new List<string> { def };
            values.AddRange(ranges.Select(r => r.Item3).Distinct(StringComparer.Ordinal).Where(v => v != def).OrderBy(v => v, StringComparer.Ordinal));
            if (name == "Extended_Pictographic")
            {
                values = ["No", "Yes"];
            }

            Bin.Varint(file, (uint)values.Count);
            foreach (string v in values)
            {
                Bin.Utf8(file, v);
            }

            // Sort, then merge neighbours with the same value into one range.
            var sorted = ranges.OrderBy(r => r.Item1).ToList();
            var merged = new List<(int First, int Last, int Value)>();
            foreach (var (first, last, v) in sorted)
            {
                int vi = name == "Extended_Pictographic" ? 1 : values.IndexOf(v);
                if (merged.Count > 0 && merged[^1].Value == vi && merged[^1].Last + 1 == first)
                {
                    merged[^1] = (merged[^1].First, last, vi);
                }
                else
                {
                    merged.Add((first, last, vi));
                }
            }

            Bin.Varint(file, (uint)merged.Count);
            int prevEnd = -1;
            foreach (var (first, last, vi) in merged)
            {
                Bin.Varint(file, (uint)(first - prevEnd - 1));
                Bin.Varint(file, (uint)(last - first));
                file.Add((byte)vi);
                prevEnd = last;
            }
        }

        return file.ToArray();
    }

    private static List<(int, int, string)> Load(string path, string? only)
    {
        var result = new List<(int, int, string)>();
        foreach (var (first, last, fields) in Ucd.Read(path))
        {
            if (only is null || fields[0] == only)
            {
                result.Add((first, last, fields[0]));
            }
        }

        return result;
    }

    // UAX #44: InCB is derived from DerivedCoreProperties lines of the form "094D ; InCB; Linker".
    private static List<(int, int, string)> LoadInCb(string path)
    {
        var result = new List<(int, int, string)>();
        foreach (var (first, last, fields) in Ucd.Read(path))
        {
            if (fields.Length >= 2 && fields[0] == "InCB")
            {
                result.Add((first, last, fields[1]));
            }
        }

        return result;
    }
}
