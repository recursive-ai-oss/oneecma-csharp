using System.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Long-string work (compares, collation), values a host hands to the engine, and out-of-memory conditions all end in limit reports:
/// each costs fuel or allocation budget in proportion to its size, so the clock and the budget see it.
/// Time bounds leave generous slack for slow machines; memory bounds are proportions of the limit.
/// </summary>
public class StringAndBoundaryLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptEngine Engine(long allocated, long fuel = 4_000_000_000L, double wallMs = 0) =>
        new(new ScriptEngineOptions { Limits = new EngineLimits { MaxAllocatedBytes = allocated, Fuel = fuel, WallClockMs = wallMs }, ExplainErrors = false });

    // ---- collation memory ----------------------------------------------------------------------------

    [Theory]
    [InlineData("const s = '\u00e9'.repeat(1e6); s.localeCompare(s + 'a')")]
    [InlineData("const s = 'a\u0301'.repeat(5e5); s.localeCompare(s + 'a', 'de')")]
    [InlineData("const s = '\u00e9'.repeat(1e6); new Intl.Collator().compare(s, s + 'a')")]
    [InlineData("const s = '\u00e9'.repeat(1e6); new Intl.Collator('fr', { numeric: true }).compare(s, s + 'a')")]
    [InlineData("const s = '\u00e9'.repeat(1e6); [s + 'b', s + 'a'].sort(new Intl.Collator().compare)")]
    [InlineData("const s = '\u00e9'.repeat(1e6); [s + 'b', s + 'a'].sort((x, y) => x.localeCompare(y))")]
    public void ALongCollationCompareEndsInAMemoryReport(string script)
    {
        const long Limit = 32L << 20;
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        ScriptEngine engine = Engine(Limit);
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "collate.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        long live = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(engine);
        Assert.True(live < Limit * 2, $"{live >> 20} MiB live against a {Limit >> 20} MiB limit");
    }

    [Fact]
    public void ShortAndModerateCollationStillWorksAndGivesTheBudgetBack()
    {
        // The sort keys of a compare exist only while it runs: many compares do not add up against the budget.
        ScriptEngine engine = Engine(32L << 20);
        JsValue v = engine.Evaluate("""
            const s = '\u00e9'.repeat(20000);
            let n = 0;
            for (let i = 0; i < 100; i++) n += s.localeCompare(s + 'a') + s.localeCompare(s) + 'b'.localeCompare('a');
            n
            """, "moderate.js", Ct);
        Assert.Equal("0", engine.ToDisplayString(v));
    }

    // ---- values the host hands over ------------------------------------------------------------------

    [Fact]
    public void HostStringsKeptInAnArrayEndInAMemoryReport()
    {
        const long Limit = 64L << 20;
        int calls = 0;
        ScriptEngine engine = Engine(Limit);
        engine.DefineGlobalFunction("big", 0, (_, _) =>
        {
            calls++;
            return JsValue.FromString(new string('x', 1 << 20) + calls);
        });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("const keep = []; for (;;) keep.push(big());", "host.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.True(calls < 200, calls + " host calls");
    }

    [Fact]
    public void StringsFromCompletedHostTasksAreCharged()
    {
        int calls = 0;
        ScriptEngine engine = Engine(32L << 20);
        engine.DefineGlobalFunction("fetchBig", 0, (_, _) =>
        {
            calls++;
            return JsMarshal.FromTask(engine.MainRealm, "fetchBig", () => Task.FromResult(new string('z', 1 << 20) + calls), (_, v) => JsValue.FromString(v));
        });
        var e = Assert.Throws<JsLimitException>(() =>
        {
            engine.Evaluate("async function main() { const keep = []; for (;;) keep.push(await fetchBig()); } main();", "tasks.js", Ct);
            engine.RunUntilIdle(Ct);
        });
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.True(calls < 200, calls + " host calls");
    }

    [Fact]
    public void HostObjectsAreChargedByTheirSize()
    {
        int calls = 0;
        ScriptEngine engine = Engine(16L << 20);
        engine.DefineGlobalFunction("rows", 0, (_, _) =>
        {
            calls++;
            JsRealm realm = engine.MainRealm;
            var items = new List<JsValue>();
            for (int i = 0; i < 2000; i++)
            {
                JsObject row = realm.NewObject();
                realm.DefineData(row, "id", JsValue.FromNumber(i));
                realm.DefineData(row, "name", JsValue.FromString("row-" + i));
                items.Add(JsValue.FromObject(row));
            }

            return JsValue.FromObject(realm.NewArray([.. items]));
        });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("const keep = []; for (;;) keep.push(rows());", "rows.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.True(calls < 200, calls + " host calls");
    }

    [Fact]
    public void ARecordedBoundaryChargeIsTheSameInATracedReplay()
    {
        var limits = new EngineLimits { MaxAllocatedBytes = 8L << 20 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits, ExplainErrors = false });
        engine.DefineGlobalFunction("chunk", 0, (_, _) => JsValue.FromString(new string('y', 1 << 16)));
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("const keep = []; for (;;) keep.push(chunk());", "boundary.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.True(engine.RecordLog.IsReplayable);

        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = new EngineLimits { MaxAllocatedBytes = 8L << 20 }, Trace = true }, Ct);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Limit);
        Assert.Equal(LimitKind.Memory, r.Limit.Kind);
        Assert.Equal(e.Report!.Headline, r.Limit.Report!.Headline);
    }

    [Theory]
    [InlineData("const s = '\u00e9'.repeat(3e5); for (;;) s.localeCompare(s + 'a');")]
    [InlineData("const a = 'a'.repeat(5e6); const b = 'a'.repeat(5e6); for (;;) a === b;")]
    [InlineData("const a = 'a'.repeat(5e6) + 'x'; const b = 'a'.repeat(5e6) + 'y'; for (;;) a < b;")]
    [InlineData("const m = new Map(); const k = 'k'.repeat(5e6); m.set(k, 1); for (;;) m.get(k);")]
    public void LongStringWorkReplaysToTheSameFuelLimit(string script)
    {
        var limits = new EngineLimits { Fuel = 50_000_000, MaxAllocatedBytes = 1L << 40 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits, ExplainErrors = false });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "text.js", Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = new EngineLimits { Fuel = 50_000_000, MaxAllocatedBytes = 1L << 40 }, Trace = true }, Ct);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Limit);
        Assert.Equal(LimitKind.Fuel, r.Limit.Kind);
        Assert.Equal(e.Report!.Headline, r.Limit.Report!.Headline);
    }

    // ---- out of memory ---------------------------------------------------------------------------------

    [Fact]
    public void AnOutOfMemoryFromAHostFunctionIsAMemoryLimitScriptCannotCatch()
    {
        bool scriptCaught = false;
        ScriptEngine engine = Engine(1L << 30);
        // An array longer than .NET allows: the runtime raises the real OutOfMemoryException.
        engine.DefineGlobalFunction("hostOom", 0, (_, _) => JsValue.FromNumber(new byte[int.MaxValue].Length));
        engine.DefineGlobalFunction("caught", 0, (_, _) =>
        {
            scriptCaught = true;
            return default;
        });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("try { hostOom(); } catch (e) { caught(); } finally { }", "oom.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.NotNull(e.Report);
        Assert.False(scriptCaught);

        // A real allocation failure under a generous limit is not the budget tripping: the headline names no limit.
        Assert.Equal("Execution ran out of memory: the process could not allocate more", e.Report.Headline);
        Assert.DoesNotContain("allocation limit", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain((1L << 30).ToString(System.Globalization.CultureInfo.InvariantCulture), e.Report.Headline, StringComparison.Ordinal);

        // The budget tripping still says so, with its figure.
        ScriptEngine small = Engine(4L << 20);
        var trip = Assert.Throws<JsLimitException>(() => small.Evaluate("const keep = []; for (;;) keep.push(new Array(1000).fill(0));", "budget.js", Ct));
        Assert.Equal(LimitKind.Memory, trip.Kind);
        Assert.Equal("Execution exceeded the allocation limit of 4194304 bytes", trip.Report!.Headline);
    }

    [Fact]
    public void AnOutOfMemoryFromAHostConstructorOrCallbackIsAlsoAMemoryLimit()
    {
        ScriptEngine engine = Engine(1L << 30);
        engine.DefineGlobalFunction("hostOom", 0, (_, _) => throw new InsufficientMemoryException("no room"));
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("[1].map(() => { try { hostOom(); } catch { return 0; } })", "oom2.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }

    // ---- the clock ---------------------------------------------------------------------------------------

    private static long TimeUntilLimit(ScriptEngine engine, string script, out LimitKind kind)
    {
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "clock.js", Ct));
        kind = e.Kind;
        return sw.ElapsedMilliseconds;
    }

    [Theory]
    [InlineData("const a = 'a'.repeat(1.5e7); const b = 'a'.repeat(1.5e7); for (;;) a === b;")]
    [InlineData("const a = 'a'.repeat(1.5e7); const b = 'a'.repeat(1.5e7); for (;;) a == b;")]
    [InlineData("const a = 'a'.repeat(1.5e7) + 'x'; const b = 'a'.repeat(1.5e7) + 'y'; for (;;) a < b;")]
    [InlineData("const m = new Map(); const k = 'k'.repeat(1.5e7); m.set(k, 1); for (;;) m.get(k);")]
    [InlineData("const s = '\u00e9'.repeat(5e5); for (;;) s.localeCompare(s + 'a');")]
    [InlineData("const s = '\u00e9'.repeat(4e6); for (;;) s.localeCompare(s + 'a');")]
    [InlineData("const s = '\u00e9'.repeat(5e5); const c = new Intl.Collator(); for (;;) c.compare(s, s + 'a');")]
    [InlineData("const a = []; for (let i = 0; i < 100; i++) a.push('a'.repeat(5e5) + i); for (;;) a.slice().sort();")]
    public void LongStringLoopsEndNearTheWallClock(string script)
    {
        ScriptEngine engine = Engine(1L << 40, fuel: long.MaxValue / 4, wallMs: 1000);
        long ms = TimeUntilLimit(engine, script, out LimitKind kind);
        Assert.True(kind is LimitKind.WallClock or LimitKind.Fuel, kind.ToString());
        Assert.True(ms < 4000, ms + " ms against a 1000 ms limit");
    }

    [Fact]
    public void ARepeatedSquaringEndsNearTheWallClock()
    {
        ScriptEngine engine = Engine(1L << 40, fuel: long.MaxValue / 4, wallMs: 1000);
        long ms = TimeUntilLimit(engine, "let b = 3n; for (;;) b = b * b;", out LimitKind kind);
        Assert.Equal(LimitKind.WallClock, kind);
        Assert.True(ms < 4000, ms + " ms against a 1000 ms limit");
    }

    // The same loops with the tight bound the engine aims at; timing-sensitive, so run explicitly on a quiet machine.
    [Theory(Explicit = true)]
    [InlineData("const a = 'a'.repeat(1.5e7); const b = 'a'.repeat(1.5e7); for (;;) a === b;")]
    [InlineData("const s = '\u00e9'.repeat(5e5); for (;;) s.localeCompare(s + 'a');")]
    [InlineData("const s = '\u00e9'.repeat(8e6); for (;;) s.localeCompare(s + 'a');")]
    [InlineData("let b = 3n; for (;;) b = b * b;")]
    public void TightClockBounds(string script)
    {
        ScriptEngine engine = Engine(1L << 40, fuel: long.MaxValue / 4, wallMs: 1000);
        long ms = TimeUntilLimit(engine, script, out LimitKind kind);
        Assert.True(kind is LimitKind.WallClock or LimitKind.Fuel, kind.ToString());
        Assert.True(ms < 1500, ms + " ms against a 1000 ms limit");
    }

    [Fact]
    public void ShortStringsAreStillFree()
    {
        // Equality, ordering and Map keys of short strings spend no extra fuel: the loop below is a few hundred thousand steps.
        ScriptEngine engine = Engine(8L << 20, fuel: 1_000_000);
        JsValue v = engine.Evaluate("""
            const a = 'hello world'.repeat(100), b = 'hello world'.repeat(100), c = a.slice(0, -1) + 'z';
            let n = 0;
            const m = new Map([[a, 1]]);
            for (let i = 0; i < 20000; i++) { if (a === b) n++; if (a < c) n++; n += m.get(b); }
            n
            """, "short.js", Ct);
        Assert.Equal("60000", engine.ToDisplayString(v));
    }
}
