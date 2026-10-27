using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Record, replay, compare. Every mutant script runs once with recording (the default) against a host that supplies a clock,
/// random numbers and two global functions, then the log is replayed with full tracing in a fresh engine. The completion value, the
/// exception report (text and JSON) or the limit must be identical, and the replay must not diverge from the log. A difference, an
/// exception that is not a script error, or a hang is a finding.
/// Half of the mutants run as the body of an async function (<c>EvaluateAsyncBody</c>), with a host function that returns tasks that are
/// complete, faulted, cancelled, throw at once or finish on another thread, and the host waits for the body's promise; the outcome is
/// what the wait produced, and the replay's wait on the same promise must produce the same.
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

    private static JsValue TaskFor(ScriptEngine engine, ReadOnlySpan<JsValue> a)
    {
        int kind = a.Length > 0 && a[0].IsNumber ? (int)a[0].AsNumber & 7 : 0;
        JsRealm realm = engine.MainRealm;
        static JsValue Obj(JsRealm r, int n)
        {
            JsObject o = r.NewObject();
            r.DefineData(o, "id", JsValue.FromNumber(n));
            r.DefineData(o, "items", JsValue.FromObject(r.NewArray([JsValue.FromNumber(1), JsValue.FromNumber(2)])));
            return JsValue.FromObject(o);
        }

        return kind switch
        {
            0 or 1 => JsMarshal.FromTask(realm, "hostTask", () => Task.FromResult(kind), Obj),
            2 => JsMarshal.FromTask(realm, "hostTask", () => Task.FromException<int>(new InvalidOperationException("faulted")), Obj),
            3 => JsMarshal.FromTask(realm, "hostTask", () => Task.FromCanceled<int>(new CancellationToken(true)), Obj),
            4 => JsMarshal.FromTask<int>(realm, "hostTask", () => throw new InvalidOperationException("threw at once"), Obj),
            5 => JsMarshal.FromTask(realm, "hostTask", () => Task.CompletedTask),
            _ => JsMarshal.FromTask(realm, "hostTask", async () =>
            {
                await Task.Yield();
                return kind;
            }, Obj),
        };
    }

    private static JsValue EachCallback(ScriptEngine engine, ReadOnlySpan<JsValue> a, bool swallow)
    {
        if (a.Length < 2 || a[0].Ref is not JsObject items)
        {
            return JsValue.Undefined;
        }

        JsRealm realm = engine.MainRealm;
        int n = (int)Math.Min(8, Math.Max(0, realm.ToNumber(engine.GetProperty(items, "length"))));
        int done = 0;
        for (int i = 0; i < n; i++)
        {
            try
            {
                realm.Call(a[1], default, engine.GetProperty(items, i.ToString(CultureInfo.InvariantCulture)), JsValue.FromNumber(i));
                done++;
            }
            catch (JsException) when (swallow)
            {
            }
        }

        return JsValue.FromNumber(done);
    }

    private static string BodyOutcome(ScriptEngine engine, Func<JsValue> run)
    {
        try
        {
            return Outcome(engine, run(), null, null);
        }
        catch (JsException e)
        {
            return Outcome(engine, default, e, null);
        }
        catch (JsLimitException e)
        {
            return Outcome(engine, default, null, e);
        }
    }

    internal static string? Check(string source)
    {
        // The mode depends on the text only, so a finding replays the same way: text valid only as a body runs as a body,
        // text valid either way runs as a body half of the time.
        bool scriptOk = !RecursiveAI.OneEcma.Syntax.JsParser.Parse(source).HasErrors;
        bool bodyOk = ScriptEngine.CheckAsyncBody(source).Count == 0;
        int h = 17;
        foreach (char c in source)
        {
            h = unchecked(h * 31 + c);
        }

        return CheckMode(source, bodyOk && (!scriptOk || (h & 1) == 1));
    }

    internal static string? CheckMode(string source, bool body)
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

        engine.DefineGlobalFunction("hostTask", 1, (_, a) => TaskFor(engine, a));

        // Callbacks into script: hostEach(items, fn) calls fn for every element and lets the first error escape (hostEachSafe
        // swallows it); hostOn(fn) keeps a handler the harness fires after the script has finished.
        JsValue handler = default;
        engine.DefineGlobalFunction("hostEach", 2, (_, a) => EachCallback(engine, a, swallow: false));
        engine.DefineGlobalFunction("hostEachSafe", 2, (_, a) => EachCallback(engine, a, swallow: true));
        engine.DefineGlobalFunction("hostOn", 1, (_, a) =>
        {
            handler = a.Length > 0 ? a[0] : default;
            return JsValue.Undefined;
        });

        string original;
        try
        {
            if (body)
            {
                original = BodyOutcome(engine, () =>
                {
                    JsValue p = engine.EvaluateAsyncBody(source, "fuzz.js");
                    return engine.Await(p);
                });
            }
            else
            {
                JsValue v = engine.Evaluate(source, "fuzz.js");
                original = Outcome(engine, v, null, null);
            }
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

        bool handlerRan = false;
        if (!body && original.StartsWith("value", StringComparison.Ordinal) && JsRealm.IsCallable(handler) && engine.RecordLog.IsReplayable)
        {
            // A handler fired by the host between entry calls is an entry call of its own; the replay must end in the same outcome.
            JsObject ev = engine.MainRealm.NewObject();
            engine.MainRealm.DefineData(ev, "n", JsValue.FromNumber(5));
            original = BodyOutcome(engine, () => engine.MainRealm.Call(handler, default, JsValue.FromObject(ev), JsValue.FromNumber(2)));
            handlerRan = true;
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

            string replayed;
            if (body && r.Exception is null && r.Limit is null && r.Engine.EntryPromise(0) is { } promise)
            {
                replayed = BodyOutcome(r.Engine, () => r.Engine.Await(JsValue.FromObject(promise)));
            }
            else
            {
                replayed = Outcome(r.Engine, r.Value, r.Exception, r.Limit);
            }

            if (handlerRan && r.Entries < 2)
            {
                return "replay skipped the handler entry call";
            }

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
            "const seen = []; hostEach([1, 2, 3], (x, i) => { seen.push(x * i); }); seen.length + hostEachSafe([1, 2], (x) => { if (x === 1) throw new Error('one'); seen.push(x); })",
            "const cfg = { name: 'x' }; hostEach([cfg], (c) => c.opts.depth);",
            "const rows = [{ id: 1 }, { id: 2 }]; hostEach(rows, (r) => { r.seen = true; hostNum(r.id); }); rows.map(r => r.seen).missing.x;",
            "globalThis.log = []; hostOn((e, k) => { log.push(e.n + k); return log.length; }); 1",
            "hostOn((e) => e.missing.deeper); 1",
            "const t = Date.now(); const r = Math.random(); ({ t, r, n: hostNum(2) }).missing.x;",
            "async function g() { const a = await Promise.resolve(hostObj()); return a.idd.toString(); } g();",
            "let s = 0; for (let i = 0; i < 50; i++) { s += hostNum(i); } s.foo();",
            "const a = await hostTask(0);\nconst b = await hostTask(7);\nreturn a.itms.length + b.id;",
            "try { await hostTask(2); } catch (e) { return e.message; }\ntry { await hostTask(3); } catch (e) { return e.message; }\nawait hostTask(4);",
            "async function f(n) { const o = await hostTask(n); return o.items.map(x => x * o.idd); }\nreturn await Promise.all([f(0), f(6), f(1)]);",
            "const r = await Promise.allSettled([hostTask(0), hostTask(2), hostTask(5), hostTask(6)]);\nreturn r.map(x => x.status).join() + r[0].value.missing.deep;",
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
                // The input may hold lone surrogates, which a UTF-8 file cannot carry: keep it as a JSON string.
                string path = Path.Combine(outDir, "replay-" + findings.ToString(CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(m));
                File.WriteAllText(path + ".txt", failure, new System.Text.UTF8Encoding(false, false));
                Console.Error.WriteLine("finding " + findings + ": " + System.Text.Json.JsonSerializer.Serialize(failure.Length > 3000 ? failure[..3000] : failure));
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
