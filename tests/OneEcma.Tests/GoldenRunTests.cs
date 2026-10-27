using System.Text.Json;
using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Runs every runtime case of the error golden corpus. The error name and message must equal what Node printed (recorded in
/// meta.json), the report's headline and its <c>--&gt;</c> location must equal the expected text.
/// </summary>
public sealed class GoldenRunTests
{
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden");
        foreach (string d in Directory.GetDirectories(dir, "run-*").Order(StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(Path.GetFileName(d));
        }
    }

    private static (JsException? Error, JsLimitException? Limit) Run(string source)
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = 200_000_000, MaxAllocatedBytes = 256L << 20 } });
        engine.DefineGlobalFunction("print", 1, (_, _) => JsValue.Undefined);
        engine.Evaluate("globalThis.console = { log() {}, error() {}, warn() {}, info() {} };", "setup.js");
        try
        {
            engine.Evaluate(source, "case.js", TestContext.Current.CancellationToken);
            return (null, null);
        }
        catch (JsException e)
        {
            return (e, null);
        }
        catch (JsLimitException e)
        {
            return (null, e);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReportMatchesTheCorpusText(string id)
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden", id);
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        JsonElement node = meta.RootElement.GetProperty("node");
        string name = node.GetProperty("name").GetString()!;
        string message = node.GetProperty("message").GetString()!;

        (JsException? err, JsLimitException? limit) = Run(source);
        Diagnostic? report = err?.Report ?? limit?.Report;
        Assert.NotNull(report);
        Assert.Equal(name, report.Kind);
        Assert.Equal(message, report.Headline);

        string actual = report.ToString();
        string path = Path.Combine(dir, "expected.txt");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }

        Assert.Equal(File.ReadAllText(path), actual);
    }

    // The reference report as the design states it, with the two differences the case's meta.json explains.
    private const string ReferenceReport = """
        error[TypeError]: cannot read properties of undefined (reading 'reduce')
          --> checkout.js:12:27
           |
         9 |   const order = await api.getOrder(id);
           |                 ---------------------- `order` comes from here: { id, lineItems, total }
        ...
        12 |   const total = order.items.reduce((s, i) => s + i.price, 0);
           |                       ^^^^^ `order.items` is undefined
           |
           = help: did you mean `order.lineItems`? (Array, 3 items)
           = stack: checkout (checkout.js:12) ← async main (checkout.js:20)
        """;

    [Fact]
    public void TheReferenceReportRendersAsDesigned()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden", "ref-001-checkout");
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
        var ex = Assert.Throws<JsException>(() =>
        {
            engine.Evaluate(File.ReadAllText(Path.Combine(dir, "case.js")), "checkout.js", TestContext.Current.CancellationToken);
            engine.RunJobs(TestContext.Current.CancellationToken);
        });
        string expected = ReferenceReport.Replace("error[TypeError]: cannot", "error[TypeError]: Cannot", StringComparison.Ordinal)
            .Replace("checkout.js:12:27", "checkout.js:12:23", StringComparison.Ordinal) + "\n";
        string actual = ex.Report!.ToString();
        string path = Path.Combine(dir, "expected.txt");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }

        Assert.Equal(expected, actual);
        Assert.Equal(expected, File.ReadAllText(path));
    }
}
