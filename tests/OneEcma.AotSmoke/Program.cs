using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Intl;
using RecursiveAI.OneEcma.TextServices;
using RecursiveAI.OneEcma.RegExp;

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
if (engine.Check("let a = 1; function f(x) { return x + a; }").Count != 0)
{
    Console.Error.WriteLine("smoke: valid script reported errors");
    return 1;
}

var errors = engine.Check("let a = ;\nlet ok = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];\nlet b = (1 + );");
string report = DiagnosticRenderer.RenderAll(errors);
if (errors.Count != 2 || !report.StartsWith("error[SyntaxError]: Unexpected token ';'", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected report\n" + report);
    return 1;
}

RepairResult repaired = engine.CheckAndRepair("const row = [1, 2, 3]];\nconsole.log(\u201chi\u201d);\n");
string repairReport = DiagnosticRenderer.RenderRepairResult(repaired);
if (!repaired.Parses || repaired.Repairs.Count != 2
    || repaired.RepairedSource != "const row = [1, 2, 3];\nconsole.log(\"hi\");\n"
    || !repairReport.StartsWith("repair[R001]: removed an extra closing delimiter", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected repair\n" + repairReport);
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

try
{
    engine.Evaluate("1 + 1");
    Console.Error.WriteLine("smoke: expected NotSupportedException");
    return 1;
}
catch (NotSupportedException)
{
    Console.Out.WriteLine("aot-smoke ok " + ScriptEngine.Version);
    return 0;
}
