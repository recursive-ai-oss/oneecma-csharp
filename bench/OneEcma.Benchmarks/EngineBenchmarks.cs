using BenchmarkDotNet.Attributes;

namespace RecursiveAI.OneEcma.Benchmarks;

/// <summary>
/// The same source on this engine and on Jint, each iteration in a fresh engine, so setup, parsing, compilation and execution are
/// all inside the number. Rows this engine cannot run yet (missing built-ins) are left out of the run and listed on the console.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
public class EngineBenchmarks
{
    private string _source = string.Empty;
    private double _expected;

    /// <summary>Name of the workload: a small kernel, or lodash, preact (the minified bundle).</summary>
    [ParamsSource(nameof(Workloads))]
    public string Script { get; set; } = "fib25";

    /// <summary>The rows that run on this engine; the others are reported as skipped with the reason.</summary>
    public static IEnumerable<string> Workloads()
    {
        foreach ((string name, string source, double expected) in BenchWorkloads.All())
        {
            string? why = BenchWorkloads.WhyOneEcmaCannotRun(source, expected);
            if (why is null)
            {
                yield return name;
            }
            else
            {
                Console.WriteLine($"SKIPPED row {name}: {why}");
            }
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        (_, _source, _expected) = BenchWorkloads.All().First(x => x.Name == Script);
        double j = Convert.ToDouble(new global::Jint.Engine().Evaluate(_source).ToObject(), System.Globalization.CultureInfo.InvariantCulture);
        if (j != _expected)
        {
            throw new InvalidOperationException($"Jint gave {j} for {Script}, expected {_expected}");
        }
    }

    /// <summary>Jint 4.17.0, the reference.</summary>
    [Benchmark(Baseline = true)]
    public double JintRun() => Convert.ToDouble(new global::Jint.Engine().Evaluate(_source).ToObject(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>This engine.</summary>
    [Benchmark]
    public double OneEcma() => new ScriptEngine().Evaluate(_source).AsNumber;
}

internal static class BenchWorkloads
{
    internal static IEnumerable<(string Name, string Source, double Expected)> All()
    {
        yield return ("startup", "1 + 1;", 2.0);
        foreach ((string name, string source, object expected) in BenchScripts.All)
        {
            yield return (name, source, (double)expected);
        }

        string dir = Environment.GetEnvironmentVariable("ONEECMA_BENCH_INPUTS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "oneecma", "bench-inputs");
        string lodash = Path.Combine(dir, "lodash-4.18.1", "lodash.js");
        if (File.Exists(lodash))
        {
            // Utility-library workload: load lodash, then chain a few collection functions.
            double sum = 0;
            for (int n = 1; n < 2000; n++)
            {
                if (n % 3 == 0)
                {
                    sum += (double)n * n;
                }
            }

            yield return ("lodash", File.ReadAllText(lodash) + "\n_.sum(_.map(_.filter(_.range(1, 2000), function (n) { return n % 3 === 0; }), function (n) { return n * n; })) + _.keys(_.groupBy(_.range(500), function (n) { return n % 7; })).length;", sum + 7);
        }

        string preact = Path.Combine(dir, "preact-11.0.2", "dist", "preact.mjs");
        if (File.Exists(preact))
        {
            // The minified bundle: parse and run it, then build a small virtual tree. The module's trailing export list becomes an
            // object so both engines run it as a plain script.
            string text = File.ReadAllText(preact);
            int e = text.LastIndexOf("export{", StringComparison.Ordinal);
            int close = e < 0 ? -1 : text.IndexOf('}', e);
            if (e >= 0 && close > e)
            {
                var members = new List<string>();
                foreach (string item in text.Substring(e + 7, close - e - 7).Split(','))
                {
                    string[] p = item.Split(" as ", StringSplitOptions.TrimEntries);
                    members.Add(p.Length == 2 ? p[1] + ":" + p[0] : p[0] + ":" + p[0]);
                }

                string script = text[..e] + "var preact = {" + string.Join(",", members) + "};\n" + "(function () { var v = preact.h('div', { id: 'a' }, preact.h('span', null, 'x'), preact.h('span', null, 'y')); return v.props.children.length; })();";
                yield return ("preact", script, 2.0);
            }
        }
    }

    /// <summary>Runs the source once on this engine; returns why it cannot (message of the failure), or null when the result is right.</summary>
    internal static string? WhyOneEcmaCannotRun(string source, double expected)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { WallClockMs = 60_000 } });
            JsValue v = engine.Evaluate(source, cancellationToken: cts.Token);
            return v.IsNumber && v.AsNumber == expected ? null : "wrong result " + engine.ToDisplayString(v);
        }
        catch (JsException e)
        {
            return e.Report?.Headline ?? e.Message;
        }
        catch (Exception e) when (e is JsLimitException or InvalidOperationException or OperationCanceledException)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }
}
