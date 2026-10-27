namespace RecursiveAI.OneEcma.Tests;

/// <summary>Callbacks from .NET into script are part of the record log, so a run that used them replays and its errors are explained.</summary>
public sealed class CallbackReplayTests
{
    private static string Show(ScriptEngine e, JsValue v) => e.ToDisplayString(v);

    /// <summary>A host function <c>each(items, fn)</c> that calls <c>fn(item, index)</c> for every element and returns the count.</summary>
    private static void DefineEach(ScriptEngine engine, bool swallow = false)
    {
        engine.DefineGlobalFunction("each", 2, (_, a) =>
        {
            JsRealm realm = engine.MainRealm;
            var items = (JsObject)a[0].Ref!;
            int n = (int)realm.ToNumber(engine.GetProperty(items, "length"));
            for (int i = 0; i < n; i++)
            {
                try
                {
                    realm.Call(a[1], default, engine.GetProperty(items, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), JsValue.FromNumber(i));
                }
                catch (JsException) when (swallow)
                {
                }
            }

            return JsValue.FromNumber(n);
        });
    }

    [Fact]
    public void CallbacksReplayWithTheirSideEffects()
    {
        int hostRuns = 0;
        var engine = new ScriptEngine();
        DefineEach(engine);
        engine.DefineGlobalFunction("tick", 0, (_, _) =>
        {
            hostRuns++;
            return JsValue.Undefined;
        });
        const string src = "globalThis.seen = []; const o = { n: 0 }; const c = each([{ id: 1 }, { id: 2 }, { id: 3 }], (x, i) => { tick(); o.n += x.id; seen.push(i); }); JSON.stringify([c, o, seen])";
        string original = Show(engine, engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken));
        Assert.Equal("[3,{\"n\":6},[0,1,2]]", original);
        Assert.True(engine.RecordLog.IsReplayable, engine.RecordLog.NotReplayableReason);

