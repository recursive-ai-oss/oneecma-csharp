using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Hostile scripts: every limit ends the run with a report within its bound and the process survives.</summary>
public class LimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptEngine Engine(Action<EngineLimits>? configure = null)
    {
        var limits = new EngineLimits { Fuel = 50_000_000, MaxAllocatedBytes = 128L << 20 };
        configure?.Invoke(limits);
        return new ScriptEngine(new ScriptEngineOptions { Limits = limits });
    }

    [Fact]
    public void InfiniteLoopEndsWithAFuelReport()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.Fuel = 1_000_000).Evaluate("function spin() { for (;;) {} }\nspin();", "loop.js", Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
        Assert.NotNull(e.Report);
        Assert.Equal("Limit", e.Report.Kind);
        Assert.Contains("step limit", e.Report.Headline, StringComparison.Ordinal);
        Assert.Equal(1, e.Report.Position.Line);
    }

    [Fact]
    public void ScriptCannotCatchALimit()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.Fuel = 1_000_000).Evaluate("try { for (;;) {} } catch (e) { 1 } finally { 2 }", cancellationToken: Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }

    [Fact]
    public void DeepRecursionIsACatchableRangeError()
    {
        var engine = Engine();
        JsValue v = engine.Evaluate("function f() { return f() + 1; }\ntry { f(); 'no' } catch (e) { e instanceof RangeError && e.message }", cancellationToken: Ct);
        Assert.Equal("Maximum call stack size exceeded", engine.ToDisplayString(v));

        var uncaught = Assert.Throws<JsException>(() => Engine().Evaluate("function g() { return g() + 1; }\ng();", "rec.js", Ct));
        Assert.Equal("RangeError", uncaught.Report!.Kind);
        Assert.Equal("Maximum call stack size exceeded", uncaught.Report.Headline);
    }

    [Fact]
    public void RecursionThroughNativeCodeStopsToo()
    {
        // Each level re-enters the interpreter from a built-in; the native depth cap ends it before the host stack does.
        var engine = Engine();
        try
        {
            engine.Evaluate("function g() { return [1].map(g); }\ng();", cancellationToken: Ct);
            Assert.Fail("expected the recursion to end");
        }
        catch (JsLimitException e)
        {
            Assert.Equal(LimitKind.NativeDepth, e.Kind);
        }
        catch (JsException e)
        {
            Assert.Equal("RangeError", e.Report!.Kind);
        }
    }

    [Fact]
    public void MemoryBlowupEndsWithAReport()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.MaxAllocatedBytes = 16L << 20).Evaluate("var keep = []; for (;;) keep.push({ a: 1, b: [1, 2, 3], c: 'x' });", cancellationToken: Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
        Assert.Contains("allocation limit", e.Report!.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void StringDoublingStopsAtTheStringLength()
    {
        var engine = Engine(l => { l.MaxStringLength = 1 << 20; l.MaxAllocatedBytes = 4L << 30; });
        JsValue v = engine.Evaluate("var s = 'x'; try { for (;;) s += s; } catch (e) { e.name + ': ' + e.message }", cancellationToken: Ct);
        Assert.Equal("RangeError: Invalid string length", engine.ToDisplayString(v));
    }

    [Fact]
    public void PromiseJobFloodEndsWithAFuelReport()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.Fuel = 2_000_000).Evaluate("function again() { Promise.resolve().then(again); }\nagain();", cancellationToken: Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }

    [Fact]
    public void WallClockEndsALongRun()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => { l.WallClockMs = 100; l.Fuel = long.MaxValue / 2; }).Evaluate("for (;;) {}", cancellationToken: Ct));
        Assert.Equal(LimitKind.WallClock, e.Kind);
    }

    [Fact]
    public void CancellationThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Engine().Evaluate("for (;;) {}", cancellationToken: cts.Token));
    }

    [Fact]
    public void HugeFlatAndChainedSourceCompilesAndRuns()
    {
        const int n = 100_000;
        var engine = Engine();
        var sb = new StringBuilder("var s = 1");
        for (int i = 0; i < n; i++)
        {
            sb.Append(" + 1");
        }

        Assert.Equal(n + 1, engine.Evaluate(sb.ToString() + "; s", cancellationToken: Ct).AsNumber);

        sb.Clear().Append("var o = {}; o.b = o; var r = o");
        for (int i = 0; i < n; i++)
        {
            sb.Append(".b");
        }

        Assert.True(engine.Evaluate(sb.ToString() + "; r === o", cancellationToken: Ct) is { IsBoolean: true });

        sb.Clear().Append("var z = 0; var l = z");
        for (int i = 0; i < n; i++)
        {
            sb.Append(" || z");
        }

        Assert.Equal("0", engine.ToDisplayString(engine.Evaluate(sb.ToString() + "; l", cancellationToken: Ct)));
    }

    [Fact]
    public void DeepNestingIsAReportNotACrash()
    {
        var sb = new StringBuilder("var a = ");
        sb.Append('[', 100_000).Append(']', 100_000);
        var e = Assert.Throws<JsException>(() => Engine().Evaluate(sb.ToString(), cancellationToken: Ct));
        Assert.Equal("SyntaxError", e.Report!.Kind);

        // A call chain too long to compile recursively ends with the engine's stack-depth error.
        sb.Clear().Append("var f = function () { return f; }; var y = f");
        for (int i = 0; i < 100_000; i++)
        {
            sb.Append("()");
        }

        var e2 = Assert.Throws<JsException>(() => Engine().Evaluate(sb.ToString(), cancellationToken: Ct));
        Assert.Equal("RangeError", e2.Report!.Kind);
    }

    [Fact]
    public void LongRunningBuiltinsChargeFuel()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.Fuel = 100_000).Evaluate("var o = { length: 1e9 }; Array.prototype.indexOf.call(o, 1);", cancellationToken: Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }
}
