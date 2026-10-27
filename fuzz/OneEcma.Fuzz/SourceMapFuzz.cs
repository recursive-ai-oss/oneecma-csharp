using System.Diagnostics;
using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.SourceMaps;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Source map decoder fuzzing. <see cref="Check"/> is the entry point for a coverage-guided driver (text in, invariants
/// checked); <see cref="RunRandom"/> is the seeded generator-and-mutation driver. Checked: no exception escapes, a call
/// stays within a time bound under small limits, every problem becomes a warning diagnostic, a decoded map is sorted with
/// in-range source and name indices, lookups and remapping never throw, and the loader never fetches on its own.
/// </summary>
internal static class SourceMapFuzz
{
    private static readonly SourceMapLimits Limits = new() { MaxJsonLength = 1 << 20, MaxMappings = 100_000, MaxSections = 200, MaxSources = 5_000 };

    private static readonly string[] Fragments =
    [
        "{", "}", "[", "]", ",", ":", "\"version\":3", "\"version\":", "\"sources\":[\"a.ts\"]", "\"sources\":[\"a.ts\",null,\"../b/c.ts\"]",
        "\"sourcesContent\":[\"x\\ny\"]", "\"names\":[\"a\",\"b\"]", "\"mappings\":\"AAAA\"", "\"mappings\":\"", "\"sections\":[", "\"offset\":{\"line\":0,\"column\":0}",
        "\"offset\":{\"line\":-1,\"column\":1e30}", "\"map\":", "\"ignoreList\":[0]", "\"ignoreList\":[-1,1.5,\"a\"]", "\"sourceRoot\":\"r/\"", "\"file\":1", "null", "true", "1e999", "\"\\ud800\"",
        "AAAA", "AACA", ";", ";;;", "gggggggg", "////////", "+++++", "A", "Z", "g", "iB", "V", "D", "kLmN", "SAASA", ",", "!", " ",
    ];

    private static string Vlq(int value)
    {
        const string Digits = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        long u = value < 0 ? ((long)-value << 1) | 1 : (long)value << 1;
        var sb = new StringBuilder();
        do
        {
            int digit = (int)(u & 31);
            u >>= 5;
            sb.Append(Digits[u > 0 ? digit | 32 : digit]);
        }
        while (u > 0);
        return sb.ToString();
    }

    private static string Build(Random rng)
    {
        if (rng.Next(4) == 0)
        {
            var sb = new StringBuilder();
            int n = rng.Next(1, 30);
            for (int i = 0; i < n; i++)
            {
                sb.Append(Fragments[rng.Next(Fragments.Length)]);
            }

            return sb.ToString();
        }

        // A valid map with random segments, which mutation then breaks in interesting places.
        var m = new StringBuilder();
        int lines = rng.Next(0, 12);
        for (int l = 0; l < lines; l++)
        {
            if (l > 0)
            {
                m.Append(';');
            }

            int segs = rng.Next(0, 6);
            for (int s = 0; s < segs; s++)
            {
                if (s > 0)
                {
                    m.Append(',');
                }

                int fields = rng.Next(3) switch { 0 => 1, 1 => 4, _ => 5 };
                for (int f = 0; f < fields; f++)
                {
                    m.Append(Vlq(rng.Next(-40, 90)));
                }
            }
        }

        string body = "\"version\":3,\"sources\":[\"a.ts\",\"b.ts\"],\"names\":[\"x\",\"y\"],\"mappings\":\"" + m + "\"";
        if (rng.Next(3) == 0)
        {
            body += ",\"sourcesContent\":[\"let a;\\nlet b;\",null]";
        }

        if (rng.Next(4) == 0)
        {
            return "{\"version\":3,\"sections\":[{\"offset\":{\"line\":0,\"column\":0},\"map\":{" + body + "}},{\"offset\":{\"line\":" + rng.Next(0, 5) + ",\"column\":" + rng.Next(0, 9) + "},\"map\":{" + body + "}}]}";
        }

        return "{" + body + "}";
    }

    private static string Mutate(Random rng, string s)
    {
        var sb = new StringBuilder(s);
        int edits = rng.Next(1, 5);
        for (int i = 0; i < edits; i++)
        {
            int at = sb.Length == 0 ? 0 : rng.Next(sb.Length + 1);
            switch (rng.Next(4))
            {
                case 0 when sb.Length > 0 && at < sb.Length:
                    sb.Remove(at, Math.Min(rng.Next(1, 8), sb.Length - at));
                    break;
                case 1:
                    sb.Insert(at, Fragments[rng.Next(Fragments.Length)]);
                    break;
                case 2 when sb.Length > 0 && at < sb.Length:
                    sb[at] = (char)rng.Next(32, 127);
                    break;
                default:
                    sb.Insert(at, (char)rng.Next(0, 0x2fff));
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Runs every check on one input; returns a failure description or null.</summary>
    internal static string? Check(string json)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            SourceMapParseResult r = SourceMap.Parse(json, "dir/m.map", Limits);
            IReadOnlyList<Diagnostic> diags = r.ToDiagnostics();
            if (diags.Count != r.Problems.Count)
            {
                return "problem and diagnostic counts differ";
            }

            foreach (Diagnostic d in diags)
            {
                if (d.Severity != Severity.Warning || DiagnosticRenderer.Render(d).Length == 0)
                {
                    return "bad problem diagnostic";
                }
            }

            if (r.Map is null)
            {
                if (r.Problems.Count == 0 || !r.Problems.Any(p => p.Fatal))
                {
                    return "no map without a fatal problem";
                }
            }
            else
            {
                SourceMap map = r.Map;
                long prev = long.MinValue;
                for (int i = 0; i < map.MappingCount; i++)
                {
                    DecodedMapping m = map.GetMapping(i);
                    long key = ((long)m.GeneratedLine << 32) | (uint)m.GeneratedColumn;
                    if (key < prev || m.GeneratedLine < 0 || m.GeneratedColumn < 0)
                    {
                        return "mappings are not sorted or not non-negative";
                    }

                    prev = key;
                    if (m.SourceIndex >= map.Sources.Count || (m.SourceIndex >= 0 && (m.OriginalLine < 0 || m.OriginalColumn < 0)) || m.NameIndex >= map.Names.Count)
                    {
                        return "mapping index out of range";
                    }
                }

                var gen = new SourceText("let a = 1;\nlet b = ;\nlet c;\n", "out.js");
                var rng = new Random(json.Length);
                for (int i = 0; i < 20; i++)
                {
                    int line = rng.Next(-1, 15);
                    int col = rng.Next(-1, 40);
                    foreach (OriginalPosition p in map.GetOriginalPositions(line, col))
                    {
                        if (p.Line < 0 || p.Column < 0 || p.SourceIndex >= map.Sources.Count)
                        {
                            return "bad original position";
                        }
                    }
                }

                foreach (Span span in new[] { new Span(0, 3), new Span(15, 20), new Span(10, 10), new Span(26, 27) })
                {
                    var d = new Diagnostic("SyntaxError", "x", "h", gen, span);
                    d.WithLabel(new Span(0, 3), "l");
                    Diagnostic m = SourceMapReports.Remap(d, map, u => "x\ny\nz");
                    if (DiagnosticRenderer.Render(m).Length == 0 || DiagnosticJson.Render([m]).Length == 0)
                    {
                        return "empty render after remap";
                    }
                }
            }

            string? url = SourceMapUrl.Extract(json);
            _ = url;
            return sw.ElapsedMilliseconds > 3000 ? "slow (" + sw.ElapsedMilliseconds + " ms)" : null;
        }
#pragma warning disable CA1031 // The fuzzer's job is to catch whatever escapes.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }

    internal static int RunRandom(int seconds, int seed, string? corpusDir)
    {
        var rng = new Random(seed);
        var corpus = new List<string>();
        if (corpusDir is not null && Directory.Exists(corpusDir))
        {
            foreach (string f in Directory.GetFiles(corpusDir, "*.map", SearchOption.AllDirectories))
            {
                corpus.Add(File.ReadAllText(f));
            }
        }

        string outDir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "sourcemap-fuzz-findings");
        var sw = Stopwatch.StartNew();
        long execs = 0;
        int findings = 0;
        var seen = new HashSet<string>();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            string input = corpus.Count > 0 && rng.Next(3) == 0 ? Mutate(rng, corpus[rng.Next(corpus.Count)]) : rng.Next(3) == 0 ? Mutate(rng, Build(rng)) : Build(rng);
            string? failure = Check(input);
            execs++;
            if (failure is not null && seen.Add(failure))
            {
                findings++;
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "finding-" + findings + ".map"), input);
                Console.WriteLine("FINDING " + findings + ": " + failure);
            }
        }

        Console.WriteLine("sourcemap fuzz: " + execs + " executions in " + (int)sw.Elapsed.TotalSeconds + " s, " + findings + " distinct findings, seed " + seed + ", corpus " + corpus.Count);
        return findings;
    }
}