        // The replay does not run each(); the callbacks (and the host function they call) are answered from the log.
        int before = hostRuns;
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false, Configure = e => DefineEach(e) }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(original, Show(r.Engine, r.Value));
        Assert.Equal(before, hostRuns);
        byte[] bytes = engine.RecordLog.ToArray();
        Assert.Equal(bytes, RecordLog.FromArray(bytes).ToArray());
    }

    [Fact]
    public void ACallbackThatMutatesAnObjectScriptPassedKeepsItsIdentity()
    {
        var engine = new ScriptEngine();
        engine.DefineGlobalFunction("visit", 2, (_, a) =>
        {
            engine.MainRealm.Call(a[1], default, a[0]);
            return JsValue.Undefined;
        });
        const string src = "const row = { seen: false }; visit(row, r => { r.seen = true; }); row.seen";
        Assert.Equal("true", Show(engine, engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken)));
        Assert.True(engine.RecordLog.IsReplayable, engine.RecordLog.NotReplayableReason);
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal("true", Show(r.Engine, r.Value));
    }

    [Fact]
    public void AnErrorAfterCallbacksIsExplainedByReplay()
    {
        var engine = new ScriptEngine();
        DefineEach(engine);
        const string src = "const order = { id: 7, lineItems: [] };\neach([1, 2], (x) => { order.lineItems.push(x); });\nconst total = order.items.reduce((s, i) => s + i, 0);";
        JsException e = Assert.Throws<JsException>(() => engine.Evaluate(src, "case.js", TestContext.Current.CancellationToken));
        string text = Diagnostics.DiagnosticRenderer.Render(e.Report!);
        Assert.DoesNotContain("explanation unavailable", text, StringComparison.Ordinal);
        Assert.Contains("did you mean `order.lineItems`", text, StringComparison.Ordinal);
        Assert.Contains("(Array, 2 items)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallbackErrorThatEscapesTheHostIsExplainedAndOneTheHostSwallowsReplays()
    {
        var escaping = new ScriptEngine();
        DefineEach(escaping);
        const string src = "const cfg = { name: 'x' };\neach([1], (x) => { return cfg.opts.depth; });";
        JsException e = Assert.Throws<JsException>(() => escaping.Evaluate(src, "case.js", TestContext.Current.CancellationToken));
        string text = Diagnostics.DiagnosticRenderer.Render(e.Report!);
        Assert.DoesNotContain("explanation unavailable", text, StringComparison.Ordinal);
        Assert.Contains("`cfg.opts` is undefined", text, StringComparison.Ordinal);

        var swallowing = new ScriptEngine();
        DefineEach(swallowing, swallow: true);
        const string src2 = "let log = []; each([1, 2], (x) => { if (x === 1) throw new Error('first'); log.push(x); }); log.join()";
        string original = Show(swallowing, swallowing.Evaluate(src2, "t.js", TestContext.Current.CancellationToken));
        Assert.Equal("2", original);
        ReplayResult r = ScriptEngine.Replay(swallowing.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(original, Show(r.Engine, r.Value));
    }

    [Fact]
    public void AHandlerFiredByTheHostBetweenEntryCallsReplays()
    {
        var engine = new ScriptEngine();
        JsValue handler = default;
        engine.DefineGlobalFunction("on", 1, (_, a) =>
        {
            handler = a[0];
            return JsValue.Undefined;
        });
        engine.Evaluate("globalThis.hits = []; on((e) => { hits.push(e.kind); if (e.kind === 'boom') null.x; });", "t.js", TestContext.Current.CancellationToken);
        JsRealm realm = engine.MainRealm;
        foreach (string kind in new[] { "a", "b" })
        {
            JsObject ev = realm.NewObject();
            realm.DefineData(ev, "kind", JsValue.FromString(kind));
            realm.Call(handler, default, JsValue.FromObject(ev));
        }

        Assert.True(engine.RecordLog.IsReplayable, engine.RecordLog.NotReplayableReason);
        string recorded = Show(engine, engine.Evaluate("hits.join()", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("a,b", recorded);
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(4, r.Entries);
        Assert.Equal(recorded, Show(r.Engine, r.Value));

        // A handler that fails is explained like any entry call.
        JsObject bad = realm.NewObject();
        realm.DefineData(bad, "kind", JsValue.FromString("boom"));
        JsException e = Assert.Throws<JsException>(() => realm.Call(handler, default, JsValue.FromObject(bad)));
        Assert.DoesNotContain("explanation unavailable", Diagnostics.DiagnosticRenderer.Render(e.Report!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallbackMadeAfterAnAwaitInAnAsyncHostMethodReplays()
    {
        var engine = new ScriptEngine();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.DefineGlobalFunction("sumAsync", 1, (_, a) =>
        {
            JsValue fn = a[0];
            return JsMarshal.FromTask(engine.MainRealm, "sumAsync", async () =>
            {
                double sum = 0;
                for (int i = 1; i <= 3; i++)
                {
                    await gate.Task;
                    sum += engine.MainRealm.ToNumber(engine.MainRealm.Call(fn, default, JsValue.FromNumber(i)));
                }

                return sum;
            }, (_, v) => JsValue.FromNumber(v));
        });
        engine.Evaluate("globalThis.calls = []; globalThis.p = sumAsync((i) => { calls.push(i); return i * 10; }); p.then(v => calls.push('sum' + v));", "t.js", TestContext.Current.CancellationToken);
        gate.SetResult();
        await engine.RunUntilIdleAsync(TestContext.Current.CancellationToken);
        string recorded = Show(engine, engine.Evaluate("calls.join()", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("1,2,3,sum60", recorded);
        Assert.True(engine.RecordLog.IsReplayable, engine.RecordLog.NotReplayableReason);
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Trace = false }, TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(recorded, Show(r.Engine, r.Value));
    }

    [Fact]
    public void AFunctionScriptNeverGaveTheHostMakesTheLogNotReplayable()
    {
        var engine = new ScriptEngine();
        JsValue fn = engine.Evaluate("(x) => x + 1", cancellationToken: TestContext.Current.CancellationToken);
        engine.MainRealm.Call(fn, default, JsValue.FromNumber(1));
        Assert.False(engine.RecordLog.IsReplayable);
        Assert.Contains("never passed to a host function", engine.RecordLog.NotReplayableReason, StringComparison.Ordinal);
    }
}
