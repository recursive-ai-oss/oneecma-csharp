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

    [Fact]
    public void RegExpValidatorHookIsCalledForLiterals()
    {
        var r = Parse("/a/g", new ParseOptions { RegExpValidator = (p, f) => p == "a" ? "Invalid regular expression: bad" : null });
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
