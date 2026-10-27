namespace RecursiveAI.OneEcma.Tests;

/// <summary>The JS-facing RegExp built-in: limits and error wording as scripts see them.</summary>
public class RegExpBuiltinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptEngine Engine(long fuel = 50_000_000) =>
        new(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = fuel, MaxAllocatedBytes = 128L << 20 } });

    [Fact]
    public void CatastrophicBacktrackingEndsWithAFuelReport()
    {
        // (a+)+$ against a non-matching tail takes exponential time; the matcher's work is charged to the step budget.
        var e = Assert.Throws<JsLimitException>(() => Engine(2_000_000).Evaluate("let s = ''; for (let i = 0; i < 40; i++) s += 'a'; /(a+)+$/.test(s + 'b')", "redos.js", Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
        Assert.NotNull(e.Report);
        Assert.Equal("Limit", e.Report.Kind);
        Assert.Contains("step limit", e.Report.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void BadPatternThrowsASyntaxErrorWithTheV8Wording()
    {
        var engine = Engine();
        JsValue v = engine.Evaluate("try { new RegExp('(', 'g'); 'no' } catch (e) { e.name + ': ' + e.message }", cancellationToken: Ct);
        Assert.Equal("SyntaxError: Invalid regular expression: /(/g: Unterminated group", engine.ToDisplayString(v));

        v = engine.Evaluate("try { new RegExp('a', 'gg'); 'no' } catch (e) { e.name + ': ' + e.message }", cancellationToken: Ct);
        Assert.Equal("SyntaxError: Invalid flags supplied to RegExp constructor 'gg'", engine.ToDisplayString(v));
    }

    [Fact]
    public void ExecResultCarriesGroupsAndIndices()
    {
        var engine = Engine();
        JsValue v = engine.Evaluate("const m = /(?<y>\\d{4})-(\\d\\d)/d.exec('on 2026-10 ok'); [m.index, m.groups.y, m[2], m.indices[0].join(), m.indices.groups.y.join()].join('|')", cancellationToken: Ct);
        Assert.Equal("3|2026|10|3,10|3,7", engine.ToDisplayString(v));
    }

    [Fact]
    public void StringMethodsDispatchToTheRegExp()
    {
        var engine = Engine();
        JsValue v = engine.Evaluate("['a1b22'.replace(/\\d+/g, '<$&>'), 'a-b_c'.split(/[-_]/).join(), 'x'.match(/y/) === null, [...'a1b2'.matchAll(/\\d/g)].length, 'abc'.search(/c/), RegExp.escape('a.b')].join('|')", cancellationToken: Ct);
        Assert.Equal("a<1>b<22>|a,b,c|true|2|2|\\x61\\.b", engine.ToDisplayString(v));
    }
}
