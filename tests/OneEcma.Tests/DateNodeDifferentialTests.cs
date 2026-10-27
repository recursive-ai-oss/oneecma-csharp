using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Date against recorded Node output: toString, parse, getters and setters across DST edges and far years, with the engine host pinned
/// to a zone and Node run under TZ=. The expected files are written by tools/date-diff/record.sh; lines whose label is listed in
/// testdata/date/deviations.txt are known differences (tzdata version, documented in docs/date.md).
/// </summary>
public class DateNodeDifferentialTests
{
    private sealed class ZoneHost(string zone) : DefaultHost
    {
        internal readonly StringBuilder Output = new();

        public override string TimeZoneId => zone;

        public override double CurrentTimeMs() => 1_700_000_000_000;

        public override void Print(string text) => Output.Append(text).Append('\n');
    }

    private static string Dir => Path.Combine(RepoPaths.Root, "testdata", "date");

    private static List<string> Run(string zone)
    {
        var host = new ZoneHost(zone);
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true, Limits = new EngineLimits { Fuel = 2_000_000_000 } });
        engine.Evaluate(File.ReadAllText(Path.Combine(Dir, "probe.js")), "probe.js", TestContext.Current.CancellationToken);
        return host.Output.ToString().TrimEnd('\n').Split('\n').ToList();
    }

    private static string Label(string line)
    {
        int i = line.IndexOf(" => ", StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }

    // Before 1900 a zone's offset is local mean time with seconds and the reference ICU names it differently; the BCL zone rules the
    // default host reads round the offset to minutes. Lines whose instant is before 1900 (or at the ends of the range) are compared only in UTC.
    private const double Era1900 = -2208988800000;

    private static bool IsEarly(string expectedLine)
    {
        string label = Label(expectedLine);
        string value = expectedLine.Length > label.Length + 4 ? expectedLine[(label.Length + 4)..] : "";
        if (label.EndsWith(" setUTCFullYear:1,0,1", StringComparison.Ordinal))
        {
            return true;
        }

        var m = System.Text.RegularExpressions.Regex.Match(label, @"^(?:t|S)(-?\d+)|^C(-?\d+)/");
        if (m.Success)
        {
            string g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            double v = double.Parse(g, System.Globalization.CultureInfo.InvariantCulture);
            return m.Groups[2].Success ? v < 1900 : v < Era1900 || v > 8e15;
        }

        return label.StartsWith("P ", StringComparison.Ordinal) && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms < Era1900;
    }

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("Europe/Berlin")]
    [InlineData("UTC")]
    public void MatchesNode(string zone)
    {
        List<string> actual = Run(zone);
        string expectedPath = Path.Combine(Dir, "expected." + zone.Replace('/', '_') + ".txt");
        if (Environment.GetEnvironmentVariable("ONEECMA_DATE_ACTUAL") is { Length: > 0 } outDir)
        {
            File.WriteAllLines(Path.Combine(outDir, "actual." + zone.Replace('/', '_') + ".txt"), actual);
        }

        string[] expected = File.ReadAllLines(expectedPath);
        var known = new HashSet<string>(File.Exists(Path.Combine(Dir, "deviations.txt"))
            ? File.ReadAllLines(Path.Combine(Dir, "deviations.txt")).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Trim())
            : []);
        var bad = new List<string>();
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i] && !known.Contains(zone + "|" + Label(expected[i])) && !(zone != "UTC" && IsEarly(expected[i])))
            {
                bad.Add($"expected: {expected[i]}\n  actual: {actual[i]}");
            }
        }

        Assert.True(bad.Count == 0, $"{bad.Count} mismatches in {zone}; first:\n" + string.Join("\n", bad.Take(8)));
    }
}
