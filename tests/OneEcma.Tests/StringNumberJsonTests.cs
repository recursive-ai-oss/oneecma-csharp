namespace RecursiveAI.OneEcma.Tests;

/// <summary>String, Number, Math, JSON, BigInt and URI built-ins: hostile input, limits and exact output.</summary>
public class StringNumberJsonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Eval(string source, Action<EngineLimits>? configure = null)
    {
        var limits = new EngineLimits { Fuel = 400_000_000, MaxAllocatedBytes = 1L << 30 };
        configure?.Invoke(limits);
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        return engine.ToDisplayString(engine.Evaluate(source, "t.js", Ct));
    }

    [Fact]
    public void DeepJsonParsesAndStringifiesWithoutUsingTheHostStack()
    {
        string r = Eval("""
            const n = 100000;
            const text = '['.repeat(n) + ']'.repeat(n);
            let v = JSON.parse(text);
            let depth = 0;
            for (let p = v; p.length; p = p[0]) depth++;
            const out = JSON.stringify(v);
            depth + ':' + (out === text)
            """);
        Assert.Equal("99999:true", r);
    }

    [Fact]
    public void DeepJsonWithAReviverWalksIteratively()
    {
        Assert.Equal("50000", Eval("""
            let calls = 0;
            JSON.parse('['.repeat(50000) + ']'.repeat(50000), (k, v) => { calls++; return v; });
            String(calls)
            """));
    }

    [Fact]
    public void ReviverSeesSourceTextOnlyForUnchangedPrimitives()
    {
        Assert.Equal("""["1.0","2e0",{"a":"-0"}]""", Eval("""JSON.stringify(JSON.parse('[1.0, 2e0, {"a": -0}]', (k, v, c) => typeof v === 'number' ? c.source : v))"""));
        Assert.Equal(":source:1,:9", Eval("""JSON.stringify(JSON.parse('[1,2]', function (k, v, c) { if (k === '0') this[1] = 9; return Object.keys(c).join() + ':' + String(v); }))""").Trim('"'));
    }

    [Theory]
    [InlineData("JSON.parse('')", "Unexpected end of JSON input")]
    [InlineData("JSON.parse(undefined)", "\"undefined\" is not valid JSON")]
    [InlineData("JSON.parse('{\\'a\\':1}')", "Expected property name or '}' in JSON at position 1 (line 1 column 2)")]
    [InlineData("JSON.parse('[1,2,3,4,5,6,7,8,9,x]')", "Unexpected token 'x', ...\"5,6,7,8,9,x]\" is not valid JSON")]
    [InlineData("JSON.parse('{\"a\":1}}')", "Unexpected non-whitespace character after JSON at position 7 (line 1 column 8)")]
    public void JsonParseErrorsUseNodesWording(string call, string message)
    {
        Assert.Equal(message, Eval("try { " + call + " } catch (e) { e.name + ': ' + e.message }").Replace("SyntaxError: ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void CircularStructureMessageNamesThePath()
    {
        string msg = Eval("const a = { b: { c: {} } }; a.b.c.d = a; try { JSON.stringify(a) } catch (e) { e.message }");
        Assert.Equal(
            "Converting circular structure to JSON\n    --> starting at object with constructor 'Object'\n    |     property 'b' -> object with constructor 'Object'\n    |     property 'c' -> object with constructor 'Object'\n    --- property 'd' closes the circle",
            msg);
    }

    [Fact]
    public void StringBuildersRespectTheStringLengthLimit()
    {
        const string Expect = "RangeError: Invalid string length";
        Assert.Equal(Expect, Eval("try { 'abc'.repeat(2 ** 30) } catch (e) { e.name + ': ' + e.message }"));
        Assert.Equal(Expect, Eval("try { 'a'.padStart(2 ** 30) } catch (e) { e.name + ': ' + e.message }"));
        Assert.Equal(Expect, Eval("try { 'a'.repeat(1000).repeat(1000) } catch (e) { e.name + ': ' + e.message }", l => l.MaxStringLength = 500_000));
        Assert.Equal(Expect, Eval("try { JSON.stringify(['x'.repeat(400000), 'x'.repeat(400000)]) } catch (e) { e.name + ': ' + e.message }", l => l.MaxStringLength = 500_000));
        Assert.Equal(Expect, Eval("try { 'x'.repeat(300000).concat('y'.repeat(300000)) } catch (e) { e.name + ': ' + e.message }", l => l.MaxStringLength = 500_000));
    }

    [Fact]
    public void RepeatChargesFuelAndMemory()
    {
        var e = Assert.Throws<JsLimitException>(() => new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { MaxAllocatedBytes = 1 << 20 } }).Evaluate("'x'.repeat(10000000)", cancellationToken: Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }

    [Fact]
    public void NumberFormattingIsExact()
    {
        Assert.Equal(
            "1.00|0.0000010|1e+21|-2|1.3|9.999999999999999e-22|1.2e+5|123.456|1.5e-7",
            Eval("[(1.005).toFixed(2), (0.000001).toFixed(7), (1e21).toFixed(2), (-1.5).toFixed(0), (1.25).toPrecision(2), (1e-21).toPrecision(16), (123456).toPrecision(2), (123.456).toPrecision(6), (1.5e-7).toExponential()].join('|')"));
    }

    [Fact]
    public void CaseMappingUsesFullMappingsAndFinalSigma()
    {
        Assert.Equal("STRASSE|ας|ας σα|i̇|ΑΣ", Eval("['stra\\u00DFe'.toUpperCase(), '\\u0391\\u03A3'.toLowerCase(), '\\u0391\\u03A3 \\u03A3\\u0391'.toLowerCase(), '\\u0130'.toLowerCase(), '\\u03B1\\u03C2'.toUpperCase()].join('|')"));
    }

    [Fact]
    public void BigIntConversionsAndRadix()
    {
        Assert.Equal("-1|255|-9223372036854775808|ff|1000", Eval("[BigInt.asIntN(8, 255n), BigInt.asUintN(8, -1n), BigInt.asIntN(64, 2n ** 63n), (255n).toString(16), (8n).toString(2)].join('|')"));
        Assert.Equal("18446744073709552000", Eval("String(Number(2n ** 64n))"));
        Assert.Equal("Infinity", Eval("String(Number(2n ** 1024n))"));
        Assert.Equal("1.7976931348623157e+308", Eval("String(Number(2n ** 1024n - 2n ** 970n - 1n))"));
        Assert.Equal("9007199254740992", Eval("String(Number(2n ** 53n + 1n))"));
    }

    [Fact]
    public void MathSumPreciseIsExact()
    {
        Assert.Equal("0.1|1e+308|true|NaN", Eval("[Math.sumPrecise([1e20, 0.1, -1e20]), Math.sumPrecise([1e308, 1e308, -1e308]), Object.is(Math.sumPrecise([-0]), -0), Math.sumPrecise([Infinity, -Infinity])].join('|')"));
    }

    [Fact]
    public void UriFunctionsRoundTripAndRejectMalformedInput()
    {
        Assert.Equal("%E2%82%AC%F0%9F%98%80|\u20AC\U0001F600|%25", Eval("[encodeURIComponent('\\u20AC\\uD83D\\uDE00'), decodeURIComponent('%E2%82%AC%F0%9F%98%80'), escape('%')].join('|')"));
        Assert.Equal("URIError: URI malformed", Eval("try { decodeURI('%C0%80') } catch (e) { e.name + ': ' + e.message }"));
        Assert.Equal("URIError: URI malformed", Eval("try { encodeURI('\\uD800') } catch (e) { e.name + ': ' + e.message }"));
    }
}
