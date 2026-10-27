using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

public class ParserTests
{
    private static ParseResult Parse(string src, ParseOptions? o = null) =>
        JsParser.Parse(src, o, TestContext.Current.CancellationToken);

    public static IEnumerable<TheoryDataRow<string, string>> DeepInputs()
    {
        const int N = 100_000;
        yield return new("parens", new string('(', N));
        yield return new("brackets", new string('[', N));
        yield return new("braces", new string('{', N));
        yield return new("object", string.Concat(Enumerable.Repeat("({a:", N)));
        yield return new("functions", string.Concat(Enumerable.Repeat("function f(){", N)));
        yield return new("arrows", string.Concat(Enumerable.Repeat("()=>", N)));
        yield return new("unary", new string('!', N) + "a");
        yield return new("pow", string.Join("**", Enumerable.Repeat("a", N)));
        yield return new("assign", string.Join("=", Enumerable.Repeat("a", N)));
        yield return new("plus", string.Join("+", Enumerable.Repeat("a", N)));
        yield return new("ternary", string.Concat(Enumerable.Repeat("a?", N)));
        yield return new("template", string.Concat(Enumerable.Repeat("`${", N)));
        yield return new("if", string.Concat(Enumerable.Repeat("if(a)", N)));
        yield return new("class", string.Concat(Enumerable.Repeat("class A extends", N)));
    }

