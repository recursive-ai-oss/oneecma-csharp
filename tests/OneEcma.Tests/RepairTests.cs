using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

public class RepairTests
{
    private static RepairResult Repair(string source, RepairOptions? limits = null) =>
        SyntaxRepair.Repair(source, null, limits, TestContext.Current.CancellationToken);

    private static void AssertRepairedParses(RepairResult r)
    {
        Assert.False(JsParser.Parse(r.RepairedSource, null, TestContext.Current.CancellationToken).HasErrors);
    }

    [Fact]
    public void SourceThatParsesIsNeverTouched()
    {
        const string source = "const a = 1;\nconsole.log(a, and_b);\nlet t = `x\n  y`;\n";
        RepairResult r = Repair(source);
        Assert.False(r.Changed);
        Assert.Equal(source, r.RepairedSource);
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void FencedProgramIsStrippedEvenThoughItParses()
    {
        RepairResult r = Repair("```js\nlet a = 1;\n```\n");
        Assert.Equal("let a = 1;\n", r.RepairedSource);
        Assert.Equal("R003", Assert.Single(r.Repairs).Id);
    }

    [Fact]
    public void FenceIsKeptWhenWhatRemainsDoesNotParse()
    {
        // Parses as templates today and would stop parsing without the fence, so it is left alone.
        RepairResult r = Repair("```\n)\n```\n");
        Assert.False(r.Changed);
    }

    [Fact]
    public void AmbiguousCloserIsOnlySuggested()
    {
        // The `}` could be extra, or `if (x)` could have lost its `{`.
        RepairResult r = Repair("if (x)\n  y();\n}\n");
        Assert.False(r.Changed);
        Assert.False(r.Parses);
        Assert.NotEmpty(r.Diagnostics);
    }

    [Fact]
    public void IndependentRepairsAreAllListedAndTheSourceParses()
    {
        RepairResult r = Repair("let a = 1 and 2;\nlet b = {\n  p: 1\n  q: 2\n};\nfoo(a));\n");
        Assert.Equal(["R006", "R005", "R001"], r.Repairs.Select(x => x.Id));
        Assert.Equal("let a = 1 && 2;\nlet b = {\n  p: 1,\n  q: 2\n};\nfoo(a);\n", r.RepairedSource);
        AssertRepairedParses(r);
    }

    [Fact]
    public void EditsAreInOriginalCoordinates()
    {
        const string source = "foo(a));\nbar(b));\n";
        RepairResult r = Repair(source);
        Assert.Equal(2, r.Repairs.Count);
        Assert.Equal(source.IndexOf("));", StringComparison.Ordinal) + 1, r.Repairs[0].Edits[0].Start);
        Assert.Equal(source.LastIndexOf("));", StringComparison.Ordinal) + 1, r.Repairs[1].Edits[0].Start);
        Assert.Equal(r.Repairs[1].Edits[0].Start, r.Repairs[1].Span.Start);
    }

    [Fact]
    public void NothingIsReturnedHalfRepaired()
    {
        // The stray `)` is repairable; the unterminated string is not.
        const string source = "foo(a));\nlet s = \"abc;\n";
        RepairResult r = Repair(source);
        Assert.False(r.Changed);
        Assert.Equal(source, r.RepairedSource);
        Assert.False(r.Parses);
    }

    [Fact]
    public void RepairCapIsReportedAndLeavesTheSourceAlone()
    {
        RepairResult r = Repair("foo(a));\nbar(b));\nbaz(c));\n", new RepairOptions { MaxRepairs = 2 });
        Assert.False(r.Changed);
        Assert.Contains(r.Diagnostics, d => d.Code == "repair-limit" && d.Severity == Severity.Warning);
    }

    [Fact]
    public void TrialParseBudgetIsReported()
    {
        RepairResult r = Repair("foo(a));\nbar(b));\n", new RepairOptions { MaxParses = 3 });
        Assert.False(r.Changed);
        Assert.Contains(r.Diagnostics, d => d.Code == "repair-limit");
    }

    [Theory]
    [InlineData("let x = 'a;\n")]
    [InlineData("function f( {\n")]
    [InlineData("const a = ;\n")]
    [InlineData("class { \n")]
    public void UnrepairableSourceKeepsItsErrors(string source)
    {
        RepairResult r = Repair(source);
        Assert.False(r.Changed);
        Assert.False(r.Parses);
    }

    [Fact]
    public void TruncatedCallInsideAnArgumentListIsClosed()
    {
        RepairResult r = Repair("foo(1,\n  2\n");
        Assert.Equal("foo(1,\n  2)\n", r.RepairedSource);
    }

    [Fact]
    public void MissingCloserInTheMiddleIsNotGuessedFromTheEnd()
    {
        // The second function is indented like the first, so the first `{` was closed earlier: only a suggestion.
        RepairResult r = Repair("function a() {\n  if (x) {\n    foo();\n\nfunction b() {\n  bar();\n}\n");
        Assert.False(r.Changed);
    }

    [Fact]
    public void ParseAloneNeverRepairs()
    {
        ParseResult r = JsParser.Parse("foo(a));", null, TestContext.Current.CancellationToken);
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void ChainedTypeScriptCastsAndSignaturesAreErased()
    {
        RepairResult r = Repair("function f(a: number, b?: string): void {\n  return;\n}\nlet x: Array<number> = [1];\nconst y = x as unknown as number[];\n");
        Assert.Equal("function f(a, b) {\n  return;\n}\nlet x = [1];\nconst y = x;\n", r.RepairedSource);
    }

    [Fact]
    public void ScriptEngineExposesRepair()
    {
        RepairResult r = ScriptEngine.CheckAndRepair("console.log(\u201chi\u201d);", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("console.log(\"hi\");", r.RepairedSource);
    }

    [Fact]
    public void IndependentErrorsOnNearbyLinesAreEachReported()
    {
        IReadOnlyList<Diagnostic> d = JsParser.Parse("let a = ;\nlet b = (1 + );\n", null, TestContext.Current.CancellationToken).Diagnostics;
        Assert.Equal(2, d.Count);

        d = JsParser.Parse("const total = Math.max(1, 2, 3;\nlet y = [1, 2\n\nfunction f( {\n", null, TestContext.Current.CancellationToken).Diagnostics;
        Assert.Equal(3, d.Count);
    }

    [Fact]
    public void FStringHintSurvivesAStringThatContainsALineSeparator()
    {
        // Found by the fuzzer: the string spans a U+2028, which the line table treats as a line end.
        string source = "const who = \"Ada\";\nconsole.log(f\"h\u2028 {who}\"-->-->";
        RepairResult r = Repair(source);
        Assert.False(r.Changed);
    }

    [Theory]
    [InlineData("console.log(1)));\n", "console.log(1);\n")]
    [InlineData("function f() {\n  return 1;\n}}}\nf();\n", "function f() {\n  return 1;\n}\nf();\n")]
    public void ARunOfExtraClosersIsRemovedTogether(string source, string expected)
    {
        RepairResult r = Repair(source);
        AssertRepairedParses(r);
        Assert.Equal(expected, r.RepairedSource);
        Assert.Equal("R001", Assert.Single(r.Repairs).Id);
    }

    [Fact]
    public void ARunOfExtraBracketsIsOnlyASuggestionBecauseAnOpenerCouldBeMissing()
    {
        RepairResult r = Repair("const o = { a: [1, 2]]];\n");
        Assert.False(r.Changed);
        Assert.NotEmpty(r.Diagnostics);
    }

    [Theory]
    [InlineData("const v = foo(1;\n", "const v = foo(1);\n")]
    [InlineData("console.log(foo(1;\nconsole.log(2);\n", "console.log(foo(1));\nconsole.log(2);\n")]
    [InlineData("const a = [1, 2;\n", "const a = [1, 2];\n")]
    [InlineData("const v = foo(1; // note\n", "const v = foo(1); // note\n")]
    public void AClosingParenBeforeASemicolonThatEndsTheLineIsInserted(string source, string expected)
    {
        RepairResult r = Repair(source);
        AssertRepairedParses(r);
        Assert.Equal(expected, r.RepairedSource);
        Assert.Equal("R012", Assert.Single(r.Repairs).Id);
    }

    [Theory]
    [InlineData("const v = foo(1; bar(2));\n")]
    [InlineData("foo(1,\n  bar(2;\n")]
    [InlineData("const v = foo({ a: 1;\n")]
    public void AMissingParenIsOnlyASuggestionWhenTheCloserPositionIsAGuess(string source)
    {
        RepairResult r = Repair(source);
        Assert.DoesNotContain(r.Repairs, x => x.Id == "R012");
        Assert.False(r.Changed);
    }

    [Theory]
    [InlineData("const names: string[] = [];\nlet n: number = 0;\nvar f: boolean;\nconst m: Map<string, number> = new Map();\n", "const names = [];\nlet n = 0;\nvar f;\nconst m = new Map();\n")]
    public void VariableAnnotationsAreErased(string source, string expected)
    {
        RepairResult r = Repair(source);
        AssertRepairedParses(r);
        Assert.Equal(expected, r.RepairedSource);
        Assert.All(r.Repairs, x => Assert.Equal("R007", x.Id));
    }
}
