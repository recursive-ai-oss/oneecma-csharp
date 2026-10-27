using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Inputs the fuzzers found, reduced to small scripts.</summary>
public sealed class FuzzRegressionTests
{
    [Theory]
    [InlineData("typeof {a = 1};")]
    [InlineData("void {a = 1};")]
    [InlineData("!{a = 1};")]
    [InlineData("delete {a = 1};")]
    [InlineData("++{a = 1}.x;")]
    [InlineData("new {a = 1};")]
    [InlineData("class A extends {a = 1} {}")]
    [InlineData("async function f() { await {a = 1}; }")]
    public void AShorthandInitializerUnderAnOperatorIsASyntaxError(string source)
    {
        // The operand of a unary operator is not a cover grammar for a pattern, so the initializer must be reported, not compiled.
        IReadOnlyList<Diagnostic> errors = ScriptEngine.Check(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(errors, d => d.Headline == "Invalid shorthand property initializer");
    }

    [Fact]
    public void ASourceWithALoneSurrogateReplaysToTheSameReport()
    {
        // A JavaScript string may hold half of a surrogate pair; the record log must carry it unchanged.
        var engine = new ScriptEngine();
        string src = "var s = '\uD801'; s.nope.x;";
        JsException first = Assert.Throws<JsException>(() => engine.Evaluate(src, "t.js", TestContext.Current.CancellationToken));
        ReplayResult r = ScriptEngine.Replay(engine.RecordLog, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(r.Divergence);
        Assert.Equal(first.Report!.ToString(), r.Exception!.Report!.ToString());
        Assert.Contains('\uD801', r.Exception.Report.ToString());

        RecordLog loaded = RecordLog.FromArray(engine.RecordLog.ToArray());
        Assert.Equal(first.Report.ToString(), ScriptEngine.Replay(loaded, cancellationToken: TestContext.Current.CancellationToken).Exception!.Report!.ToString());
    }
}
