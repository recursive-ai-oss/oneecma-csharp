using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// The compact report style. Representative corpus cases render to <c>expected.compact.txt</c> byte for byte (regenerate with
/// <c>ONEECMA_UPDATE_GOLDEN=1</c>), and across the whole corpus no compact report is larger than the full one and the median is at least 13% smaller
/// (most corpus reports are one label and one help, which compact keeps; the savings come from the cases with context lines,
/// notes, extra helps and long lines).
/// Set <c>ONEECMA_SIZE_TABLE</c> to a file path to write the per-case table.
/// </summary>
public sealed class CompactGoldenTests
{
    private static readonly RenderOptions Compact = new() { Compact = true };

    private static string Golden => Path.Combine(RepoPaths.Root, "testdata", "golden");

    private static readonly string[] CaseIds =
        [
            "ref-001-checkout", "run-001-undefined-property-reduce", "run-045-upstream-missing-property-through-helper",
            "run-018-infinite-recursion", "run-036-long-line-two-windows", "run-004-missing-await", "run-020-reduce-empty",
            "syn-033-two-errors", "syn-003-missing-close-paren", "syn-040-long-line-missing-paren",
            "syn-001-extra-close-paren#repair", "syn-008-smart-double-quotes#repair",
            "host-001-decimal-price-as-string", "host-003-misspelled-property", "host-004-dotnet-exception-uncaught",
            "lim-001-infinite-loop",
        ];

    /// <summary>Cases with a checked-in compact report.</summary>
    public static IEnumerable<TheoryDataRow<string>> GoldenCases() => CaseIds.Select(id => new TheoryDataRow<string>(ResolveId(id)));

    private static string ResolveId(string id)
    {
        // Allow a prefix before '-' + slug so a renamed slug is a one-line fix.
        string name = id.Split('#')[0];
        if (Directory.Exists(Path.Combine(Golden, name)))
        {
            return id;
        }

        string prefix = string.Join('-', name.Split('-').Take(2));
        string found = Directory.GetDirectories(Golden, prefix + "*").Select(Path.GetFileName).Single()!;
        return id.Contains('#', StringComparison.Ordinal) ? found + "#" + id.Split('#')[1] : found;
    }

