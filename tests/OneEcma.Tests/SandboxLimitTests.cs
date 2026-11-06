using System.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Growth that used to escape the allocation budget (the global symbol registry, the job queue, closures and their environments)
/// and BigInt kernels whose cost grows with operand size (so neither fuel nor the wall clock saw them) now end in limit reports.
/// </summary>
public class SandboxLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptEngine Engine(long allocated, long fuel = 4_000_000_000L, double wallMs = 0) =>
        new(new ScriptEngineOptions { Limits = new EngineLimits { MaxAllocatedBytes = allocated, Fuel = fuel, WallClockMs = wallMs }, ExplainErrors = false });

    // ---- memory-charged patterns --------------------------------------------------------------------

    public static TheoryData<string, string> MemoryScripts() => new()
    {
        { "symbol registry", "for (let i = 0; ; i++) Symbol.for('k' + i);" },
        { "promise queue", "for (;;) Promise.resolve().then(() => {});" },
        { "await in a loop of async calls", "async function f() { await 0; } for (;;) f();" },
        { "async generator requests", "async function* g() { yield 1; } const it = g(); for (;;) it.next();" },
        { "thenable jobs", "const t = { then(r) { r(); } }; for (;;) Promise.resolve(t);" },
        { "closure kept", "const keep = []; for (let i = 0; ; i++) keep.push(() => i);" },
        { "closure over a block scope", "const keep = []; for (;;) { let x = 1; keep.push(function () { return x; }); }" },
        { "bound functions", "const f = function () {}; const keep = []; for (;;) keep.push(f.bind(null, 1, 2, 3));" },
        { "classes", "const keep = []; for (;;) keep.push(class { m() {} static s() {} });" },
        { "function prototype objects", "const keep = []; for (;;) { const f = function () {}; f.prototype; keep.push(f); }" },
        { "suspended generators", "function* g() { yield 1; } const keep = []; for (;;) { const it = g(); it.next(); keep.push(it); }" },
        { "suspended async functions", "async function f() { await new Promise(() => {}); } for (;;) f();" },
        { "arguments objects", "function f() { return arguments; } const keep = []; for (;;) keep.push(f(1, 2, 3, 4));" },
        { "closures made by a recursion-free call", "function mk(i) { return () => i; } const keep = []; for (let i = 0; ; i++) keep.push(mk(i));" },
        { "new promises", "const keep = []; for (;;) keep.push(new Promise(() => {}));" },
        { "WeakRef", "const keep = []; for (;;) keep.push(new WeakRef({}));" },
        { "Proxy", "const keep = []; const h = {}; for (;;) keep.push(new Proxy({}, h));" },
        { "RegExp built from strings", "const keep = []; for (let i = 0; ; i++) keep.push(new RegExp('a'.repeat(200) + i));" },
        { "Error objects", "const keep = []; for (;;) keep.push(new Error('x'));" },
        { "Intl objects", "const keep = []; for (;;) keep.push(new Intl.NumberFormat('en-US'));" },
        { "BigInt results", "const keep = []; let b = 1n << 100000n; for (;;) keep.push(b + 1n);" },
        { "BigInt asUintN", "BigInt.asUintN(2 ** 29, -1n)" },
    };

    [Theory]
    [MemberData(nameof(MemoryScripts))]
    public void EndsInAMemoryReport(string name, string script)
    {
        ScriptEngine engine = Engine(16L << 20);
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, name.Replace(' ', '-') + ".js", Ct));
        Assert.True(e.Kind == LimitKind.Memory, name + " ended with " + e.Kind);
        Assert.NotNull(e.Report);
        Assert.Contains("allocation limit", e.Report.Headline, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("for (let i = 0; ; i++) Symbol.for('key' + i);")]
    [InlineData("for (;;) Promise.resolve().then(() => {});")]
    [InlineData("const keep = []; for (let i = 0; ; i++) keep.push(() => i);")]
    public void LiveMemoryStaysNearTheLimit(string script)
    {
        const long Limit = 64L << 20;
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        ScriptEngine engine = Engine(Limit);
        Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "grow.js", Ct));
        long live = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(engine);
        // The charges estimate the .NET footprint, which differs a little by platform (about 1.1x the limit on x64, under 2x on
        // arm64): the bound proves growth is stopped in proportion to the limit, where it used to run to gigabytes.
        Assert.True(live < Limit * 3, $"{live >> 20} MiB live against a {Limit >> 20} MiB limit");
    }

    [Fact]
    public void OrdinaryCodeIsNotStoppedByTheNewCharges()
    {
        // Calls, short-lived closures and promise chains that are dropped at once stay far under a small budget.
        ScriptEngine engine = Engine(64L << 20);
        JsValue v = engine.Evaluate("""
            function add(a, b) { return a + b; }
            let t = 0;
            for (let i = 0; i < 5000; i++) t = add(t, [1, 2, 3].map(x => x + i).length);
            let p = Promise.resolve(0);
            for (let i = 0; i < 200; i++) p = p.then(x => x + 1);
            t
            """, "ordinary.js", Ct);
        Assert.Equal("15000", engine.ToDisplayString(v));
        engine.RunUntilIdle(Ct);
    }

    // ---- BigInt ----------------------------------------------------------------------------------------

    private static (JsLimitException E, long Ms) Run(ScriptEngine engine, string script, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "big.js", token));
        return (e, sw.ElapsedMilliseconds);
    }

    [Fact]
    public void RepeatedSquaringEndsOnFuelBeforeAnOperationThatCannotFinish()
    {
        // Fuel counts work, not time, so how long this takes depends on the machine; the bound only guards against a hang. The time
        // guarantees are the wall-clock and cancellation tests below.
        var (e, ms) = Run(Engine(1L << 40, fuel: 100_000_000), "let b = 3n; for (;;) b = b * b;", Ct);
        Assert.Equal(LimitKind.Fuel, e.Kind);
        Assert.True(ms < 60_000, ms + " ms");
    }

    [Fact]
    public void AnOperationLargerThanTheRemainingFuelNeverStarts()
    {
        var (e, ms) = Run(Engine(1L << 40, fuel: 1_000_000), "const b = 1n << 100000000n; b * b;", Ct);
        Assert.Equal(LimitKind.Fuel, e.Kind);
        Assert.True(ms < 2_000, ms + " ms");
    }

    [Fact]
    public void ALongMultiplicationEndsAtTheWallClockWhateverTheFuel()
    {
        var (e, ms) = Run(Engine(1L << 40, fuel: long.MaxValue / 4, wallMs: 1000), "let b = 3n; for (;;) b = b * b;", Ct);
        Assert.Equal(LimitKind.WallClock, e.Kind);
        Assert.True(ms is > 800 and < 3500, ms + " ms");
    }

    [Fact]
    public void ALongExponentiationEndsAtTheWallClock()
    {
        var (e, ms) = Run(Engine(1L << 40, fuel: long.MaxValue / 4, wallMs: 1000), "3n ** 10000000n", Ct);
        Assert.Equal(LimitKind.WallClock, e.Kind);
        Assert.True(ms < 3500, ms + " ms");
    }

    [Fact]
    public void CancellationEndsALongMultiplication()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(500);
        ScriptEngine engine = Engine(1L << 40, fuel: long.MaxValue / 4);
        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("let b = 3n; for (;;) b = b * b;", "cancel.js", cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 3000, sw.ElapsedMilliseconds + " ms");
    }

    [Theory]
    [InlineData("(1n << 40000000n) / ((1n << 20000000n) + 1n)")]
    [InlineData("(1n << 40000000n) % ((1n << 20000000n) + 1n)")]
    [InlineData("(1n << 10000000n).toString()")]
    [InlineData("(1n << 10000000n).toString(7)")]
    [InlineData("(1n << 10000000n).toLocaleString()")]
    [InlineData("String(1n << 10000000n)")]
    [InlineData("BigInt('9'.repeat(5000000))")]
    [InlineData("BigInt('0x' + 'f'.repeat(5000000))")]
    [InlineData("parseInt('7'.repeat(5000000), 8)")]
    [InlineData("(1n << 10000000n) * (1n << 10000000n) * (1n << 10000000n) * (1n << 10000000n) * 3n * 5n * 7n * (2n ** 100n) ** 300000n")]
    public void SizeDependentKernelsEndInALimitReport(string script)
    {
        var (e, ms) = Run(Engine(1L << 40), script, Ct);
        Assert.True(e.Kind is LimitKind.Fuel or LimitKind.WallClock, e.Kind.ToString());
        Assert.True(ms < 10_000, ms + " ms");
    }

    [Fact]
    public void AShiftResultIsChargedToTheMemoryBudget()
    {
        ScriptEngine engine = Engine(16L << 20);
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("const keep = []; const a = 1n << 1000000n; for (;;) keep.push(a >> 1n);", "shift.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }

    [Fact]
    public void BlockedMultiplicationAndPowerAgreeWithTheBclKernels()
    {
        int saved = BigMath.BlockBytes;
        BigMath.BlockBytes = 4096;
        try
        {
            ScriptEngine engine = Engine(1L << 36);
            // Both operands span several blocks; the identities hold only if the blocked product is exact.
            JsValue ok = engine.Evaluate("""
                const p = 1000003n;
                const a = 3n ** 100000n * -1n;
                const b = 7n ** 90000n + 12345n;
                const ab = a * b;
                let good = ab / b === a && ab % b === 0n && ab % p === ((a % p) * (b % p)) % p && (a * a) / a === a && (b * 5n) / b === 5n;
                const c = 11n ** 200000n;
                function modpow(x, n, m) { let r = 1n; x %= m; while (n > 0n) { if (n & 1n) r = r * x % m; x = x * x % m; n >>= 1n; } return r; }
                good = good && c % p === modpow(11n, 200000n, p) && (-5n) ** 30001n % p === (modpow(5n, 30001n, p) * -1n) % p;
                good = good && (2n ** 100000n) === (1n << 100000n) && (-2n) ** 100001n === -(1n << 100001n);
                good
                """, "blocks.js", Ct);
            Assert.True(ok.AsBoolean);
        }
        finally
        {
            BigMath.BlockBytes = saved;
        }
    }

    [Fact]
    public void BlockedMultiplicationMatchesTheBclProductForManyShapes()
    {
        // A tiny block forces the split path for every product, so unbalanced, odd-length and zero-padded halves all occur.
        int saved = BigMath.BlockBytes;
        BigMath.BlockBytes = 16;
        try
        {
            ScriptEngine engine = Engine(1L << 36);
            JsValue v = engine.Evaluate("""
                let seed = 12345n;
                function rnd(bytes) { let r = 0n; for (let i = 0; i < bytes; i++) { seed = (seed * 6364136223846793005n + 1442695040888963407n) % (1n << 64n); r = (r << 8n) | (seed >> 56n); } return r; }
                const out = [];
                for (const [la, lb] of [[17, 17], [33, 16], [100, 3], [257, 255], [64, 1], [129, 128], [500, 40], [1000, 999]]) {
                  for (let k = 0; k < 3; k++) {
                    let a = rnd(la), b = rnd(lb);
                    if (k === 1) { a = a << 136n; }            // zero low bytes
                    if (k === 2) { b = (1n << BigInt(lb * 8)) - 1n; a = -a; }   // all-ones operand, negative sign
                    out.push(a.toString(16) + ',' + b.toString(16) + ',' + (a * b).toString(16));
                  }
                }
                out.join(';')
                """, "mul.js", Ct);
            foreach (string row in v.AsString.Split(';'))
            {
                string[] p = row.Split(',');
                Assert.Equal(Hex(p[0]) * Hex(p[1]), Hex(p[2]));
            }
        }
        finally
        {
            BigMath.BlockBytes = saved;
        }

        static System.Numerics.BigInteger Hex(string s)
        {
            bool neg = s.StartsWith('-');
            var r = System.Numerics.BigInteger.Parse("0" + (neg ? s[1..] : s), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            return neg ? -r : r;
        }
    }

    [Fact]
    public void OrdinaryBigIntArithmeticStillWorks()
    {
        ScriptEngine engine = Engine(8L << 20);
        JsValue v = engine.Evaluate("""
            let f = 1n; for (let i = 1n; i <= 300n; i++) f *= i;
            [f.toString().length, (2n ** 200n).toString(16), BigInt('123456789012345678901234567890') * 2n, 10n ** 30n / 7n, BigInt(parseInt('123456789012345678901234567890'))].join(',')
            """, "ordinary.js", Ct);
        Assert.Equal("615,100000000000000000000000000000000000000000000000000,246913578024691357802469135780,142857142857142857142857142857,123456789012345677877719597056", engine.ToDisplayString(v).Trim('"'));
    }

    // ---- record and replay -----------------------------------------------------------------------------

    [Theory]
    [InlineData("for (let i = 0; ; i++) Symbol.for('k' + i);", LimitKind.Memory)]
    [InlineData("for (;;) Promise.resolve().then(() => {});", LimitKind.Memory)]
    [InlineData("const keep = []; for (let i = 0; ; i++) keep.push(() => i);", LimitKind.Memory)]
    [InlineData("function* g() { yield 1; } const keep = []; for (;;) { const it = g(); it.next(); keep.push(it); }", LimitKind.Memory)]
    [InlineData("function f() { return arguments; } const keep = []; for (;;) keep.push(f(1, 2, 3));", LimitKind.Memory)]
    [InlineData("const keep = []; for (;;) keep.push(new Error('x'));", LimitKind.Memory)]
    [InlineData("const keep = []; let b = 1n << 100000n; for (;;) keep.push(b + 1n);", LimitKind.Memory)]
    [InlineData("let b = 3n; for (;;) b = b * b;", LimitKind.Fuel)]
    [InlineData("(1n << 10000000n).toString(7)", LimitKind.Memory)]
    public void ATracedReplayHitsTheSameLimit(string script, LimitKind expected)
    {
        ScriptEngine engine = Engine(8L << 20, fuel: 300_000_000);
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "replay.js", Ct));
        Assert.Equal(expected, e.Kind);
        Assert.True(engine.RecordLog.IsReplayable);

        var options = new ReplayOptions { Limits = new EngineLimits { MaxAllocatedBytes = 8L << 20, Fuel = 300_000_000 }, Trace = true };
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, options, Ct);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Limit);
        Assert.Equal(expected, r.Limit.Kind);
        Assert.Equal(e.Report!.Headline, r.Limit.Report!.Headline);
    }

    // A traced replay cannot reuse frames and environments the way the recorded run does, so it counts what the pool would have
    // supplied; the charge for a call-heavy loop must come out the same on both sides.
    [Theory]
    [InlineData("function f() { return } const keep = []; for (;;) keep.push(f(1, 2, 3));")]
    [InlineData("function f(a) { let x = a; { let y = x; return () => y; } } const keep = []; for (let i = 0; ; i++) { f(i); keep.push(i); }")]
    [InlineData("function f(a) { const g = (b) => a + b; return g(1); } const keep = []; for (let i = 0; ; i++) keep.push(f(i));")]
    [InlineData("function r(n) { return n ? r(n - 1) + 1 : 0; } const keep = []; for (;;) keep.push(r(40));")]
    [InlineData("function f(a, b = a) { for (let i = 0; i < 2; i++) { setTimeoutLike = () => i; } return b; } var setTimeoutLike; const keep = []; for (;;) keep.push(f(1));")]
    public void ReplayChargesCallsLikeTheRecordedRun(string script)
    {
        var limits = new EngineLimits { MaxAllocatedBytes = 24L << 20, Fuel = 300_000 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits, ExplainErrors = false });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "calls.js", Ct));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = new EngineLimits { MaxAllocatedBytes = 24L << 20, Fuel = 300_000 }, Trace = true }, Ct);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Limit);
        Assert.Equal(e.Kind, r.Limit.Kind);
        Assert.Equal(e.Report!.Headline, r.Limit.Report!.Headline);
    }

    // ---- a 256 MiB allocation limit with 30 s of wall clock -------------------------------------
    // These run for up to half a minute each; run them one at a time with explicit tests enabled.

    [Theory(Explicit = true)]
    [InlineData("for (let i = 0; ; i++) Symbol.for('k' + i);", LimitKind.Memory)]
    [InlineData("for (;;) Promise.resolve().then(() => {});", LimitKind.Memory)]
    [InlineData("const keep = []; for (let i = 0; ; i++) keep.push(() => i);", LimitKind.Memory)]
    [InlineData("let b = 3n; while (true) b = b * b;", LimitKind.Fuel)]
    [InlineData("let b = 3n; for (let i = 0; i < 26; i++) b = b * b; b > 0n;", LimitKind.Fuel)]
    public void LargeLimitsEndInTheRightLimit(string script, LimitKind expected)
    {
        ScriptEngine engine = Engine(256L << 20, fuel: 5_000_000_000, wallMs: 30_000);
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate(script, "large.js", Ct));
        Assert.True(e.Kind == expected || e.Kind == LimitKind.WallClock && sw.ElapsedMilliseconds < 31_500, $"{e.Kind} after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 31_500, sw.ElapsedMilliseconds + " ms");
        using var self = Process.GetCurrentProcess();
        Assert.True(self.PeakWorkingSet64 < 1L << 30, (self.PeakWorkingSet64 >> 20) + " MiB peak");
    }
}