    [Theory]
    [MemberData(nameof(DeepInputs))]
    public void DeepSourceEndsInADiagnosticOnASmallStack(string name, string source)
    {
        ParseResult? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = JsParser.Parse(source); }
            catch (Exception e) { error = e; }
        }, 256 * 1024);
        t.Start();
        t.Join();
        Assert.Null(error);
        // A flat operator chain is valid source; it must simply not crash the process.
        Assert.True(result!.HasErrors || name == "plus", name);
    }

    // Minimized inputs that once crashed the parser (found by the fuzzer); none may throw.
    [Theory]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("`\\u")]
    [InlineData(".?`\\u")]
    [InlineData("function one() {\"\n  return (1 + );\n}")]
    public void FuzzRegressionsDoNotThrow(string source)
    {
        Assert.True(Parse(source).HasErrors);
    }

    [Fact]
    public void ManyErrorsAfterManyLinesStayLinear()
    {
        // Found by the fuzzer: error help scanned the whole text before the error, so this took seconds.
        string src = new string('\n', 20_000) + string.Concat(Enumerable.Repeat("?", 9570));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(Parse(src).HasErrors);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
    }

    [Fact]
    public void ModerateNestingStillParses()
    {
        string src = new string('(', 100) + "1" + new string(')', 100);
        Assert.False(Parse(src).HasErrors);
    }

    [Fact]
    public void DepthLimitIsReportedWithItsCode()
    {
        var r = Parse(new string('[', 5000) + new string(']', 5000), new ParseOptions { MaxDepth = 100 });
        Assert.Contains(r.Diagnostics, d => d.Code == "depth-limit");
    }

    [Fact]
    public void AnnexBFunctionInBlockIsHoistedInSloppyModeOnly()
    {
        const string src = "{ function f() {} }";
        Assert.False(Parse(src).HasErrors);
        Assert.False(Parse(src, new ParseOptions { Strict = true }).HasErrors);
    }

    [Theory]
    [InlineData("let a; let a;", "Identifier 'a' has already been declared")]
    [InlineData("'use strict'; with (a) {}", "Strict mode code may not include a with statement")]
    [InlineData("function f() { 'use strict'; var eval; }", "Unexpected eval or arguments in strict mode")]
    [InlineData("a +", "Unexpected end of input")]
    public void EarlyAndSyntaxErrorsUseV8Wording(string src, string headline)
    {
        Assert.Equal(headline, Parse(src).Diagnostics[0].Headline);
    }

    [Fact]
    public void EveryErrorIsReportedNotJustTheFirst()
    {
        var r = Parse("let a = ;\nlet c = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];\nfoo(1,,);\nlet d = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];\nlet b = ;");
        Assert.True(r.Diagnostics.Count >= 3);
    }

    // ECMA-262 15.3: ConciseBody[?In] keeps the In parameter of the arrow function, so in a for-loop head the
    // body of an arrow function cannot contain a bare 'in'. Expected results recorded from Node.
    [Theory]
    [InlineData("for (f = x => x in y;;) break;", false)]
    [InlineData("for (f = () => {} in y;;) break;", false)]
    [InlineData("for (var f = () => a in b;;) break;", false)]
    [InlineData("for (var f = async x => x in b;;) break;", false)]
    [InlineData("for (x => x in y;;) break;", false)]
    [InlineData("for (f = x => a ? b : c in d;;) break;", false)]
    [InlineData("for (f = x => (x in y);;) break;", true)]
    [InlineData("for (f = x => a ? b in c : d;;) break;", true)]
    [InlineData("for (f = x => { return 'a' in y; };;) break;", true)]
    [InlineData("for (f = (x = 'a' in y) => x;;) break;", true)]
    [InlineData("f = x => x in y;", true)]
    public void ArrowBodyInheritsTheInParameter(string src, bool valid) => Assert.Equal(valid, !Parse(src).HasErrors);

    // ECMA-262 15.7.1: ClassStaticBlockBody is parsed with [+Await] and it is an error for it to contain 'await' as an
    // identifier. An arrow function's parameters inherit that, its concise body is [~Await] (15.3), as are nested
    // ordinary functions. Expected results recorded from Node.
    [Theory]
    [InlineData("class C { static { (await => 0); } }", false)]
    [InlineData("class C { static { (async await => 0); } }", false)]
    [InlineData("class C { static { (x = await) => 0; } }", false)]
    [InlineData("class C { static { ((x = await) => 0); } }", false)]
    [InlineData("class C { static { var await; } }", false)]
    [InlineData("class C { static { await; } }", false)]
    [InlineData("class C { static { ({ await }); } }", false)]
    [InlineData("class C { static { class D { [await] = 1 } } }", false)]
    [InlineData("class C { static { (() => await); } }", true)]
    [InlineData("class C { static { (() => { await; }); } }", true)]
    [InlineData("class C { static { (() => { class await {} }); } }", true)]
    [InlineData("class C { static { function f() { await; } } }", true)]
    [InlineData("class C { static { (function await() {}); } }", true)]
    [InlineData("class C { static { (async () => await 1); } }", true)]
    public void AwaitInsideClassStaticBlock(string src, bool valid) => Assert.Equal(valid, !Parse(src).HasErrors);

    [Theory]
    [InlineData("/(/", "Invalid regular expression: /(/: Unterminated group", 1, 2)]
    [InlineData("x = /a/g; y = /(?<n>a)(?<n>b)/", "Invalid regular expression: /(?<n>a)(?<n>b)/: Duplicate capture group name", 24, 27)]
    [InlineData("/[b-a]/u", "Invalid regular expression: /[b-a]/u: Range out of order in character class", 2, 5)]
    public void RegExpPatternErrorsAreEarlyErrorsWithTheirExactSpan(string src, string headline, int start, int end)
    {
        var r = Parse(src);
        Assert.True(r.HasErrors);
        Assert.Equal(headline, r.Diagnostics[0].Headline);
        Assert.Equal(start, r.Diagnostics[0].PrimarySpan.Start);
        Assert.Equal(end, r.Diagnostics[0].PrimarySpan.End);
    }

    [Fact]
    public void RegExpValidatorHookIsCalledForLiterals()
    {
        var r = Parse("/a/g", new ParseOptions { RegExpValidator = (p, f, at) => p == "a" ? [new RecursiveAI.OneEcma.RegExp.RegExpDiagnostic(0, 1, "bad", "Invalid regular expression: /a/g: bad")] : [] });
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void RendererPlainHasNoEscapesAndAnsiHasThem()
    {
        var r = Parse("let a = ;");
        string plain = DiagnosticRenderer.RenderAll(r.Diagnostics);
        string ansi = DiagnosticRenderer.RenderAll(r.Diagnostics, new RenderOptions { Ansi = true });
        Assert.DoesNotContain('\u001b', plain);
        Assert.Contains('\u001b', ansi);
        Assert.StartsWith("error[SyntaxError]: Unexpected token ';'", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeFlatSourceParses()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50_000; i++) sb.Append("var v").Append(i).Append(" = [").Append(i).Append(", 'x', {a: ").Append(i).Append("}];\n");
        Assert.False(Parse(sb.ToString()).HasErrors);
    }
}
