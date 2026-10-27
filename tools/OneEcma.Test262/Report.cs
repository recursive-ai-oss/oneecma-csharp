using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneEcma.Test262;

/// <summary>Per-directory tallies and their text and JSON renderings.</summary>
public sealed class Report
{
    private readonly SortedDictionary<string, Counts> _dirs = new(StringComparer.Ordinal);
    private readonly List<string> _stale = [];

    /// <summary>Paths of tests that passed although the documented-failures file lists them; the entries to prune.</summary>
    public IReadOnlyList<string> StalePaths => _stale;

    public Report(IEnumerable<TestResult> results)
    {
        foreach (TestResult r in results)
        {
            if (r.StaleDocumented)
            {
                _stale.Add(r.RelativePath);
            }

            Add("", r);
            string[] parts = r.RelativePath.Split('/');
            string key = "";
            // Directories down to three levels; deeper tests roll up into their depth-3 parent.
            for (int i = 0; i < parts.Length - 1 && i < 3; i++)
            {
                key = i == 0 ? parts[0] : key + "/" + parts[i];
                Add(key, r);
            }
        }
    }

    public Counts Total => _dirs.TryGetValue("", out var c) ? c : new Counts();

    private void Add(string dir, TestResult r)
    {
        if (!_dirs.TryGetValue(dir, out var c))
        {
            c = new Counts();
            _dirs[dir] = c;
        }

        c.Add(r);
    }

    public string ToText(string backend, int maxDepth)
    {
        var sb = new StringBuilder();
        sb.Append("backend: ").AppendLine(backend);
        sb.AppendLine("path                                          pass   fail   documented  skip(feat/loc/host/mode)   rate");
        foreach (var (dir, c) in _dirs)
        {
            int depth = dir.Length == 0 ? 0 : dir.Count(ch => ch == '/') + 1;
            if (depth > maxDepth)
            {
                continue;
            }

            string name = dir.Length == 0 ? "TOTAL" : dir;
            sb.Append(name.PadRight(44)).Append(' ')
              .Append(c.Pass.ToString().PadLeft(7))
              .Append(c.Fail.ToString().PadLeft(7))
              .Append(c.Documented.ToString().PadLeft(12))
              .Append("  ")
              .Append($"{c.SkippedFeature}/{c.SkippedLocale}/{c.SkippedHost}/{c.SkippedParseOnly}".PadRight(24))
              .Append(c.RateText.PadLeft(8))
              .AppendLine();
        }

        Counts t = Total;
        if (t.NotImplemented > 0)
        {
            sb.AppendLine($"not implemented (counted as fail): {t.NotImplemented}");
        }

        if (t.Stale > 0)
        {
            sb.AppendLine($"documented failures that now pass: {t.Stale}");
            foreach (string path in _stale.Order(StringComparer.Ordinal))
            {
                sb.Append("  stale: ").AppendLine(path);
            }
        }

        return sb.ToString();
    }

    public void WriteJson(Stream stream, string backend, string suiteSha, bool parseOnly)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("backend", backend);
        w.WriteString("test262", suiteSha);
        w.WriteBoolean("parseOnly", parseOnly);
        w.WriteStartArray("directories");
        foreach (var (dir, c) in _dirs)
        {
            w.WriteStartObject();
            w.WriteString("path", dir.Length == 0 ? "(total)" : dir);
            w.WriteNumber("pass", c.Pass);
            w.WriteNumber("fail", c.Fail);
            w.WriteNumber("documented", c.Documented);
            w.WriteNumber("skippedFeature", c.SkippedFeature);
            w.WriteNumber("skippedLocale", c.SkippedLocale);
            w.WriteNumber("skippedHost", c.SkippedHost);
            w.WriteNumber("skippedParseOnly", c.SkippedParseOnly);
            w.WriteNumber("notImplemented", c.NotImplemented);
            w.WriteNumber("staleDocumented", c.Stale);
            w.WriteString("passRate", c.RateText);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("staleDocumentedPaths");
        foreach (string path in _stale.Order(StringComparer.Ordinal))
        {
            w.WriteStringValue(path);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

/// <summary>Counts for one directory. The pass rate excludes skipped tests.</summary>
public sealed class Counts
{
    public int Pass { get; private set; }

    public int Fail { get; private set; }

    public int Documented { get; private set; }

    public int SkippedFeature { get; private set; }

    public int SkippedLocale { get; private set; }

    public int SkippedHost { get; private set; }

    public int SkippedParseOnly { get; private set; }

    public int NotImplemented { get; private set; }

    public int Stale { get; private set; }

    public string RateText
    {
        get
        {
            int n = Pass + Fail + Documented;
            return n == 0 ? "-" : (100.0 * Pass / n).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }
    }

    public void Add(TestResult r)
    {
        switch (r.Status)
        {
            case TestStatus.Pass: Pass++; break;
            case TestStatus.Fail: Fail++; break;
            case TestStatus.Documented: Documented++; break;
            case TestStatus.SkippedFeature: SkippedFeature++; break;
            case TestStatus.SkippedLocale: SkippedLocale++; break;
            case TestStatus.SkippedHost: SkippedHost++; break;
            case TestStatus.SkippedParseOnly: SkippedParseOnly++; break;
        }

        if (r.NotImplemented)
        {
            NotImplemented++;
        }

        if (r.StaleDocumented)
        {
            Stale++;
        }
    }
}
