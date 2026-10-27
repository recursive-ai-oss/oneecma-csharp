namespace RecursiveAI.OneEcma.Tests;

public class ScriptEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EvaluateReturnsTheCompletionValue()
    {
        var engine = new ScriptEngine();
        JsValue v = engine.Evaluate("var a = 20; a + 22", cancellationToken: Ct);
        Assert.True(v.IsNumber);
        Assert.Equal(42, v.AsNumber);
        Assert.Equal("hello world", engine.ToDisplayString(engine.Evaluate("'hello ' + 'world'", cancellationToken: Ct)));
    }

    [Fact]
    public void StateSurvivesBetweenEvaluations()
    {
        var engine = new ScriptEngine();
        engine.Evaluate("function twice(x) { return x * 2; }", cancellationToken: Ct);
        JsValue fn = engine.GetProperty(engine.Global, "twice");
        JsValue r = engine.Call(fn, JsValue.Undefined, [JsValue.FromNumber(21)], Ct);
        Assert.Equal(42, r.AsNumber);
    }

    [Fact]
    public void ThrowReachesTheHostWithAReport()
    {
        var engine = new ScriptEngine();
        JsException e = Assert.Throws<JsException>(() => engine.Evaluate("let o;\no.x.y;", "t.js", Ct));
        Assert.NotNull(e.Report);
        Assert.Equal("TypeError", e.Report.Kind);
        Assert.Equal("Cannot read properties of undefined (reading 'x')", e.Report.Headline);
        Assert.Equal(2, e.Report.Position.Line);
        Assert.StartsWith("error[TypeError]: Cannot read properties of undefined (reading 'x')", e.Report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SyntaxErrorsAreReportedAsSyntaxErrors()
    {
        JsException e = Assert.Throws<JsException>(() => new ScriptEngine().Evaluate("let a = ;", cancellationToken: Ct));
        Assert.Equal("SyntaxError", e.Report!.Kind);
    }

    [Fact]
    public void HostFunctionsAreCallableAndTheirExceptionsBecomeErrors()
    {
        var engine = new ScriptEngine();
        engine.DefineGlobalFunction("add", 2, (_, a) => JsValue.FromNumber(a[0].AsNumber + a[1].AsNumber));
        engine.DefineGlobalFunction("boom", 0, (_, _) => throw new InvalidOperationException("nope"));
        Assert.Equal(5, engine.Evaluate("add(2, 3)", cancellationToken: Ct).AsNumber);
        Assert.Equal("Error: InvalidOperationException: nope", engine.ToDisplayString(engine.Evaluate("try { boom() } catch (e) { String(e) }", cancellationToken: Ct)));
    }

    [Fact]
    public void PromiseJobsRunBeforeEvaluateReturns()
    {
        var engine = new ScriptEngine();
        engine.Evaluate("var out = []; Promise.resolve(1).then(v => out.push(v)); (async () => { await null; out.push(2); })();", cancellationToken: Ct);
        Assert.Equal("1,2", engine.ToDisplayString(engine.Evaluate("out.join()", cancellationToken: Ct)));
    }

    [Fact]
    public void NullSourceIsAnArgumentError()
    {
        Assert.Throws<ArgumentNullException>(() => ScriptEngine.Check(null!, null, Ct));
        Assert.Throws<ArgumentNullException>(() => new ScriptEngine().Evaluate(null!, cancellationToken: Ct));
    }

    [Fact]
    public void CheckReturnsNothingForValidSourceAndDiagnosticsForInvalid()
    {
        Assert.Empty(ScriptEngine.Check("let a = 1;", null, Ct));
        Assert.NotEmpty(ScriptEngine.Check("let a = ;", null, Ct));
    }
}
