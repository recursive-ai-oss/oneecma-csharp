using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Intl;
using RecursiveAI.OneEcma.TextServices;
using RecursiveAI.OneEcma.RegExp;
using RecursiveAI.OneEcma.SourceMaps;
using RecursiveAI.OneEcma.Text;

// Published with NativeAOT in CI: the build must produce no trim or AOT warnings and this must run.
// RegExp: loads the embedded Unicode table (a manifest resource) and runs a property escape and a case-folded match.
var re = RegExpProgram.Compile(@"^\p{Script=Greek}+(?<n>\w)\k<n>$", "iu");
var hit = re.Program?.Exec("αΑss", 0, false);
if (hit?.Match is null || hit.Match.End != 4)
{
    Console.Error.WriteLine("smoke: regexp failed");
    return 1;
}

var engine = new ScriptEngine();
if (ScriptEngine.Check("let a = 1; function f(x) { return x + a; }").Count != 0)
{
    Console.Error.WriteLine("smoke: valid script reported errors");
    return 1;
}

var errors = ScriptEngine.Check("let a = ;\nlet ok = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];\nlet b = (1 + );");
string report = DiagnosticRenderer.RenderAll(errors);
if (errors.Count != 2 || !report.StartsWith("error[SyntaxError]: Unexpected token ';'", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected report\n" + report);
    return 1;
}

RepairResult repaired = ScriptEngine.CheckAndRepair("const row = [1, 2, 3]];\nconsole.log(\u201chi\u201d);\n");
string repairReport = DiagnosticRenderer.RenderRepairResult(repaired);
if (!repaired.Parses || repaired.Repairs.Count != 2
    || repaired.RepairedSource != "const row = [1, 2, 3];\nconsole.log(\"hi\");\n"
    || !repairReport.StartsWith("repair[R001]: removed an extra closing delimiter", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected repair\n" + repairReport);
    return 1;
}

// JSON report and source map: a one-segment map sends the error in generated code back to the original file.
string json = DiagnosticJson.Render(errors);
if (!json.StartsWith("{\"version\":1,\"format\":\"oneecma.report\"", StringComparison.Ordinal) || !json.Contains("\"headline\":\"Unexpected token ';'\"", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected json\n" + json);
    return 1;
}

SourceMap? map = SourceMap.Parse("{\"version\":3,\"sources\":[\"app.ts\"],\"sourcesContent\":[\"let a: number = ;\\n\"],\"names\":[],\"mappings\":\"AAAA\"}").Map;
IReadOnlyList<Diagnostic> generatedErrors = ScriptEngine.Check("let a = ;\n", new RecursiveAI.OneEcma.Syntax.ParseOptions { FileName = "app.js" });
Diagnostic mapped = SourceMapReports.Remap(generatedErrors[0], map!);
if (mapped.Source.Name != "app.ts" || mapped.Mapping is not { SpansAreOriginal: true } || !DiagnosticRenderer.Render(mapped).Contains("generated position: app.js:1:9", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected source map report\n" + DiagnosticRenderer.Render(mapped));
    return 1;
}

// Text services: normalization, collation (with a tailoring and numeric ordering) and segmentation.
if (Normalizer.Normalize("e\u0301", NormalizationForm.Nfc) != "\u00E9"
    || Normalizer.Normalize("\uFB01", NormalizationForm.Nfkd) != "fi"
    || !Normalizer.IsNormalized("abc", NormalizationForm.Nfd))
{
    Console.Error.WriteLine("smoke: normalization");
    return 1;
}

TextCollator spanish = TextCollator.Create("es");
TextCollator numeric = TextCollator.Create("en", new CollatorOptions { Numeric = true });
if (spanish.Compare("\u00F1", "nz") <= 0 || numeric.Compare("a2", "a10") >= 0 || spanish.GetSortKey("a").Length == 0)
{
    Console.Error.WriteLine("smoke: collation");
    return 1;
}

int words = 0;
int graphemes = 0;
foreach (TextSegment s in TextSegmenter.Enumerate("Hello, wor\u0301ld!", SegmentGranularity.Word))
{
    if (s.IsWordLike)
    {
        words++;
    }
}

foreach (TextSegment s in TextSegmenter.Enumerate("e\u0301\uD83C\uDDFA\uD83C\uDDF8", SegmentGranularity.Grapheme))
{
    graphemes++;
}

if (words != 2 || graphemes != 2)
{
    Console.Error.WriteLine($"smoke: segmentation {words} {graphemes}");
    return 1;
}

// Intl: locale data is an embedded resource, so this checks it loads under NativeAOT.
NumberFormatService german = NumberFormatService.Create(["de"], new NumberFormatOptions { Style = "currency", Currency = "EUR" }, "en-US");
string money = german.Format(DecimalNumber.FromDouble(1234.5));
DisplayNamesService names = DisplayNamesService.Create(["fr"], new DisplayNamesOptions { Type = "region" }, "en-US");
if (money != "1.234,50\u00A0\u20AC" || names.Of("US") != "\u00C9tats-Unis")
{
    Console.Error.WriteLine($"smoke: intl {money} {names.Of("US")}");
    return 1;
}

// Date-time and duration formatting: pattern matching over the embedded CLDR data, zone names, and the unit list.
DateTimeFormatService dtf = DateTimeFormatService.Create(["en-US"], new DateTimeFormatOptions { DateStyle = "full", TimeStyle = "long", TimeZone = "America/New_York" }, "en-US", "UTC", new FixedZone(-4 * 3600000, true));
string when = dtf.Format(1720105509123);
DateTimeFormatService range = DateTimeFormatService.Create(["de"], new DateTimeFormatOptions { Year = "numeric", Month = "long", Day = "numeric" }, "en-US", "UTC", new FixedZone(0, false));
string between = range.FormatRange(1720105509123, 1720105509123 + (3 * 86400000.0));
DurationFormatService dur = DurationFormatService.Create(["en"], new DurationFormatOptions { Style = "digital" }, "en-US");
string clock = dur.Format(DurationRecord.Create([null, null, null, null, 1, 2, 3, null, null, null]));
if (when != "Thursday, July 4, 2024 at 11:05:09 AM EDT" || between != "4.–7. Juli 2024" || clock != "1:02:03")
{
    Console.Error.WriteLine($"smoke: date-time {when} | {between} | {clock}");
    return 1;
}

// The interpreter runs scripts: closures, classes, generators, async functions, destructuring and error reports.
var runner = new ScriptEngine();
JsValue answer = runner.Evaluate(
    "class Counter { #n = 0; inc() { return ++this.#n; } }\n" +
    "function* gen() { yield 1; yield 2; }\n" +
    "const { a, ...rest } = { a: 40, b: 1, c: 1 };\n" +
    "let seen = 0; (async () => { await null; seen = 100; })();\n" +
    "const c = new Counter(); c.inc();\n" +
    "a + c.inc() + [...gen()].length - 2 + Object.keys(rest).length");
if (runner.ToDisplayString(answer) != "44" || runner.ToDisplayString(runner.Evaluate("seen")) != "100")
{
    Console.Error.WriteLine("smoke: evaluate gave " + runner.ToDisplayString(answer));
    return 1;
}

try
{
    runner.Evaluate("const o = {};\no.missing.x;", "smoke.js");
    Console.Error.WriteLine("smoke: expected a JsException");
    return 1;
}
catch (JsException e) when (e.Report is { Kind: "TypeError" } r && r.Position.Line == 2)
{
    Console.Out.WriteLine("aot-smoke ok " + ScriptEngine.Version);
    return 0;
}

/// <summary>A zone source with one fixed offset.</summary>
internal sealed class FixedZone(int offsetMs, bool daylight) : ITimeZoneSource
{
    public bool TryGetOffset(string zoneId, double epochMs, out TimeZoneOffset offset)
    {
        offset = new TimeZoneOffset(offsetMs, daylight);
        return true;
    }
}
