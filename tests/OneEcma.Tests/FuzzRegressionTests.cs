using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Inputs the fuzzers found, reduced to small scripts.</summary>
public sealed class FuzzRegressionTests
{
    [Theory]
    [InlineData("typeof {a = 1};")]
    [InlineData("void {a = 1};")]
    [InlineData("!{a = 1};")]
    [InlineData("delete {a = 1};")]
    [InlineData("++{a = 1}.x;")]
    [InlineData("new {a = 1};")]
    [InlineData("class A extends {a = 1} {}")]
    [InlineData("async function f() { await {a = 1}; }")]
    public void AShorthandInitializerUnderAnOperatorIsASyntaxError(string source)
    {
        // The operand of a unary operator is not a cover grammar for a pattern, so the initializer must be reported, not compiled.
        IReadOnlyList<Diagnostic> errors = ScriptEngine.Check(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(errors, d => d.Headline == "Invalid shorthand property initializer");
    }

    [Fact]
    public void ASourceWithALoneSurrogateReplaysToTheSameReport()
    {
        // A JavaScript string may hold half of a surrogate pair; the record log must carry it unchanged.
        var engine = new ScriptEngine();
        string src = "var s = '\uD801'; s.nope.x;";
        JsException first = Assert.Throws<JsException>(() => engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(first.Report!.ToString(), r.Exception!.Report!.ToString());
        Assert.Contains('\uD801', r.Exception.Report.ToString());

        RecordLog loaded = RecordLog.FromArray(engine.RecordLog.ToArray());
        Assert.Equal(first.Report.ToString(), ScriptEngine.Replay(loaded, cancellationToken: TestContext.Current.CancellationToken).Exception!.Report!.ToString());
    }

    private const string Bottomless = "var it = {}; it[Symbol.iterator] = function() { return { next() { return { done: false, valu: [1, 2] }; } }; };\n";

    [Theory]
    [InlineData("Promise.all(it);")]
    [InlineData("Promise.allSettled(it);")]
    [InlineData("Promise.any(it);")]
    [InlineData("Promise.race(it);")]
    [InlineData("Array.from(it);")]
    [InlineData("[...it];")]
    [InlineData("f(...it); function f() {}")]
    [InlineData("var [a, ...b] = it;")]
    [InlineData("new Map(it);")]
    [InlineData("new Set(it);")]
    [InlineData("new WeakMap(it);")]
    [InlineData("new WeakSet(it);")]
    [InlineData("Object.fromEntries(it);")]
    [InlineData("new Uint8Array(it);")]
    [InlineData("Uint8Array.from(it);")]
    [InlineData("Iterator.from(it).toArray();")]
    [InlineData("Iterator.from(it).forEach(function() {});")]
    [InlineData("Iterator.from(it).some(function() { return false; });")]
    [InlineData("Iterator.from(it).every(function() { return true; });")]
    [InlineData("Iterator.from(it).find(function() { return false; });")]
    [InlineData("Iterator.from(it).reduce(function(a) { return a; }, 0);")]
    [InlineData("Iterator.from(it).drop(1e9).next();")]
    [InlineData("Iterator.from(it).filter(function() { return false; }).next();")]
    [InlineData("Iterator.from(it).flatMap(function() { return []; }).next();")]
    [InlineData("Array.fromAsync(it);")]
    [InlineData("for (var x of it) {}")]
    [InlineData("yieldStar(); function* yieldStar() { yield* it; }; [...yieldStar()];")]
    [InlineData("new Set().union({ size: 1e9, has() { return true; }, keys() { return it[Symbol.iterator](); } });")]
    [InlineData("new Set([1]).isSubsetOf({ size: 1e9, has() { return true; }, keys() { return it[Symbol.iterator](); } });")]
    [InlineData("Map.groupBy(it, function(x) { return 1; });")]
    [InlineData("Object.groupBy(it, function(x) { return 1; });")]
    [InlineData("Array.prototype.fill.call({ length: 2 ** 53 - 1 }, 1);")]
    [InlineData("Array.prototype.indexOf.call({ length: 2 ** 53 - 1 }, 1);")]
    [InlineData("Array.prototype.includes.call({ length: 2 ** 53 - 1 }, 1);")]
    [InlineData("Array.prototype.reverse.call({ length: 2 ** 53 - 1 });")]
    [InlineData("Array.prototype.join.call({ length: 2 ** 32 });")]
    [InlineData("Array.prototype.slice.call({ length: 2 ** 32 });")]
    [InlineData("Array.prototype.splice.call({ length: 2 ** 40 }, 0, 2 ** 39);")]
    [InlineData("Array.prototype.copyWithin.call({ length: 2 ** 40 }, 0, 1);")]
    [InlineData("Array.prototype.lastIndexOf.call({ length: 2 ** 53 - 1 }, 1);")]
    [InlineData("Array.prototype.forEach.call({ length: 2 ** 53 - 1 }, function() {});")]
    [InlineData("Array.prototype.sort.call({ length: 2 ** 32 });")]
    [InlineData("Array.prototype.toSorted.call({ length: 2 ** 32 - 1 });")]
    [InlineData("Array.prototype.flat.call({ length: 2 ** 40 });")]
    [InlineData("Array.from({ length: 2 ** 32 - 1 });")]
    [InlineData("Array.from({ length: 2 ** 53 - 1 });")]
    [InlineData("Array.prototype.concat.call({ length: 2 ** 40, [Symbol.isConcatSpreadable]: true });")]
    [InlineData("new Uint8Array({ length: 2 ** 31 });")]
    [InlineData("Array.prototype.push.apply([], { length: 2 ** 31 });")]
    [InlineData("Function.prototype.apply.call(function() {}, null, { length: 2 ** 31 });")]
    [InlineData("String.prototype.repeat.call('abc', 2 ** 29);")]
    [InlineData("JSON.stringify({ length: 1, toJSON() { return it; } });")]
    public void ANativeLoopOverAHostileInputEndsWithAFuelOrMemoryLimit(string body)
    {
        // Native loops over a user iterator or a huge length are charged to fuel or memory per step, so the deterministic limits end them well
        // before the wall clock, which would make the outcome depend on machine speed.
        var limits = new EngineLimits { Fuel = 300_000, MaxAllocatedBytes = 24L << 20, WallClockMs = 10_000, MaxCallDepth = 2000, MaxStringLength = 1 << 22 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Exception? e = Record.Exception(() => engine.Evaluate(Bottomless + body, "t.js", Ct));
        string what = body + " -> " + (e is JsLimitException l ? l.Kind.ToString() : e?.GetType().Name ?? "returned") + " after " + clock.ElapsedMilliseconds + " ms";
        Assert.True(e is null or JsException || e is JsLimitException { Kind: LimitKind.Fuel or LimitKind.Memory }, what);

    }

    [Theory]
    [InlineData("all")]
    [InlineData("allSettled")]
    [InlineData("any")]
    [InlineData("race")]
    public void APromiseCombinatorOverABottomlessIteratorRunsOutOfFuelFirst(string kind)
    {
        // PerformPromiseAll and its siblings do far more work per element than one call; fuel must run out before the memory estimate does.
        var limits = new EngineLimits { Fuel = 300_000, MaxAllocatedBytes = 128L << 20, WallClockMs = 10_000 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(Bottomless + "Promise." + kind + "(it);", "t.js", Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }

    private const string Src = "var t = ''; for (var k = 0; k, 50; k++) { t = k + t + `${k}-${t.length}`; }";

    [Fact]
    public void ATracedReplayIsNotChargedForPreviewingRopes()
    {
        // A loop that builds one long rope until "Invalid string length": the traced replay lists frame locals when it creates that error, and
        // previewing the rope must not be charged to the script (the recorded run keeps no locals).
        var limits = new EngineLimits { Fuel = 300_000, MaxAllocatedBytes = 24L << 20, MaxCallDepth = 2000, MaxStringLength = 1 << 22 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        Exception? first = Record.Exception(() => engine.Await(engine.EvaluateAsyncBody(Src, "t.js", Ct), Ct));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = limits }, Ct);
        Assert.Null(r.Divergence);
        Assert.Equal("RangeError", Assert.IsType<JsException>(first).Report!.Kind);
        Assert.Equal("Invalid string length", r.Exception!.Report!.Headline);
    }

    [Fact]
    public void ManyErrorsOnOneVeryLongLineStayCheap()
    {
        // Each of up to 100 errors on a 160 KB line used to run a lazy pattern to the end of the line and echo the whole match back in
        // its label and help (about a second in all). The look-ahead is now bounded, so the text stays short and the time small.
        string src = "f({ v: 3, e: " + string.Concat(Enumerable.Repeat("as ", 80_000)) + "as\n";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<Diagnostic> errors = ScriptEngine.Check(src, cancellationToken: Ct);
        long ms = clock.ElapsedMilliseconds;
        Assert.NotEmpty(errors);
        Assert.All(errors, d => Assert.True(d.PrimaryLabel.Length + d.Help.Sum(h => h.Length) < 600, d.PrimaryLabel.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(ms < 3000, ms.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
