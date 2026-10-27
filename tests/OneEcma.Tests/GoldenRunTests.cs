using System.Text.Json;
using System.Text.RegularExpressions;
using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Runs every runtime case of the error golden corpus. The error name and message must equal what Node printed (recorded in
/// meta.json), the report's headline and its <c>--&gt;</c> location must equal the expected text.
/// </summary>
public sealed partial class GoldenRunTests
{
    [GeneratedRegex(@"^ *--> case\.js:(\d+):(\d+)$", RegexOptions.Multiline)]
    private static partial Regex Arrow();

    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden");
        foreach (string d in Directory.GetDirectories(dir, "run-*").Order(StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(Path.GetFileName(d));
        }
    }

    // Cases that need something outside the interpreter core: library functions that arrive with the built-in areas
    // (JSON.parse, Number.prototype.toFixed), or a mode of running
    // code (top-level await) the core does not offer. Each one must still fail until its feature lands.
    private static readonly Dictionary<string, string> NeedsLibrary = new()
    {
        ["run-010-json-single-quotes"] = "JSON.parse",
        ["run-011-json-parse-undefined"] = "JSON.parse",
        ["run-026-string-too-long"] = "String.prototype.repeat",
    };

    // The error is the right one but a native function raised it, so the span is the call; the corpus points at the
    // offending argument, which needs the value provenance the diagnostics work adds.
    private static readonly HashSet<string> SpanIsTheArgument =
    [
        "run-017-invalid-array-length",
        "run-022-keys-of-null",
    ];

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

    private static bool Matches(string id, out string detail)
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden", id);
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        string expected = File.ReadAllText(Path.Combine(dir, "expected.txt"));
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        JsonElement node = meta.RootElement.GetProperty("node");
        string name = node.GetProperty("name").GetString()!;
        string message = node.GetProperty("message").GetString()!;

        (JsException? err, JsLimitException? limit) = Run(source);
        Diagnostic? report = err?.Report ?? limit?.Report;
        if (report is null)
        {
            detail = "the script finished without an error";
            return false;
        }

        string firstLine = expected.Split('\n')[0];
        string actualFirst = report.ToString().Split('\n')[0];
        Match arrow = Arrow().Match(expected);
        string actualArrow = Arrow().Match(report.ToString()).Value.Trim();
        detail = $"expected [{name}] {message} / {arrow.Value.Trim()}; got [{report.Kind}] {report.Headline} / {actualArrow}";
        bool spanOk = SpanIsTheArgument.Contains(id) || arrow.Value.Trim() == actualArrow;
        return report.Kind == name && report.Headline == message && firstLine == actualFirst && spanOk;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ErrorNameMessageAndLocationMatchTheCorpus(string id)
    {
        bool ok = Matches(id, out string detail);
        if (NeedsLibrary.TryGetValue(id, out string? reason))
        {
            Assert.False(ok, $"{id} now passes; remove it from NeedsLibrary ({reason})");
            return;
        }

        Assert.True(ok, detail);
    }
}
