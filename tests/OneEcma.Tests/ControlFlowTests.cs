namespace RecursiveAI.OneEcma.Tests;

public class ControlFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class OneModuleHost(string source) : DefaultHost
    {
        public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
            => specifier == "main.mjs" ? new HostModule("main.mjs", source) : null;
    }

    // A `continue` inside `catch` runs the enclosing `finally` after the catch scope was popped; the finally body must
    // resolve its names against the scope chain as it is at that point, not the one inside the catch block.
    private const string ContinueThroughFinally = """
        var c = 0, fin = 0;
        do {
          try { throw 1; } catch (e) { c += 1; continue; } finally { fin = c; }
        } while (c < 2);
        """;

    [Fact]
    public void ContinueInCatchRunsFinallyInScript()
    {
        var engine = new ScriptEngine();
        JsValue v = engine.Evaluate(ContinueThroughFinally + "fin * 10 + c", cancellationToken: Ct);
        Assert.Equal(22, v.AsNumber);
    }

    [Fact]
    public void ContinueInCatchRunsFinallyInModule()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new OneModuleHost(ContinueThroughFinally + "if (fin !== 2 || c !== 2) throw new Error('bad ' + fin + ' ' + c);") });
        engine.EvaluateModule("main.mjs", Ct);
    }

    [Fact]
    public void BreakAndReturnThroughNestedScopesAndFinally()
    {
        var engine = new ScriptEngine();
        JsValue v = engine.Evaluate("""
            function f() {
              let log = '';
              outer: for (let i = 0; i < 3; i++) {
                try { let a = i; { let b = a; try { if (b == 1) continue outer; if (b == 2) break outer; } finally { log += 'f' + b; } } }
                catch (e) { log += 'x'; }
                finally { let z = i; log += 'F' + z; }
              }
              return log;
            }
            f()
            """, cancellationToken: Ct);
        Assert.Equal("f0F0f1F1f2F2", engine.ToDisplayString(v));
    }

    private static string Run(string source)
    {
        var engine = new ScriptEngine();
        return engine.ToDisplayString(engine.Evaluate(source, cancellationToken: Ct));
    }

    [Fact]
    public void SparseArrayFilledBackwardsKeepsEveryElement()
    {
        Assert.Equal("4,a,b,c,d", Run("var e = []; e[3]='d'; e[2]='c'; e[1]='b'; e[0]='a'; [e.length, e[0], e[1], e[2], e[3]].join()"));
    }

    [Fact]
    public void DestructuringAssignmentEvaluatesTargetsBeforeReadingValues()
    {
        const string src = """
            var log = [];
            var it = { [Symbol.iterator]() { return { next() { log.push('next'); return { done: true }; }, return() { log.push('return'); return {}; } }; } };
            function t() { log.push('target'); throw new Error('stop'); }
            try { [ {}[t()] ] = it; } catch (e) {}
            log.join()
            """;
        Assert.Equal("target,return", Run(src));
    }

    [Fact]
    public void DerivedConstructorReturnErrorIsNotCatchableByTheBody()
    {
        const string src = """
            class C extends class {} { constructor() { super(); try { return 0; } catch (e) { return; } } }
            var r; try { new C(); r = 'no error'; } catch (e) { r = e.constructor.name; }
            r
            """;
        Assert.Equal("TypeError", Run(src));
    }

    [Fact]
    public void StrictDeleteOfNonConfigurableComputedMemberThrows()
    {
        Assert.Equal("TypeError", Run("'use strict'; var o = {}; Object.defineProperty(o, 'k', {value: 1}); var n = 'k'; try { delete o[n]; 'no' } catch (e) { e.constructor.name }"));
    }

    [Fact]
    public void TailCallsInStrictCodeDoNotGrowTheStack()
    {
        const string src = """
            'use strict';
            function f(n) { if (n === 0) return 'done'; return f(n - 1); }
            function g(_, n) { if (n === 0) return 'tag'; return g`${n - 1}`; }
            f(200000) + g(null, 1)
            """;
        Assert.StartsWith("done", Run(src), StringComparison.Ordinal);
    }

    [Fact]
    public void EvalInParameterExpressionCannotRedeclareParameters()
    {
        Assert.Equal("SyntaxError", Run("function f(a, b = eval('var a')) {} try { f(); 'no' } catch (e) { e.constructor.name }"));
    }

    // Fuzzing found loops whose only back edge was a `continue`: nothing charged fuel, so the step limit never fired.
    [Theory]
    [InlineData("var c = 0; while (true) { c++; continue; }")]
    [InlineData("var c = 0; while (c < 2) { try { throw 1; } finally { continue; } }")]
    [InlineData("var c = 0; while (c < 2) { try { throw 1; } catch (e) { c += v; break; } finally { continue; } }")]
    [InlineData("for (;;) { continue; }")]
    [InlineData("do { continue; } while (true);")]
    [InlineData("a: for (;;) { while (true) { continue a; } }")]
    public void LoopsThatOnlyContinueEndAtTheStepLimit(string source)
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = 200_000 } });
        JsLimitException e = Assert.Throws<JsLimitException>(() => engine.Evaluate(source, cancellationToken: Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }
}
