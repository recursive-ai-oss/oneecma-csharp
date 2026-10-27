using RecursiveAI.OneEcma.ExecutorSpike;
using RecursiveAI.OneEcma.ExecutorSpike.Bytecode;
using RecursiveAI.OneEcma.ExecutorSpike.Closure;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Both spike executors agree with each other and with C# on the benchmark sources.</summary>
public class ExecutorSpikeTests
{
    public static TheoryData<string> ScriptNames()
    {
        var d = new TheoryData<string>();
        foreach (var s in SpikeScripts.All)
        {
            d.Add(s.Name);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void ClosureExecutorComputesExpectedValue(string name)
    {
        var s = SpikeScripts.All.First(x => x.Name == name);
        Assert.Equal(s.Expected, ClosureEngine.Evaluate(s.Source));
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void BytecodeExecutorComputesExpectedValue(string name)
    {
        var s = SpikeScripts.All.First(x => x.Name == name);
        Assert.Equal(s.Expected, BytecodeEngine.Evaluate(s.Source));
    }

    private static object? Closure(string src, SpikeLimits? l = null) => ClosureEngine.Evaluate(src, l);

    private static object? Bytecode(string src, SpikeLimits? l = null) => BytecodeEngine.Evaluate(src, l);

    public static TheoryData<string, object?> Semantics() => new()
    {
        { "var fs=[]; for (let i=0;i<3;i++){ fs.push(function(){return i;}); } fs[0]()*100+fs[1]()*10+fs[2]()", 12.0 },
        { "try { x; let x = 1; } catch (e) { e.message }", "Cannot access 'x' before initialization" },
        { "try { null.x } catch (e) { e.message }", "Cannot read properties of null (reading 'x')" },
        { "var o = {}; try { o.foo(); } catch (e) { e.message }", "o.foo is not a function" },
        { "const c = 1; try { c = 2; } catch (e) { e.message }", "Assignment to constant variable." },
        { "function f(){ throw 5; } try { f(); } catch (e) { e }", 5.0 },
        { "function mk(){ var n = 0; return { inc: function(){ n = n + 1; return n; } }; } var c = mk(); c.inc(); c.inc(); c.inc()", 3.0 },
        { "function f(a, b){ return a + b; } f(1)", double.NaN },
        { "var x = 1; x = x + (x = 5); x", 6.0 },
        { "var a = 1, b = 2; a = b || a; a", 2.0 },
        { "var s = 'a'; s += 1; s += 2; s", "a12" },
        { "var i = 5; var j = i++ + ++i; j * 100 + i", 1207.0 },
        { "function f(){ return g(); function g(){ return 7; } } f()", 7.0 },
        { "var r = 0; for (var i = 0; i < 10; i++) { if (i === 3) continue; if (i === 6) break; r += i; } r", 12.0 },
        { "typeof (function(){}) + typeof 1 + typeof 'a' + typeof undefined + typeof null", "functionnumberstringundefinedobject" },
        { "function P(x){ this.x = x; } P.prototype.get = function(){ return this.x; }; new P(4).get()", 4.0 },
        { "var o = {a:1}; o.b = 2; o.a + o.b + (o.c === undefined ? 10 : 0)", 13.0 },
        { "var a = [1,2,3]; a[5] = 9; a.length", 6.0 },
        { "var f = () => 1; f() + (function(){ return (() => 2)(); })()", 3.0 },
    };

    [Theory]
    [MemberData(nameof(Semantics))]
    public void BothExecutorsAgreeOnSemantics(string src, object? expected)
    {
        Assert.Equal(expected, Closure(src));
        Assert.Equal(expected, Bytecode(src));
    }

    [Theory]
    [InlineData("while (true) {}")]
    [InlineData("do {} while (true);")]
    [InlineData("for (;;) {}")]
    [InlineData("var d = false; while (!d) {}")]
    [InlineData("function f(){ return f(); } f()")]
    public void FuelAndDepthLimitsEndInLimitExceptions(string src)
    {
        var limits = () => new SpikeLimits { Fuel = 200_000 };
        Assert.Throws<SpikeLimitException>(() => Closure(src, limits()));
        Assert.Throws<SpikeLimitException>(() => Bytecode(src, limits()));
    }

    private static T OnThread<T>(int stackBytes, Func<T> f)
    {
        T? result = default;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                result = f();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, stackBytes);
        t.Start();
        t.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result!;
    }

    [Fact]
    public void HundredThousandTermChainDoesNotOverflowAThinStack()
    {
        string src = "var x = 1; x" + string.Concat(Enumerable.Repeat(" + x", 100_000));
        Assert.Equal(100_001.0, OnThread(256 * 1024, () => ClosureEngine.Evaluate(src)));
        Assert.Equal(100_001.0, OnThread(256 * 1024, () => BytecodeEngine.Evaluate(src)));
    }

    [Fact]
    public void DeepRecursionIsABoundedLimitInBothAndHostStackIndependentInBytecode()
    {
        const string Src = "function f(n){ return n === 0 ? 0 : 1 + f(n - 1); } f(5000)";
        var limits = () => new SpikeLimits { MaxDepth = 100_000 };
        Assert.Equal(5000.0, OnThread(256 * 1024, () => BytecodeEngine.Evaluate(Src, limits())));
        // The closure executor needs host stack per JavaScript call: on a thin stack it must stop with a limit, not crash.
        Assert.Throws<SpikeLimitException>(() => OnThread(256 * 1024, () => ClosureEngine.Evaluate(Src, limits())));
        Assert.Equal(5000.0, OnThread(64 * 1024 * 1024, () => ClosureEngine.Evaluate(Src, limits())));
    }

    [Fact]
    public void TracedRunSeesTheValuesBehindEachSpan()
    {
        const string Src = "var o = { a: [1, 2] }; o.a.length";
        var (result, trace) = BytecodeEngine.RunTraced(Src);
        Assert.Equal(2.0, result);
        Assert.Contains(trace, t => Src.Substring(t.Start, t.End - t.Start) == "o.a" && t.Value == "1,2");
        Assert.Equal(2.0, BytecodeEngine.Evaluate(Src));
    }

    [Fact]
    public void UnsupportedSyntaxIsReportedNotMisexecuted()
    {
        Assert.Throws<SpikeUnsupportedException>(() => ClosureEngine.Evaluate("function* g(){ yield 1; }"));
        Assert.Throws<SpikeUnsupportedException>(() => BytecodeEngine.Evaluate("function* g(){ yield 1; }"));
    }

    [Theory]
    [InlineData("var a = {}; a", ".b")]
    [InlineData("var f = 1; f", "()")]
    public void HundredThousandLinkMemberAndCallChainsStopWithALimit(string head, string link)
    {
        string src = head + string.Concat(Enumerable.Repeat(link, 100_000));
        Assert.Throws<SpikeLimitException>(() => OnThread(256 * 1024, () => ClosureEngine.Evaluate(src)));
        Assert.Throws<SpikeLimitException>(() => OnThread(256 * 1024, () => BytecodeEngine.Evaluate(src)));
    }
}