    private static async Task<(string Full, string Compact)> Both(string id)
    {
        bool repair = id.EndsWith("#repair", StringComparison.Ordinal);
        string name = id.Split('#')[0];
        string dir = Path.Combine(Golden, name);
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        var ct = TestContext.Current.CancellationToken;

        if (name.StartsWith("syn-", StringComparison.Ordinal))
        {
            if (repair)
            {
                RepairResult r = SyntaxRepair.Repair(source, new ParseOptions { FileName = "case.js" }, cancellationToken: ct);
                var full = new RenderOptions { RepairOutcome = "the repaired source ran to completion and is returned with this report" };
                var compact = new RenderOptions { Compact = true };
                return (DiagnosticRenderer.RenderRepair(r.Repairs[0], full), DiagnosticRenderer.RenderRepair(r.Repairs[0], compact));
            }

            var result = JsParser.Parse(source, new ParseOptions { FileName = "case.js" }, ct);
            return (DiagnosticRenderer.RenderAll(result.Diagnostics), DiagnosticRenderer.RenderAll(result.Diagnostics, Compact));
        }

        if (name.StartsWith("host-", StringComparison.Ordinal))
        {
            JsException? e = await new HostFixture().RunAsync(source);
            Assert.NotNull(e);
            return (DiagnosticRenderer.Render(e.Report!), DiagnosticRenderer.Render(e.Report!, Compact));
        }

        if (name.StartsWith("ref-", StringComparison.Ordinal))
        {
            JsException e = RunReference(source);
            return (DiagnosticRenderer.Render(e.Report!), DiagnosticRenderer.Render(e.Report!, Compact));
        }

        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = 200_000_000, MaxAllocatedBytes = 256L << 20 } });
        engine.DefineGlobalFunction("print", 1, (_, _) => JsValue.Undefined);
        engine.Evaluate("globalThis.console = { log() {}, error() {}, warn() {}, info() {} };", "setup.js");
        Diagnostic? report;
        try
        {
            engine.Evaluate(source, "case.js", ct);
            report = null;
        }
        catch (JsException e)
        {
            report = e.Report;
        }
        catch (JsLimitException e)
        {
            report = e.Report;
        }

        Assert.NotNull(report);
        return (DiagnosticRenderer.Render(report), DiagnosticRenderer.Render(report, Compact));
    }

    private static JsException RunReference(string source)
    {
        var engine = new ScriptEngine();
        engine.DefineGlobalFunction("print", 1, (_, _) => JsValue.Undefined);
        engine.DefineGlobalFunction("getOrder", 1, (_, a) =>
        {
            JsRealm realm = engine.MainRealm;
            JsObject order = realm.NewObject();
            realm.DefineData(order, "id", a[0]);
            var lines = new List<JsValue>();
            foreach (int price in (ReadOnlySpan<int>)[5, 7, 9])
            {
                JsObject line = realm.NewObject();
                realm.DefineData(line, "price", JsValue.FromNumber(price));
                lines.Add(JsValue.FromObject(line));
            }

            realm.DefineData(order, "lineItems", JsValue.FromObject(realm.NewArray(lines.ToArray())));
            realm.DefineData(order, "total", JsValue.FromNumber(21));
            return JsValue.FromObject(order);
        });
        engine.Evaluate("globalThis.api = { getOrder };", "setup.js", TestContext.Current.CancellationToken);
        return Assert.Throws<JsException>(() =>
        {
            engine.Evaluate(source, "checkout.js", TestContext.Current.CancellationToken);
            engine.RunJobs(TestContext.Current.CancellationToken);
        });
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task CompactReportMatchesTheCorpus(string id)
    {
        (string full, string compact) = await Both(id);
        string dir = Path.Combine(Golden, id.Split('#')[0]);
        string path = Path.Combine(dir, id.EndsWith("#repair", StringComparison.Ordinal) ? "expected.compact.repair.txt" : "expected.compact.txt");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, compact);
        }

        Assert.Equal(File.ReadAllText(path), compact);
        Assert.True(compact.Length < full.Length, "compact must be smaller than full");
        Assert.StartsWith(full[..full.IndexOf('\n', StringComparison.Ordinal)], compact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactIsNeverLargerAndTheMedianIsSmaller()
    {
        var rows = new List<(string Id, int Full, int Compact)>();
        foreach (string d in Directory.GetDirectories(Golden).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(d);
            if (!(name.StartsWith("run-", StringComparison.Ordinal) || name.StartsWith("syn-", StringComparison.Ordinal) || name.StartsWith("host-", StringComparison.Ordinal)))
            {
                continue;
            }

            if (name.StartsWith("syn-", StringComparison.Ordinal) && !File.ReadAllText(Path.Combine(d, "meta.json")).Contains("\"kind\": \"syntax\"", StringComparison.Ordinal))
            {
                continue;
            }

            (string f, string c) = await Both(name);
            rows.Add((name, f.Length, c.Length));
            if (File.Exists(Path.Combine(d, "repaired.js")))
            {
                (f, c) = await Both(name + "#repair");
                rows.Add((name + " (repair)", f.Length, c.Length));
            }
        }

        double Cut((string Id, int Full, int Compact) r) => 100.0 * (r.Full - r.Compact) / r.Full;
        double[] cuts = rows.Select(Cut).Order().ToArray();
        double median = cuts[cuts.Length / 2];
        string? table = Environment.GetEnvironmentVariable("ONEECMA_SIZE_TABLE");
        if (table is not null)
        {
            var sb = new StringBuilder("case\tfull\tcompact\treduction%\n");
            foreach (var r in rows)
            {
                sb.Append(r.Id).Append('\t').Append(r.Full).Append('\t').Append(r.Compact).Append('\t').Append(Cut(r).ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
            }

            sb.Append("TOTAL\t").Append(rows.Sum(r => r.Full)).Append('\t').Append(rows.Sum(r => r.Compact)).Append("\nMEDIAN\t")
              .Append(median.ToString("F1", CultureInfo.InvariantCulture)).Append("\nCASES\t").Append(rows.Count).Append('\n');
            File.WriteAllText(table, sb.ToString());
        }

        Assert.True(rows.All(r => r.Compact <= r.Full));
        Assert.True(median >= 13.0, "median reduction was " + median.ToString("F1", CultureInfo.InvariantCulture) + "%");
    }
}
