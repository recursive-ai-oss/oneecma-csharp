using RecursiveAI.OneEcma.Cli;

namespace RecursiveAI.OneEcma.Tests;

public sealed class ReplTests
{
    private static (string Out, string Err) Session(string input)
    {
        var engine = new ScriptEngine();
        engine.DefineGlobalFunction("print", 1, (_, _) => JsValue.Undefined);
        var o = new StringWriter();
        var e = new StringWriter();
        new Repl(engine, new StringReader(input), o, e, ansi: false, showPrompt: false).Run();
        return (o.ToString().ReplaceLineEndings("\n"), e.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ResultsAreShownAndStateSurvives()
    {
        (string o, string e) = Session("var x = 20\nx + 22\n'hi'\n[1, 2]\n({ a: 1 })\n1n\n");
        Assert.Equal("42\n'hi'\n[\n  1,\n  2\n]\n{\n  \"a\": 1\n}\n1n\n", o);
        Assert.Equal(string.Empty, e);
    }

    [Fact]
    public void MultiLineEntriesContinueUntilComplete()
    {
        (string o, _) = Session("function f(a) {\n  return a * 2;\n}\nf(4)\nconst s = `a\nb`;\ns.length\n[1,\n2,\n3].length\n");
        Assert.Equal("8\n3\n3\n", o);
    }

    [Fact]
    public void TopLevelAwaitWorks()
    {
        (string o, string e) = Session("await Promise.resolve(5)\nconst y = await 6; y\nawait new Promise(r => r('done'))\n");
        Assert.Equal("5\n'done'\n", o);
        Assert.Equal(string.Empty, e);
    }

    [Fact]
    public void ErrorsPrintTheSameReportAsRun()
    {
        (string o, string e) = Session("let u;\nu.x\n1 + 1\n");
        Assert.Equal("2\n", o);
        Assert.StartsWith("error[TypeError]: Cannot read properties of undefined (reading 'x')\n --> repl:2:1", e, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntaxErrorsInACompleteEntryAreReportedNotContinued()
    {
        (string o, string e) = Session("let = ;\n1 + 2\n");
        Assert.Equal("3\n", o);
        Assert.Contains("error[SyntaxError]", e, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitStopsAndClearDropsTheEntry()
    {
        (string o, _) = Session("{\n.clear\n7\n.exit\n8\n");
        Assert.Equal("7\n", o);
    }
}
