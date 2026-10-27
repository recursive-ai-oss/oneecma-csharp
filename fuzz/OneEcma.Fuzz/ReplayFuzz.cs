using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Record, replay, compare. Every mutant script runs once with recording (the default) against a host that supplies a clock,
/// random numbers and two global functions, then the log is replayed with full tracing in a fresh engine. The completion value, the
/// exception report (text and JSON) or the limit must be identical, and the replay must not diverge from the log. A difference, an
/// exception that is not a script error, or a hang is a finding.
/// usage: replay &lt;seconds&gt; &lt;outdir&gt; [seed] [seeddir...]; replay-file &lt;path&gt; checks one input.
/// </summary>
internal static class ReplayFuzz
{
    private static long _compared;
    private static long _threw;
    private static long _limits;
    private static long _skipped;

    private static EngineLimits NewLimits() => new() { Fuel = 300_000, MaxAllocatedBytes = 24L << 20, MaxCallDepth = 2000, MaxStringLength = 1 << 22 };

    private sealed class ClockHost : DefaultHost
    {
        private double _now = 1_700_000_000_000;

        public ClockHost()
            : base(11)
        {
        }

        public override double CurrentTimeMs() => _now += 13;
    }

    private static string Outcome(ScriptEngine engine, JsValue value, JsException? error, JsLimitException? limit)
    {
        if (error is not null)
        {
            return "throw: " + error.Report!.ToString() + DiagnosticJson.Render([error.Report]);
        }

        if (limit is not null)
        {
            return "limit " + limit.Kind + ": " + limit.Message;
        }

        return "value: " + engine.ToDisplayString(value);
    }

    internal static string? Check(string source)
    {
        EngineLimits limits = NewLimits();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new ClockHost(), Limits = limits, InstallTest262Host = true, ThrowOnUnhandledRejection = false });
        int calls = 0;
        engine.DefineGlobalFunction("hostNum", 1, (_, a) => JsValue.FromNumber(++calls * 3 + (a.Length > 0 && a[0].IsNumber ? a[0].AsNumber : 0)));
        engine.DefineGlobalFunction("hostObj", 0, (_, _) =>
        {
            JsRealm realm = engine.MainRealm;
            JsObject o = realm.NewObject();
            realm.DefineData(o, "id", JsValue.FromNumber(++calls));
            realm.DefineData(o, "items", JsValue.FromObject(realm.NewArray([JsValue.FromNumber(1), JsValue.FromNumber(2)])));
            return JsValue.FromObject(o);
        });

        string original;
        try
        {
            JsValue v = engine.Evaluate(source, "fuzz.js");
            original = Outcome(engine, v, null, null);
        }
        catch (JsException e)
        {
            original = Outcome(engine, default, e, null);
        }
        catch (JsLimitException e)
        {
            original = Outcome(engine, default, null, e);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            return "recording run threw " + e.GetType().Name + ": " + e.Message + "\n" + (e.InnerException ?? e).StackTrace;
        }

        if (!engine.RecordLog.IsReplayable)
        {
            Interlocked.Increment(ref _skipped);
            return null;
        }

        try
        {
            ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = limits, ThrowOnUnhandledRejection = false, InstallTest262Host = true });
            if (r.Divergence is not null)
            {
                return "replay diverged: " + r.Divergence;
            }

            string replayed = Outcome(r.Engine, r.Value, r.Exception, r.Limit);
            Interlocked.Increment(ref _compared);
            if (original.StartsWith("throw", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _threw);
            }
            else if (original.StartsWith("limit", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _limits);
            }

            return replayed == original ? null : "replay differs\n--- recorded\n" + original + "\n--- replayed\n" + replayed;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return "replay threw " + e.GetType().Name + ": " + e.Message + "\n" + (e.InnerException ?? e).StackTrace;
        }
    }

    /// <summary>Runs one input on a worker thread with a deadline.</summary>
    internal static string? RunOne(string source, int deadlineSeconds)
    {
        string? result = null;
        var t = new Thread(() => result = Check(source), 16 * 1024 * 1024);
        t.Start();
        return t.Join(TimeSpan.FromSeconds(deadlineSeconds)) ? result : "hang: no result after " + deadlineSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";
    }

    internal static int RunRandom(int seconds, string outDir, int seed, IEnumerable<string> seedDirs)
    {
        Directory.CreateDirectory(outDir);
        var seeds = new List<string>
        {
            "const o = hostObj(); function f(x) { return x.items.map((n) => n * hostNum(n)); } f(o);",
            "const o = hostObj(); o.itms.length;",
            "const t = Date.now(); const r = Math.random(); ({ t, r, n: hostNum(2) }).missing.x;",
            "async function g() { const a = await Promise.resolve(hostObj()); return a.idd.toString(); } g();",
            "let s = 0; for (let i = 0; i < 50; i++) { s += hostNum(i); } s.foo();",
        };
        foreach (string d in seedDirs)
        {
            foreach (string f in Directory.EnumerateFiles(d, "*.js", SearchOption.AllDirectories))
            {
                if (new FileInfo(f).Length < 32 * 1024 && !f.Contains("_FIXTURE", StringComparison.Ordinal))
                {
                    seeds.Add(File.ReadAllText(f));
                }
            }
        }

        var rng = new Random(seed);
        long executions = 0;
        int findings = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            string m = InterpFuzz.Mutate(seeds[rng.Next(seeds.Count)], rng);
            string? failure = RunOne(m, 30);
            executions++;
            if (failure is not null)
            {
                findings++;
                string path = Path.Combine(outDir, "replay-" + findings.ToString(CultureInfo.InvariantCulture) + ".js");
                File.WriteAllText(path, m);
                File.WriteAllText(path + ".txt", failure);
                Console.Error.WriteLine("finding " + findings + ": " + failure.Split('\n')[0]);
                if (failure.StartsWith("hang", StringComparison.Ordinal))
                {
                    Console.WriteLine($"replay: executions={executions} findings={findings} (stopped on a hang)");
                    return 1;
                }
            }
        }

        Console.WriteLine($"replay: executions={executions} findings={findings} seeds={seeds.Count} compared={_compared} threw={_threw} limits={_limits} notReplayable={_skipped} seconds={clock.Elapsed.TotalSeconds:F0}");
        return findings == 0 ? 0 : 1;
    }
}
