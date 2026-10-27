using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Rendering a report never throws and never needs its fallback: every corpus script and seed renders at several widths in the
/// default and compact styles, and the long-line cases that once made the windowed renderer throw stay fixed.
/// </summary>
public sealed class RendererRobustnessTests
{
    private static readonly int[] Widths = [40, 80, 100, 200];

    // A long line sits between two labelled lines, so it is printed as context and has no label of its own.
    private static readonly string Long = string.Concat(Enumerable.Range(0, 60).Select(i => "var v" + i + " = " + i + "; "));

    public static IEnumerable<TheoryDataRow<string>> Regressions() =>
    [
        new("const cfg = {};\n" + Long + "\nconst t = cfg.timeout.ms;\n"),
        new("const o = { a: 1 };\n" + Long + "\nreturn o.b.c;\n"),
        new("var x = [];\n" + Long + "\nx.y.z();\n"),
    ];

    private static IEnumerable<Diagnostic> ReportsFor(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (Diagnostic d in JsParser.Parse(source, new ParseOptions { FileName = "case.js" }, ct).Diagnostics)
        {
            yield return d;
        }

        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = 3_000_000, MaxAllocatedBytes = 64L << 20 } });
        engine.DefineGlobalFunction("print", 1, (_, _) => JsValue.Undefined);
        Diagnostic? report = null;
        try
        {
            engine.Evaluate(source, "case.js", ct);
        }
        catch (JsException e)
        {
            report = e.Report;
        }
        catch (JsLimitException e)
        {
            report = e.Report;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Other failures are not reports.
        }

        if (report is not null)
        {
            yield return report;
        }
    }

    private static void RenderEverywhere(string source)
    {
        foreach (Diagnostic d in ReportsFor(source))
        {
            foreach (int width in Widths)
            {
                foreach (bool compact in new[] { false, true })
                {
                    var o = new RenderOptions { MaxLineWidth = width, Compact = compact, ShowLocals = true };
                    string text = DiagnosticRenderer.Render(d, o);
                    Assert.DoesNotContain(DiagnosticRenderer.FallbackNote, text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Regressions))]
    public void LongContextLinesRender(string source) => RenderEverywhere(source);

    [Fact]
    public void EveryGoldenCaseAndSeedRendersAtEveryWidthInBothStyles()
    {
        int n = 0;
        foreach (string file in Directory.GetFiles(Path.Combine(RepoPaths.Root, "testdata", "golden"), "case.js", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(RepoPaths.Root, "testdata", "fuzz-seeds"), "*.js")))
        {
            RenderEverywhere(File.ReadAllText(file));
            n++;
        }

        Assert.True(n > 100);
    }

    [Fact]
    public void ARendererFailureFallsBackToHeadlineAndLocation()
    {
        // A diagnostic whose span lies past its source cannot be drawn; the report still names the error.
        var src = new Text.SourceText("let a;", "case.js");
        var d = new Diagnostic("SyntaxError", "E0", "boom", src, new Text.Span(500, 600));
        string text = DiagnosticRenderer.Render(d);
        Assert.StartsWith("error[SyntaxError]: boom\n", text, StringComparison.Ordinal);
    }
}
