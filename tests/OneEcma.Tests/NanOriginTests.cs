namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// A NaN computed from an undefined carries where the undefined came from in the traced replay. The tag must not change what
/// the script computes (so the replay agrees with the recorded run) and must reach the explanation.
/// </summary>
public sealed class NanOriginTests
{
    private static string Report(string source)
    {
        var engine = new ScriptEngine();
        engine.Evaluate("globalThis.console = { log() {} };", "setup.js", TestContext.Current.CancellationToken);
        JsException e = Assert.Throws<JsException>(() => engine.Evaluate(source, "case.js", TestContext.Current.CancellationToken));
        return e.Report!.ToString();
    }

    [Fact]
    public void NaNKeepsItsSemanticsAndNamesTheMissingProperty()
    {
        string report = Report("""
            const row = { id: 1, unitPrice: 4 };
            const n = row.price * 2;
            const seen = [Object.is(n, NaN), isNaN(n), n !== n, new Set([n, NaN]).size, [n].includes(NaN), [n].indexOf(NaN), String(n), JSON.stringify(n), -n, n + 1, Math.max(n, 1), `${n}`];
            if (seen.join() !== "true,true,true,1,true,-1,NaN,null,NaN,NaN,NaN,NaN") throw new Error(seen.join());
            new Array(n);
            """);
        Assert.Contains("Invalid array length", report, StringComparison.Ordinal);
        Assert.Contains("`row` has no `price`", report, StringComparison.Ordinal);
        Assert.Contains("did you mean `row.unitPrice`", report, StringComparison.Ordinal);
        Assert.DoesNotContain("explanation unavailable", report, StringComparison.Ordinal);
    }

    [Fact]
    public void NaNFromNumberOfAnUndefinedIsTraced()
    {
        string report = Report("""
            const order = { id: 7, lineCount: 2 };
            const count = Number(order.count);
            const copy = count;
            BigInt(copy);
            """);
        Assert.Contains("`order` has no `count`", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ANaNThatDoesNotComeFromAnUndefinedGetsNoOrigin()
    {
        string report = Report("""
            const n = 0 / 0;
            new Array(n);
            """);
        Assert.DoesNotContain("starts here", report, StringComparison.Ordinal);
    }
}
