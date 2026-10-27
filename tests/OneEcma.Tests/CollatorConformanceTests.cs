using System.Globalization;
using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// The CLDR root collation conformance files (UTS #10 section 12.2, CLDR flavour): every line must sort
/// at or after the previous one, compared at identical strength.
/// </summary>
public class CollatorConformanceTests
{
    private static string Hex(string field)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string h in field.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int cp = int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                sb.Append((char)cp);
            }
            else
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
        }

        return sb.ToString();
    }

    private static (int Lines, List<string> Failures) Run(string file, bool shifted)
    {
        string path = TextTestFiles.RequireOrSkip(TextTestFiles.Cldr("uca", file));
        var collator = TextCollator.Create("und", new CollatorOptions { Strength = CollationStrength.Identical, IgnorePunctuation = shifted });
        string? previous = null;
        int lines = 0;
        var failures = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            string line = raw[..raw.IndexOf(';', StringComparison.Ordinal)];
            string current = Hex(line);
            lines++;
            if (previous is not null && collator.Compare(previous, current) > 0)
            {
                failures.Add($"{lines}: {raw}");
            }

            previous = current;
        }

        return (lines, failures);
    }

    [Theory]
    [InlineData("CollationTest_CLDR_NON_IGNORABLE.txt", false)]
    [InlineData("CollationTest_CLDR_SHIFTED.txt", true)]
    public void CldrRootConformance(string file, bool shifted)
    {
        var (lines, failures) = Run(file, shifted);
        Assert.True(failures.Count == 0, $"{failures.Count} of {lines} lines out of order; first: {string.Join(" | ", failures.Take(4))}");
        Assert.True(lines > 100_000, $"only {lines} lines");
    }
}
