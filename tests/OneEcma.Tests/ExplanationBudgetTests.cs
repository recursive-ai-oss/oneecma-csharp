using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// The replay that explains an error has a budget of its own, counted in work rather than time. When it runs out the plain report
/// stands and says why; scripts that fail in ordinary ways keep their explanations.
/// </summary>
public class ExplanationBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BudgetNote = "= note: explanation unavailable: the explanation exceeded its work budget";

    private static EngineLimits Small() => new() { Fuel = 300_000, MaxAllocatedBytes = 24L << 20 };

    // A big map built while errors keep being created: each error captured the frame locals, which listed every key of the map.
    private const string ManyWritesThenThrow =
        "const o = {}; for (let i = 0; i < 40000; i++) { o['k' + i] = i; try { null.x; } catch (e) {} }\no.missing.deeper;";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyDictionaryWritesStillGetTheirExplanation(bool smallLimits)
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = smallLimits ? new EngineLimits { Fuel = 50_000_000, MaxAllocatedBytes = 256L << 20 } : null });
        var e = Assert.Throws<JsException>(() => engine.Evaluate(ManyWritesThenThrow, "map.js", Ct));
        Assert.Equal("TypeError", e.Report!.Kind);
        Assert.Equal("Cannot read properties of undefined (reading 'deeper')", e.Report.Headline);
        Assert.DoesNotContain("explanation unavailable", DiagnosticRenderer.Render(e.Report), StringComparison.Ordinal);
    }

    [Fact]
    public void ABigObjectIsPreviewedBySize()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions());
        var e = Assert.Throws<JsException>(() => engine.Evaluate("const big = {}; for (let i = 0; i < 500; i++) big['k' + i] = i;\nfunction f() { return big.nothing.x; }\nf();", "big.js", Ct));
        Assert.Equal("Cannot read properties of undefined (reading 'x')", e.Report!.Headline);
        Assert.Contains("{ 500 properties }", DiagnosticRenderer.Render(e.Report, new RenderOptions { ShowLocals = true }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplanationThatRunsOutOfBudgetLeavesThePlainReport(bool smallLimits)
    {
        const string script = "function f(o) { let t = 0; for (let i = 0; i < 2000; i++) { t += i; } return o.items.length + t; }\nf({ id: 1 });";
        EngineLimits? limits() => smallLimits ? Small() : null;

        var plain = Assert.Throws<JsException>(() => new ScriptEngine(new ScriptEngineOptions { Limits = limits(), ExplainErrors = false }).Evaluate(script, "b.js", Ct));
        var budgeted = Assert.Throws<JsException>(() => new ScriptEngine(new ScriptEngineOptions { Limits = limits(), TraceWorkBudget = 500 }).Evaluate(script, "b.js", Ct));

        Assert.Equal(plain.Report!.Headline, budgeted.Report!.Headline);
        Assert.Equal(plain.Report.Kind, budgeted.Report.Kind);
        Assert.Equal(plain.Report.Position, budgeted.Report.Position);
        Assert.Contains(BudgetNote, DiagnosticRenderer.Render(budgeted.Report), StringComparison.Ordinal);

        // With the default budget the same script is explained.
        var explained = Assert.Throws<JsException>(() => new ScriptEngine(new ScriptEngineOptions { Limits = limits() }).Evaluate(script, "b.js", Ct));
        Assert.DoesNotContain("explanation unavailable", DiagnosticRenderer.Render(explained.Report!), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBudgetGivesTheSameReportEveryTime()
    {
        const string script = "function f(o) { let t = 0; for (let i = 0; i < 2000; i++) { t += i; } return o.items.length + t; }\nf({ id: 1 });";
        string Run() => DiagnosticRenderer.Render(Assert.Throws<JsException>(() => new ScriptEngine(new ScriptEngineOptions { TraceWorkBudget = 500 }).Evaluate(script, "b.js", Ct)).Report!);
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ThePropertyMapLoopEndsQuicklyWithALimitAndReplaysTheSame()
    {
        // Fuel and allocation limits as a hostile-input harness would set them; every iteration also makes a rejected promise.
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = Small() });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("const o = {}; for (let i = 0; import('x'); i++) o['k' + i] = i;", "loop.js", Ct));
        Assert.True(e.Kind is LimitKind.Fuel or LimitKind.Memory);

        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = Small(), ThrowOnUnhandledRejection = false }, Ct);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Limit);
        Assert.Equal(e.Kind, r.Limit.Kind);
        Assert.Equal(e.Message, r.Limit.Message);
    }

    [Fact]
    public void ATracedReplayThatSpendsItsBudgetStopsWithoutDiverging()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = Small(), ExplainErrors = false });
        Assert.Throws<JsLimitException>(() => engine.Evaluate("const o = {}; for (let i = 0; ; i++) { o['k' + i] = i; }", "loop.js", Ct));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Limits = Small(), TraceWorkBudget = 10_000 }, Ct);
        Assert.True(r.BudgetSpent);
        Assert.Null(r.Limit);
    }
}
