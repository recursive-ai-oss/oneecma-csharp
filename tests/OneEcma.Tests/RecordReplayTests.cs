using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

public sealed class RecordReplayTests
{
    private sealed class CountingHost : DefaultHost
    {
        internal int Clock;
        internal int Random;
        internal double Now = 1_700_000_000_000;

        public override double CurrentTimeMs()
        {
            Clock++;
            return Now += 7;
        }

        public override double NextRandom()
        {
            Random++;
            return base.NextRandom();
        }
    }

    private static string Show(ScriptEngine e, JsValue v) => e.ToDisplayString(v);

    [Fact]
    public void ClockAndRandomAreRecordedAndReplayedWithoutTheHost()
    {
        var host = new CountingHost();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host });
        const string src = "const a = Date.now(); const b = new Date().getTime(); [a, b, Math.random(), Math.random(), a < b].join()";
        string original = Show(engine, engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken));
        Assert.True(engine.RecordLog.IsReplayable);

        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(original, Show(r.Engine, r.Value));
        Assert.Equal(2, host.Clock);
        Assert.Equal(2, host.Random);
    }

    [Fact]
    public void ALogSurvivesSerializationAndRejectsDamage()
    {
        var engine = new ScriptEngine();
        string original = Show(engine, engine.Evaluate("Math.random() + ':' + Date.now()", cancellationToken: TestContext.Current.CancellationToken));
        byte[] bytes = engine.RecordLog.ToArray();
        RecordLog loaded = RecordLog.FromArray(bytes);
        ReplayResult r = ScriptEngine.Replay(loaded, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(original, Show(r.Engine, r.Value));
        Assert.Equal(engine.RecordLog.EventCount, loaded.EventCount);

        Assert.Throws<FormatException>(() => RecordLog.FromArray(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Throws<FormatException>(() => RecordLog.FromArray(bytes.AsSpan(0, bytes.Length - 3)));
        byte[] future = (byte[])bytes.Clone();
        future[4] = 99;
        Assert.Throws<FormatException>(() => RecordLog.FromArray(future));
    }

    [Fact]
    public void HostFunctionResultsAreReplayedFromTheLogAlone()
    {
        int calls = 0;
        var engine = new ScriptEngine();
        engine.DefineGlobalFunction("getOrder", 1, (_, a) =>
        {
            calls++;
            JsRealm realm = engine.MainRealm;
            JsObject order = realm.NewObject();
            realm.DefineData(order, "id", a[0]);
            realm.DefineData(order, "lines", JsValue.FromObject(realm.NewArray([JsValue.FromNumber(5), JsValue.FromNumber(7)])));
            return JsValue.FromObject(order);
        });
        engine.DefineGlobalFunction("boom", 0, (_, _) => throw new KeyNotFoundException("no such order"));
        const string src = "const o = getOrder(4); let m = ''; try { boom(); } catch (e) { m = e.message + '|' + e.dotnetType; } JSON.stringify(o) + m";
        string original = Show(engine, engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);

        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(original, Show(r.Engine, r.Value));
        Assert.Equal(1, calls);
        Assert.Contains("KeyNotFoundException", original, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncHostMethodsSettleAtTheirRecordedPlace()
    {
        // Each call returns a task the test completes by hand, so the order of settlement is fixed without any real delay.
        var engine = new ScriptEngine();
        var pending = new List<(double N, TaskCompletionSource<double> Done)>();
        engine.DefineGlobalFunction("later", 1, (_, a) =>
        {
            double n = engine.MainRealm.ToNumber(a[0]);
            var done = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((n, done));
            return JsMarshal.FromTask(engine.MainRealm, "later", async () =>
            {
                double v = await done.Task;
                if (n > 20)
                {
                    throw new InvalidOperationException("too slow: " + n);
                }

                return v * 2;
            }, (_, v) => JsValue.FromNumber(v));
        });
        const string src = "globalThis.out = []; globalThis.aDone = new Promise(r => { globalThis.resolveA = r; }); later(5).then(v => { out.push('a' + v); resolveA(); }); later(250).catch(e => out.push('b ' + e.message)); Promise.resolve().then(() => out.push('first'));";
        engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken);
        Assert.Equal(2, pending.Count);
        pending[0].Done.SetResult(5);
        await engine.AwaitAsync(engine.Evaluate("aDone", cancellationToken: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        pending[1].Done.SetResult(250);
        await engine.RunUntilIdleAsync(TestContext.Current.CancellationToken);
        string recorded = Show(engine, engine.Evaluate("out.join('|')", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("first|a10|b InvalidOperationException: too slow: 250", recorded);
        Assert.True(engine.RecordLog.IsReplayable);

        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(recorded, Show(r.Engine, r.Value));
    }

    [Fact]
    public void TimersDrivenByEntryCallsReplayInTheSameOrder()
    {
        var host = new CountingHost();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host });
        engine.Evaluate("""
            globalThis.seen = [];
            globalThis.timers = [];
            globalThis.setTimeout = (fn, ms) => { timers.push({ fn, at: Date.now() + ms }); };
            globalThis.fire = () => { const t = Date.now(); const due = timers.filter(x => x.at <= t).sort((p, q) => p.at - q.at); timers = timers.filter(x => x.at > t); for (const x of due) x.fn(); };
            setTimeout(() => { seen.push('late'); Promise.resolve().then(() => seen.push('late-job')); }, 30);
            setTimeout(() => seen.push('early'), 10);
            """, "timers.js", TestContext.Current.CancellationToken);
        for (int i = 0; i < 6; i++)
        {
            engine.Evaluate("fire()", "tick.js", TestContext.Current.CancellationToken);
        }

        string recorded = Show(engine, engine.Evaluate("seen.join()", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("early,late,late-job", recorded);
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(8, r.Entries);
        Assert.Equal(recorded, Show(r.Engine, r.Value));
    }

    [Fact]
    public void ARunThatCallsScriptDirectlyIsMarkedNotReplayable()
    {
        var engine = new ScriptEngine();
        JsValue fn = engine.Evaluate("(x) => x + 1", cancellationToken: TestContext.Current.CancellationToken);
        engine.Call(fn, default, [JsValue.FromNumber(1)], TestContext.Current.CancellationToken);
        Assert.False(engine.RecordLog.IsReplayable);
        Assert.Contains("never passed to a host function", engine.RecordLog.NotReplayableReason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ScriptEngine.Replay(engine.RecordLog, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReplayReproducesTheReportBytesOfAFailure()
    {
        const string src = "const rows = [];\nfunction f(o) { return o.a.b; }\nf({});";
        var engine = new ScriptEngine(new ScriptEngineOptions { ExplainErrors = false });
        JsException original = Assert.Throws<JsException>(() => engine.Evaluate(src, "case.js", TestContext.Current.CancellationToken));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Exception);
        Assert.Equal(original.Report!.ToString(), r.Report!.ToString());
        Assert.Equal(Show(engine, original.Value), Show(r.Engine, r.Exception!.Value));
    }

    [Fact]
    public void ADifferentProgramIsReportedAsDivergence()
    {
        var engine = new ScriptEngine();
        engine.Evaluate("Math.random()", cancellationToken: TestContext.Current.CancellationToken);
        // Hand-edit the log: the recorded script no longer asks for a random number first.
        byte[] bytes = engine.RecordLog.ToArray();
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("Math.random()", StringComparison.Ordinal);
        bytes[at] = (byte)'M';
        bytes[at + 5] = (byte)'x';
        ReplayResult r = ScriptEngine.Replay(RecordLog.FromArray(bytes), new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.NotNull(r.Divergence);
    }
}
