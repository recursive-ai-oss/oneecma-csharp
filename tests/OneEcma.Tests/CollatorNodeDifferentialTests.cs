using System.Text.Json;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Compares our collator with the reference Node's <c>Intl.Collator</c> on the committed corpus over the
/// grid of locales and options (tools/text-diff/gen-collation.js recorded the expectations). Each expected
/// order is a list of groups of equal strings; adjacent groups must compare as less, members of a group as equal.
/// </summary>
public class CollatorNodeDifferentialTests
{
    private static CollatorOptions OptionsFor(string[] key, out string locale)
    {
        locale = key[0];
        string? collation = null;
        int ext = locale.IndexOf("-u-co-", StringComparison.Ordinal);
        if (ext >= 0)
        {
            collation = locale[(ext + 6)..];
            locale = locale[..ext];
        }

        var options = new CollatorOptions
        {
            IgnorePunctuation = key[2] == "1",
            Numeric = key[3] == "1",
            CaseFirst = key[4] switch { "lower" => CaseFirst.Lower, "upper" => CaseFirst.Upper, _ => CaseFirst.Off },
            Usage = key[5] == "search" ? CollationUsage.Search : CollationUsage.Sort,
            Collation = collation,
        };
        return options.WithSensitivity(key[1]);
    }

    /// <summary>Runs the whole grid and returns "combo: a vs b" lines for each disagreement.</summary>
    internal static List<(string Combo, string Detail)> Mismatches()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "testdata", "text", "collation-expected.json")));
        JsonElement root = doc.RootElement;
        string[] strings = root.GetProperty("strings").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var orders = root.GetProperty("orders").EnumerateArray()
            .Select(o => o.EnumerateArray().Select(g => g.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToArray()).ToArray();
        var result = new List<(string, string)>();
        foreach (JsonProperty combo in root.GetProperty("combos").EnumerateObject())
        {
            string[] key = combo.Name.Split('|');
            TextCollator collator = TextCollator.Create(OptionsFor(key, out string locale).Usage == CollationUsage.Search ? locale : locale, OptionsFor(key, out _));
            int[][] groups = orders[combo.Value.GetInt32()];
            for (int g = 0; g < groups.Length; g++)
            {
                string rep = strings[groups[g][0]];
                foreach (int member in groups[g].Skip(1))
                {
                    if (collator.Compare(rep, strings[member]) != 0)
                    {
                        result.Add((combo.Name, $"expected equal: {Show(rep)} = {Show(strings[member])}"));
                    }
                }

                if (g + 1 < groups.Length && collator.Compare(rep, strings[groups[g + 1][0]]) >= 0)
                {
                    result.Add((combo.Name, $"expected less: {Show(rep)} < {Show(strings[groups[g + 1][0]])}"));
                }
            }
        }

        return result;
    }

    private static string Show(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Summary()
    {
        var mismatches = Mismatches();
        var byCombo = mismatches.GroupBy(m => m.Combo).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        string text = $"{mismatches.Count} mismatches in {byCombo.Count} of 768 combinations\n"
            + string.Join("\n", byCombo.Select(g => $"{g.Key}: {g.Count()} e.g. {g.First().Detail}"));
        string dir = Environment.GetEnvironmentVariable("ONEECMA_ARTIFACTS") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, "collation-mismatches.txt"), text);
        File.WriteAllLines(Path.Combine(dir, "collation-mismatches-all.txt"), mismatches.Select(m => m.Combo + "\t" + m.Detail));
        Assert.True(mismatches.Count == 0, text.Length > 3000 ? text[..3000] : text);
    }
}
