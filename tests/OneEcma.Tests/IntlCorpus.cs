using System.Numerics;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Reads the recorded Node output under <c>testdata/intl</c> (or the directory in ONEECMA_INTL_CORPUS) and
/// compares the services against it row by row. Rows listed in <c>deviations.txt</c> are known, explained
/// differences and do not fail the test. With ONEECMA_INTL_REPORT set, every mismatch is written there.
/// </summary>
internal static class IntlCorpus
{
    internal static string Dir =>
        Environment.GetEnvironmentVariable("ONEECMA_INTL_CORPUS") is { Length: > 0 } d ? d : Path.Combine(RepoPaths.Root, "testdata", "intl");

    // The larger corpus files are stored gzip-compressed (name.jsonl.gz).
    private static IEnumerable<string> Lines(string path)
    {
        if (File.Exists(path))
        {
            return File.ReadLines(path);
        }

        return ReadGzip(path + ".gz");
    }

    private static IEnumerable<string> ReadGzip(string path)
    {
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    internal static IEnumerable<JsonElement> Rows(string file, params string[] kinds)
    {
        foreach (string line in Lines(Path.Combine(Dir, file)))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonElement row = JsonDocument.Parse(line).RootElement.Clone();
            if (kinds.Length == 0 || kinds.Contains(row.GetProperty("s").GetString()))
            {
                yield return row;
            }
        }
    }

    internal static Dictionary<string, string> Deviations()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(RepoPaths.Root, "testdata", "intl", "deviations.txt");
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int sp = line.IndexOf(' ', StringComparison.Ordinal);
                map[sp < 0 ? line : line[..sp]] = sp < 0 ? "" : line[(sp + 1)..];
            }
        }

        return map;
    }

    /// <summary>Runs <paramref name="check"/> on every row; returns (total, matched, documented, failures).</summary>
    internal static (int Total, int Matched, int Documented, List<string> Failures) Run(string service, IEnumerable<JsonElement> rows, Func<JsonElement, string?> check)
    {
        if (!Directory.Exists(Dir) || !Directory.EnumerateFiles(Dir, "*.jsonl").Any())
        {
            Assert.Skip("No recorded corpus in " + Dir + " (set ONEECMA_INTL_CORPUS).");
        }

        Dictionary<string, string> dev = Deviations();
        int total = 0, matched = 0, documented = 0;
        var failures = new List<string>();
        var report = new StringBuilder();
        foreach (JsonElement row in rows)
        {
            total++;
            string? mismatch;
            try
            {
                mismatch = check(row);
            }
            catch (IntlException e)
            {
                mismatch = ExpectedError(row, e.Kind == IntlErrorKind.Range ? "RangeError" : "TypeError");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                mismatch = "threw " + e.GetType().Name + ": " + e.Message;
            }

            if (mismatch is null)
            {
                matched++;
                continue;
            }

            string id = row.GetProperty("id").GetString()!;
            string line = $"{id} [{row.GetProperty("s").GetString()}/{(row.TryGetProperty("g", out JsonElement g) ? g.GetString() : "")}] {row.GetProperty("l").GetString()} {row.GetProperty("o").GetRawText()} {(row.TryGetProperty("v", out JsonElement v) ? v.ToString() : "")} => {mismatch}";
            report.AppendLine(line);
            if (dev.ContainsKey(id))
            {
                documented++;
            }
            else
            {
                failures.Add(line);
            }
        }

        if (Environment.GetEnvironmentVariable("ONEECMA_INTL_REPORT") is { Length: > 0 } rep)
        {
            Directory.CreateDirectory(rep);
            File.WriteAllText(Path.Combine(rep, service + ".txt"), report.ToString());
            File.AppendAllText(Path.Combine(rep, "summary.txt"), $"{service}: {total} rows, {matched} match, {documented} documented, {failures.Count} unexplained\n");
        }

        return (total, matched, documented, failures);
    }

    /// <summary>The expected error class of a row when this run threw one: null when the row expected the same error.</summary>
    internal static string? ExpectedError(JsonElement row, string actual) =>
        row.TryGetProperty("e", out JsonElement e) && e.GetString() == actual ? null : $"threw {actual}, expected {(row.TryGetProperty("e", out e) ? e.GetString() : "a value")}";

    internal static DecimalNumber Value(string token)
    {
        string t = token[2..];
        switch (token[0])
        {
            case 'n':
                return DecimalNumber.FromDouble(t switch
                {
                    "NaN" => double.NaN,
                    "Infinity" => double.PositiveInfinity,
                    "-Infinity" => double.NegativeInfinity,
                    _ => double.Parse(t, System.Globalization.NumberStyles.Float, System.Globalization.NumberFormatInfo.InvariantInfo),
                });
            case 'b':
                return DecimalNumber.FromBigInteger(BigInteger.Parse(t, System.Globalization.NumberFormatInfo.InvariantInfo));
            default:
                return DecimalNumber.ParseNumericString(t);
        }
    }

    internal static string? Str(this JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    internal static int? Int(this JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;

    internal static string PartsText(IEnumerable<(string Type, string Value)> parts) =>
        string.Join("|", parts.Select(p => p.Type + ":" + p.Value));

    internal static string PartsText(JsonElement parts) =>
        string.Join("|", parts.EnumerateArray().Select(p => p[0].GetString() + ":" + p[1].GetString()));
}
