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
    private object _expected = 0.0;

    /// <summary>Name of the workload: a small kernel, or lodash, preact (the minified bundle).</summary>
    [ParamsSource(nameof(Workloads))]
    public string Script { get; set; } = "fib25";

    /// <summary>The rows that run on this engine; the others are reported as skipped with the reason.</summary>
    public static IEnumerable<string> Workloads()
    {
        foreach ((string name, string source, object expected) in BenchWorkloads.All())
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
        string j = JintRun();
        if (!BenchWorkloads.Matches(_expected, j))
        {
            throw new InvalidOperationException($"Jint gave a different result for {Script}");
        }
    }

    /// <summary>Jint 4.17.0, the reference.</summary>
    [Benchmark(Baseline = true)]
    public string JintRun() => new global::Jint.Engine().Evaluate(_source).ToString();

    /// <summary>This engine.</summary>
    [Benchmark]
    public string OneEcma()
    {
        var engine = new ScriptEngine();
        return engine.ToDisplayString(engine.Evaluate(_source));
    }
}

internal static class BenchWorkloads
{
    internal static IEnumerable<(string Name, string Source, object Expected)> All()
    {
        yield return ("startup", "1 + 1;", 2.0);
        foreach ((string name, string source, object expected) in BenchScripts.All)
        {
            yield return (name, source, expected);
        }

        foreach ((string name, string source) in BundleWorkloads.All())
        {
            yield return (name, source, BundleWorkloads.NodeResult(source));
        }
    }

    /// <summary>True when the engine's string form of the completion value equals the expected one (a number for the kernels, Node's string for the bundles).</summary>
    internal static bool Matches(object expected, string actual) => expected is double d
        ? double.TryParse(actual, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a) && a == d
        : actual == (string)expected;

    /// <summary>Runs the source once on this engine; returns why it cannot (message of the failure), or null when the result is right.</summary>
    internal static string? WhyOneEcmaCannotRun(string source, object expected)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { WallClockMs = 60_000 } });
            JsValue v = engine.Evaluate(source, cancellationToken: cts.Token);
            string text = engine.ToDisplayString(v);
            return Matches(expected, text) ? null : "wrong result " + (text.Length > 200 ? text[..200] + "..." : text);
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
