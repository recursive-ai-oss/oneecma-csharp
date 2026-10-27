using System.Text.Json;
using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>The explained runtime report: provenance, "did you mean", locals, and that the lean path is unchanged.</summary>
public sealed class DiagnosticsCoreTests
{
    private static JsException Fail(string source, bool explain = true, Action<ScriptEngine>? setup = null)
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { ExplainErrors = explain });
        setup?.Invoke(engine);
        return Assert.Throws<JsException>(() => engine.Evaluate(source, "t.js", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheLeanReportHasNoExplanation()
    {
        JsException e = Fail("const o = { a: 1 };\no.b.c;", explain: false);
        Assert.Empty(e.Report!.SecondaryLabels);
        Assert.Empty(e.Report.Help);
        Assert.All(e.Report.Stack, f => Assert.Null(f.Locals));
    }

    [Fact]
    public void AMisspelledPropertyOffersWhatTheObjectHolds()
    {
        JsException e = Fail("const user = { firstName: 'a', age: 3 };\nuser.firstname.trim();");
        Diagnostic d = e.Report!;
        Assert.Contains(d.Help, h => h.StartsWith("did you mean `user.firstName`?", StringComparison.Ordinal));
        Assert.Equal("user.firstName", d.Suggestions[0].Name);
    }

    [Fact]
    public void ACopiedObjectShowsEveryPlaceItWasStored()
    {
        JsException e = Fail("const a = { k: 1 };\nconst b = a;\nconst c = b;\nc.zzz.y;");
        string text = e.Report!.ToString();
        Assert.Contains("`c` comes from here: { k }", text, StringComparison.Ordinal);
        Assert.Equal(3, e.Report.Provenance.Count);
        Assert.Contains("the same object was stored here before", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalsOfEveryFrameAreRecordedAndRendered()
    {
        JsException e = Fail("function inner(p) { const q = { x: 1 }; return q.y.z; }\nfunction outer(n) { const label = 'k'; return inner(n + 1); }\nouter(41);");
        IReadOnlyList<LocalInfo> inner = e.Report!.Stack[0].Locals!;
        Assert.Contains(new LocalInfo("p", "42"), inner);
        Assert.Contains(new LocalInfo("q", "{ x }"), inner);
        Assert.Contains(e.Report.Stack[1].Locals!, l => l.Name == "label" && l.Value == "\"k\"");
        string text = DiagnosticRenderer.Render(e.Report, new RenderOptions { ShowLocals = true });
        Assert.Contains("= locals in inner: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("= locals", e.Report.ToString(), StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(DiagnosticJson.Render([e.Report]));
        JsonElement diag = json.RootElement.GetProperty("diagnostics")[0];
        Assert.True(diag.GetProperty("stack")[0].GetProperty("locals").GetArrayLength() >= 2);
        Assert.Equal(JsonValueKind.Array, diag.GetProperty("provenance").ValueKind);
    }

    [Fact]
    public void AHostResultIsTheOriginOfTheValue()
    {
        JsException e = Fail("const r = await_free();\nr.lst.length;", setup: engine =>
            engine.DefineGlobalFunction("await_free", 0, (_, _) =>
            {
                JsRealm realm = engine.MainRealm;
                JsObject o = realm.NewObject();
                realm.DefineData(o, "list", JsValue.FromNumber(1));
                return JsValue.FromObject(o);
            }));
        string text = e.Report!.ToString();
        Assert.Contains("`r` comes from here: { list }", text, StringComparison.Ordinal);
        Assert.Contains("did you mean `r.list`?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayOfARecordedFailureGivesTheSameReportBytes()
    {
        var engine = new ScriptEngine();
        engine.DefineGlobalFunction("now2", 0, (_, _) => JsValue.FromNumber(5));
        const string src = "const t = now2();\nconst o = { id: t, rows: [1, 2] };\nfunction f() { return o.row.length; }\nf();";
        JsException first = Assert.Throws<JsException>(() => engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions(), TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.NotNull(r.Exception);
        Assert.Equal(first.Report!.ToString(), r.Exception.Report!.ToString());
        Assert.Equal(DiagnosticJson.Render([first.Report]), DiagnosticJson.Render([r.Exception.Report]));
    }

    [Fact]
    public void ExplanationStaysBoundedForHugeAndLongChainedValues()
    {
        // A huge array is not enumerated for suggestions, and a value copied many times keeps a capped chain.
        JsException e = Fail("const big = new Array(2000000).fill(0);\nbig.foo.bar;");
        Assert.Empty(e.Report!.Suggestions);

        JsException chain = Fail("let o = { a: 1 };\nfor (let i = 0; i < 3000; i++) { const c = o; o = c; }\no.zz.y;");
        Assert.True(chain.Report!.Provenance.Count <= 8, "the provenance chain is capped");
    }
}
