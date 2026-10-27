namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Finished call frames and scope environments are reused for later calls. These scripts keep something alive past the call
/// (a closure, an arrow's receiver, a generator, an arguments object) and then make many more calls that would overwrite it
/// if it had been reused.
/// </summary>
public class FrameReuseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Run(string source)
    {
        var engine = new ScriptEngine();
        return engine.ToDisplayString(engine.Evaluate(source, cancellationToken: Ct));
    }

    private const string Churn = "function churn(n) { var s = 0; for (var i = 0; i < n; i++) { let t = i * 2; s += (function (a, b) { return a + b + t; })(i, 1); } return s; } churn(50);";

    [Fact]
    public void ClosureKeepsItsScopeAfterTheCallReturns()
    {
        Assert.Equal("[3,6,9]", Run("function mk(x) { var y = x; return function () { return x + y; }; } var f1 = mk(1), f2 = mk(2), f3 = mk(3);" + Churn + " JSON.stringify([f1() + 1, f2() + 2, f3() + 3]);").Replace("\"", ""));
    }

    [Fact]
    public void ArrowKeepsReceiverOfItsDefiningCall()
    {
        Assert.Equal("a1,b2,a1,b2,true", Run("""
            function Box(v) { this.v = v; this.get = () => this.v; this.nt = () => new.target === Box; }
            var a = new Box('a1'), b = new Box('b2');
            function churn() { var o = { m() { return [1, 2, 3].map(x => x * 2).join(); } }; for (var i = 0; i < 100; i++) o.m(); }
            churn();
            [a.get(), b.get(), a.get(), b.get(), a.nt() && b.nt()].join()
            """));
    }

    [Fact]
    public void GeneratorFramesAreNeverReused()
    {
        Assert.Equal("1,10,2,20,3,30", Run("""
            function* g(x) { var y = yield x; var z = yield y + 1; return z + 0; }
            var a = g(1), b = g(10);
            var out = [a.next().value, b.next().value];
            function noise(n) { return n < 2 ? n : noise(n - 1) + noise(n - 2); } noise(12);
            out.push(a.next(1).value, b.next(19).value);
            noise(10);
            out.push(a.next(3).value, b.next(30).value);
            out.join()
            """));
    }

    [Fact]
    public void ThrowingCallsDoNotCorruptLaterCalls()
    {
        Assert.Equal("37,3", Run("""
            function bad(n) { var local = n; if (n % 3 == 0) throw new Error('x' + local); return local; }
            var ok = 0, failed = 0;
            for (var i = 1; i <= 10; i++) { try { ok += bad(i); } catch (e) { failed += 1; } }
            ok + ',' + failed
            """));
    }

    [Fact]
    public void ArgumentsObjectAndEvalVariablesSurviveReuse()
    {
        Assert.Equal("1,2,3|7|x", Run("""
            function args() { return arguments; }
            var a = args(1, 2, 3);
            function ev() { eval('var q = 7'); return function () { return q; }; }
            var getq = ev();
            function mapped(p) { arguments[0] = 'x'; return p; }
            var m = mapped('y');
            function churn(n) { return n ? churn(n - 1) + args(n, n)[0] : 0; } churn(30);
            Array.prototype.join.call(a, ',') + '|' + getq() + '|' + m
            """));
    }

    [Fact]
    public void EvalDoesNotReleaseItsCallersScope()
    {
        // Direct eval runs in the calling function's environment; finishing the eval must leave that environment alone.
        Assert.Equal("inner", Run("var x = 'outer'; var r; function f({a: ignored = eval(\"var x = 'inner'\")}) { r = x; } f({}); r"));
        Assert.Equal("7,8", Run("function g() { var a = 7; eval('var b = a + 1'); return a + ',' + b; } g();"));
    }

    [Fact]
    public void RecursionAndConstructorsStillWork()
    {
        Assert.Equal("75025", Run("function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); } fib(25);"));
        Assert.Equal("6", Run("class A { constructor(x) { this.x = x; } } class B extends A { constructor(x) { super(x * 2); this.y = () => this.x; } } var b = new B(3); new A(1); new B(9); b.y();"));
    }
}
