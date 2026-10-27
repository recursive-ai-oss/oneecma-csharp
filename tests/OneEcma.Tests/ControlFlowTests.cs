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
}
