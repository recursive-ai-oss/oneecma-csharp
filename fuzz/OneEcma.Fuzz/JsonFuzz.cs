using System.Diagnostics;
using System.Text;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// JSON.parse / JSON.stringify fuzzing. Texts are built from JSON fragments and then mutated; every text goes through
/// JSON.parse (with and without a reviver) and, when it parses, back through JSON.stringify. Checked: only JsException or a
/// limit ends a call, a text that parses re-serialises to text that parses to the same serialisation, the reviver sees the
/// same values as the plain parse, and one call stays within a time bound under small limits.
/// usage: json &lt;seconds&gt; [seed]; json-file &lt;path&gt; replays one text.
/// </summary>
internal static class JsonFuzz
{
    private static readonly string[] Fragments =
    [
        "{", "}", "[", "]", ",", ":", "\"a\"", "\"\"", "\"\\u00e9\"", "\"\\ud800\"", "\"\\ud83d\\ude00\"", "\"\\", "\"\\u12", "\"\\x\"", "\"\n\"", "\"__proto__\"",
        "0", "-0", "-", "1", "12", "01", "1.5", "1.", "1e5", "1E-5", "1e", "1e+", "9007199254740993", "1e999", "-1e999", "0.0000001",
        "true", "false", "null", "tru", "nul", "undefined", "NaN", "Infinity", " ", "\t", "\r\n", "\u00a0", "\u2028", "[[[[[[[[", "]]]]]]]]", "{\"a\":{\"a\":{\"a\":",
    ];

    private const string Harness = """
        function fuzz(text) {
          let plain;
          try { plain = JSON.parse(text); } catch (e) { if (!(e instanceof SyntaxError)) throw new Error('parse threw ' + e); return 'syntax'; }
          const seen = [];
          const revived = JSON.parse(text, function (k, v, ctx) { if (ctx && 'source' in ctx) seen.push(ctx.source); return v; });
          const s1 = JSON.stringify(plain);
          const s2 = JSON.stringify(revived);
          if (s1 !== s2) throw new Error('reviver changed the value');
          if (s1 === undefined) throw new Error('stringify returned undefined for a parsed value');
          const s3 = JSON.stringify(JSON.parse(s1));
          if (s3 !== s1) throw new Error('round trip differs: ' + s1 + ' vs ' + s3);
          JSON.stringify(plain, null, 2);
          JSON.stringify(plain, (k, v) => v, '\t');
          return 'ok';
        }
        """;

    internal static string? Check(string text)
    {
        var limits = new EngineLimits { Fuel = 5_000_000, MaxAllocatedBytes = 64L << 20, WallClockMs = 3000, MaxStringLength = 1 << 22 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        try
        {
            engine.Evaluate(Harness, "harness.js");
            JsValue fn = engine.GetProperty(engine.Global, "fuzz");
            engine.Call(fn, JsValue.Undefined, [JsValue.FromString(text)]);
            return null;
        }
        catch (JsException e)
        {
            return "script error: " + (e.Report?.Headline ?? e.Message);
        }
        catch (JsLimitException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message + "\n" + (e.InnerException ?? e).StackTrace;
        }
    }

    private static string Build(Random rng, List<string> corpus)
    {
        if (rng.Next(3) == 0 && corpus.Count > 0)
        {
            return corpus[rng.Next(corpus.Count)];
        }

        var sb = new StringBuilder();
        int n = rng.Next(1, 40);
        for (int i = 0; i < n; i++)
        {
            sb.Append(Fragments[rng.Next(Fragments.Length)]);
        }

        return sb.ToString();
    }

    private static string Generate(Random rng, int depth)
    {
        if (depth > 6 || rng.Next(4) == 0)
        {
            return rng.Next(6) switch
            {
                0 => "null",
                1 => "true",
                2 => (rng.NextDouble() * 1000 - 500).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                3 => "\"" + new string((char)('a' + rng.Next(26)), rng.Next(5)) + "\\n\\u00e9\"",
                4 => "-0",
                _ => "1e" + rng.Next(-30, 30).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        int count = rng.Next(0, 4);
        var parts = new List<string>();
        if (rng.Next(2) == 0)
        {
            for (int i = 0; i < count; i++)
            {
                parts.Add(Generate(rng, depth + 1));
            }

            return "[" + string.Join(rng.Next(2) == 0 ? "," : " , ", parts) + "]";
        }

        for (int i = 0; i < count; i++)
        {
            parts.Add("\"k" + rng.Next(5).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\":" + Generate(rng, depth + 1));
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static string Mutate(string s, Random rng)
    {
        var sb = new StringBuilder(s);
        int edits = rng.Next(0, 4);
        for (int i = 0; i < edits; i++)
        {
            int pos = sb.Length == 0 ? 0 : rng.Next(sb.Length + 1);
            switch (rng.Next(3))
            {
                case 0 when pos < sb.Length:
                    sb.Remove(pos, Math.Min(rng.Next(1, 4), sb.Length - pos));
                    break;
                case 1:
                    sb.Insert(pos, Fragments[rng.Next(Fragments.Length)]);
                    break;
                default:
                    if (pos < sb.Length)
                    {
                        sb[pos] = (char)rng.Next(0x20, 0x7f);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    internal static int RunRandom(int seconds, int seed, string? outDir)
    {
        var rng = new Random(seed);
        var corpus = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            corpus.Add(Generate(rng, 0));
        }

        long executions = 0;
        int findings = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            string text = Mutate(Build(rng, corpus), rng);
            var timer = Stopwatch.StartNew();
            string? failure = Check(text);
            if (failure is null && timer.ElapsedMilliseconds > 2000)
            {
                failure = "slow: " + timer.ElapsedMilliseconds + " ms";
            }

            executions++;
            if (failure is not null)
            {
                findings++;
                Console.Error.WriteLine("finding " + findings + ": " + failure.Split('\n')[0]);
                if (outDir is not null)
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "json-" + findings.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"), text);
                }
            }
        }

        Console.WriteLine($"json: executions={executions} findings={findings} seconds={clock.Elapsed.TotalSeconds:F0}");
        return findings == 0 ? 0 : 1;
    }
}
