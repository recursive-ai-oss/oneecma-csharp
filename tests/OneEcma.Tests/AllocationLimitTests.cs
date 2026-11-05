namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// The allocation budget is charged before the engine allocates: a script that asks for, or keeps growing, array storage, typed-array
/// buffers, collections, strings or property storage ends in a memory-limit report instead of taking the host's memory.
/// </summary>
public class AllocationLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EngineLimits Small() => new() { MaxAllocatedBytes = 64L << 20 };

    public static TheoryData<string, string> Scripts() => new()
    {
        // The three reported cases.
        { "fill", "new Array(1e9).fill(0)" },
        { "from", "Array.from({ length: 1e9 })" },
        { "push", "const a = []; for (;;) a.push(0);" },

        // Array growth paths.
        { "index append", "const a = []; for (;;) a[a.length] = 0;" },
        { "length setter then fill", "const a = []; a.length = 4e9; a.fill(0)" },
        { "holey to dense", "const a = [,]; for (let i = 0; ; i++) { a[a.length + 1] = i; }" },
        { "new Array(n) then write", "const a = new Array(1e9); for (let i = 0; ; i++) a[i] = i;" },
        { "array literal spread doubling", "let a = [0]; for (;;) a = [...a, ...a];" },
        { "concat doubling", "let a = [0]; for (;;) a = a.concat(a);" },
        { "flat", "let a = [0]; for (;;) a = [a, a].flat();" },
        { "splice growth", "const a = [0]; for (;;) a.splice(a.length, 0, ...a);" },
        { "Array.of spread", "Array.of(...new Array(5e6).fill(0), ...new Array(5e6).fill(0))" },
        { "toReversed", "new Array(1e9).toReversed()" },
        { "toSorted", "new Array(1e9).toSorted()" },
        { "with", "new Array(1e9).with(0, 1)" },
        { "toSpliced", "new Array(1e9).toSpliced(0, 0)" },
        { "rest from an endless iterator", "const [...r] = { [Symbol.iterator]: function* () { for (;;) yield 0; } };" },
        { "Array.from endless iterable", "Array.from({ [Symbol.iterator]: function* () { for (;;) yield 0; } })" },
        { "regexp match array", "'x'.repeat(1 << 24).match(/x/g)" },
        { "split", "'x'.repeat(1 << 24).split('')" },

        // Typed arrays and buffers.
        { "Uint8Array", "new Uint8Array(2 ** 30)" },
        { "Float64Array", "new Float64Array(2 ** 27)" },
        { "ArrayBuffer", "new ArrayBuffer(2 ** 30)" },
        { "buffers in a loop", "const keep = []; for (;;) keep.push(new Uint8Array(1 << 20));" },
        { "TypedArray.from array-like", "Uint8Array.from({ length: 2 ** 30 })" },
        { "TypedArray.of", "Float64Array.of(...new Array(5e6).fill(1), ...new Array(5e6).fill(1))" },
        { "typed array slice", "const t = new Uint8Array(1 << 25); for (;;) t.slice();" },
        { "typed array toSorted", "const t = new Float64Array(1 << 22); for (;;) t.toSorted();" },
        { "resizable buffer", "const b = new ArrayBuffer(1, { maxByteLength: 2 ** 30 }); b.resize(2 ** 30)" },
        { "hex decode", "Uint8Array.fromHex('ab'.repeat(1 << 25))" },

        // Collections.
        { "Map", "const m = new Map(); for (let i = 0; ; i++) m.set(i, i);" },
        { "Set", "const s = new Set(); for (let i = 0; ; i++) s.add(i);" },
        { "Set from an endless iterable", "new Set({ [Symbol.iterator]: function* () { for (let i = 0; ; i++) yield i; } })" },
        { "WeakMap keys", "const w = new WeakMap(); for (;;) w.set({}, 1);" },
        { "FinalizationRegistry", "const r = new FinalizationRegistry(() => {}); const o = {}; for (;;) r.register(o, 1);" },

        // Strings.
        { "repeat", "'x'.repeat(2 ** 28)" },
        { "padStart", "'x'.padStart(2 ** 28)" },
        { "padEnd", "'x'.padEnd(2 ** 28, 'ab')" },
        { "join", "const s = 'x'.repeat(1 << 20); new Array(200).fill(s).join(s)" },
        { "join of a long array-like", "Array.prototype.join.call({ length: 2e8 }, 'abcd')" },
        { "rope flatten in a loop", "let s = 'x'; for (;;) { s += 'y'.repeat(1000); s.indexOf('q'); }" },
        { "concat then read", "let s = 'x'.repeat(1 << 20); for (;;) { s = s + 'y'; s.charCodeAt(0); }" },
        { "template", "let s = 'x'.repeat(1 << 22); for (;;) { s = `${s}y`; s.charCodeAt(0); }" },
        { "replace", "const s = 'x'.repeat(1 << 20); for (;;) s.replace(/x/g, 'yyyy');" },
        { "replaceAll", "const s = 'x'.repeat(1 << 20); for (;;) s.replaceAll('x', 'yy');" },
        { "JSON.stringify", "const a = new Array(400).fill('x'.repeat(1 << 16)); for (;;) JSON.stringify(a);" },

        // Property storage.
        { "object as a map", "const o = {}; for (let i = 0; ; i++) o['k' + i] = i;" },
        { "many objects with distinct keys", "for (let i = 0; ; i++) { const o = {}; for (let j = 0; j < 60; j++) o['k' + i + '_' + j] = j; }" },
        { "array as a map", "const a = []; for (let i = 0; ; i += 1000) a[i] = i;" },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void EndsInAMemoryLimitReport(string name, string script)
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = Small() });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, name.Replace(' ', '-') + ".js", Ct));
        Assert.True(e.Kind == LimitKind.Memory, name + " ended with " + e.Kind);
        Assert.NotNull(e.Report);
        Assert.Equal("Limit", e.Report.Kind);
        Assert.Contains("allocation limit", e.Report.Headline, StringComparison.Ordinal);
        Assert.True(GC.GetTotalMemory(false) < 1L << 30, name + " held far more than the limit allows");
    }

    [Fact]
    public void ALimitBelowTheRequestStopsBeforeAnythingIsAllocated()
    {
        // 8 MiB cannot hold the 16 GB the script asks for in one step; the request itself is refused.
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { MaxAllocatedBytes = 8L << 20 } });
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<JsLimitException>(() => engine.Evaluate("new Array(1e9).toReversed()", cancellationToken: Ct));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 256L << 20);
    }

    [Fact]
    public void SmallArraysAndObjectsAreUnaffected()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { MaxAllocatedBytes = 4L << 20 } });
        JsValue v = engine.Evaluate("const a = []; for (let i = 0; i < 1000; i++) a.push(i); const m = new Map(); for (let i = 0; i < 500; i++) m.set(i, i); a.length + m.size", cancellationToken: Ct);
        Assert.Equal("1500", engine.ToDisplayString(v));
    }

    [Fact]
    public void ARecordedLimitHitsTheSameChargeInATracedReplay()
    {
        var limits = Small();
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("const a = []; for (let i = 0; ; i++) { a.push(i); if (i % 7 === 0) a.concat([1]).length; }", "grow.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.True(engine.RecordLog.IsReplayable);

        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = Small(), Trace = true }, Ct);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Limit);
        Assert.Equal(LimitKind.Memory, r.Limit.Kind);
        Assert.Equal(e.Report!.Headline, r.Limit.Report!.Headline);
    }

    // These allocate close to the default budget (2 GiB requested, about 1.5 GiB live at the peak), so they stay out of the normal run:
    // dotnet test --filter-explicit on, or run them one at a time.
    [Theory(Explicit = true)]
    [InlineData("new Array(1e9).fill(0)")]
    [InlineData("Array.from({ length: 1e9 })")]
    [InlineData("const a = []; for (;;) a.push(0);")]
    [InlineData("const k = []; for (;;) k.push(new Uint8Array(2 ** 28));")]
    public void DefaultLimitsEndInAMemoryReportNotAnOutOfMemoryError(string script)
    {
        var engine = new ScriptEngine();
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "default.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }
}
