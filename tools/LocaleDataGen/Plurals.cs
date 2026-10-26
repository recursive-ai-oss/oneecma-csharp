using System.Text.Json;

namespace RecursiveAI.OneEcma.LocaleDataGen;

/// <summary>
/// Compiles CLDR plural rules (UTS #35 Part 3, "Language Plural Rules") into a compact AST.
/// Layout of one rule: varint orCount, then per "and" group: varint relationCount, then per relation:
/// byte operand (0..7 = n i v w f t c e), varint modulus (0 = none), byte negated, varint rangeCount,
/// then per range varint lo and varint hi (a single value has lo == hi). Samples after '@' are dropped.
/// </summary>
internal static class Plurals
{
    private static readonly string[] Categories = ["zero", "one", "two", "few", "many", "other"];
    private const string Operands = "nivwftce";

    public static byte[] Write(string pluralsJson, string ordinalsJson)
    {
        var file = new List<byte>();
        Bin.Magic(file, Format.KindPlurals);
        WriteType(file, pluralsJson, "plurals-type-cardinal");
        WriteType(file, ordinalsJson, "plurals-type-ordinal");
        return file.ToArray();
    }

    private static void WriteType(List<byte> file, string path, string section)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement langs = doc.RootElement.GetProperty("supplemental").GetProperty(section);
        var entries = langs.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        Bin.Varint(file, (uint)entries.Count);
        foreach (JsonProperty lang in entries)
        {
            Bin.Utf8(file, lang.Name);
            var rules = new List<(int Cat, byte[] Ast)>();
            foreach (JsonProperty rule in lang.Value.EnumerateObject())
            {
                string name = rule.Name[(rule.Name.LastIndexOf('-') + 1)..];
                int cat = Array.IndexOf(Categories, name);
                if (cat < 0)
                {
                    throw new InvalidOperationException($"unknown plural category {rule.Name}");
                }

                string text = rule.Value.GetString()!;
                int at = text.IndexOf('@', StringComparison.Ordinal);
                if (at >= 0)
                {
                    text = text[..at];
                }

                text = text.Trim();

                // The "other" category has an empty condition and is the fallback; it is implied.
                if (text.Length == 0)
                {
                    continue;
                }

                rules.Add((cat, Compile(text)));
            }

            rules.Sort((a, b) => a.Cat.CompareTo(b.Cat));
            file.Add((byte)rules.Count);
            foreach (var (cat, ast) in rules)
            {
                file.Add((byte)cat);
                Bin.Varint(file, (uint)ast.Length);
                file.AddRange(ast);
            }
        }
    }

    internal static byte[] Compile(string text)
    {
        var buf = new List<byte>();
        string[] ors = text.Split(" or ", StringSplitOptions.None);
        Bin.Varint(buf, (uint)ors.Length);
        foreach (string orPart in ors)
        {
            string[] ands = orPart.Split(" and ", StringSplitOptions.None);
            Bin.Varint(buf, (uint)ands.Length);
            foreach (string rel in ands)
            {
                CompileRelation(rel.Trim(), buf);
            }
        }

        return buf.ToArray();
    }

    // relation := operand ('%' modulus)? ('=' | '!=') rangeList
    private static void CompileRelation(string rel, List<byte> buf)
    {
        bool negated = false;
        int op = rel.IndexOf("!=", StringComparison.Ordinal);
        int opLen = 2;
        if (op >= 0)
        {
            negated = true;
        }
        else
        {
            op = rel.IndexOf('=', StringComparison.Ordinal);
            opLen = 1;
        }

        if (op < 0)
        {
            throw new InvalidOperationException($"unsupported plural relation '{rel}'");
        }

        string left = rel[..op].Trim();
        string right = rel[(op + opLen)..].Trim();
        uint modulus = 0;
        int pct = left.IndexOf('%', StringComparison.Ordinal);
        if (pct >= 0)
        {
            modulus = uint.Parse(left[(pct + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            left = left[..pct].Trim();
        }

        int operand = left.Length == 1 ? Operands.IndexOf(left[0], StringComparison.Ordinal) : -1;
        if (operand < 0)
        {
            throw new InvalidOperationException($"unknown plural operand '{left}'");
        }

        buf.Add((byte)operand);
        Bin.Varint(buf, modulus);
        buf.Add(negated ? (byte)1 : (byte)0);
        string[] ranges = right.Split(',');
        Bin.Varint(buf, (uint)ranges.Length);
        foreach (string r in ranges)
        {
            int dots = r.IndexOf("..", StringComparison.Ordinal);
            uint lo;
            uint hi;
            if (dots >= 0)
            {
                lo = uint.Parse(r[..dots].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                hi = uint.Parse(r[(dots + 2)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                lo = hi = uint.Parse(r.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }

            Bin.Varint(buf, lo);
            Bin.Varint(buf, hi);
        }
    }
}
