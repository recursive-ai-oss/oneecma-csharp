using System.Text.Json;
using System.Text.RegularExpressions;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

public sealed partial class DiagnosticJsonTests
{
    [GeneratedRegex(@"^error\[SyntaxError\]: (.*)$", RegexOptions.Multiline)]
    private static partial Regex Headline();

    [GeneratedRegex(@"^ *--> case\.js:(\d+):(\d+)$", RegexOptions.Multiline)]
    private static partial Regex Arrow();

    private static string GoldenDir => Path.Combine(RepoPaths.Root, "testdata", "golden");

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
        Directory.GetDirectories(GoldenDir, "syn-*").Order(StringComparer.Ordinal).Select(d => new TheoryDataRow<string>(Path.GetFileName(d)));

    private static IReadOnlyList<Diagnostic> Check(string source) =>
        JsParser.Parse(source, new ParseOptions { FileName = "case.js" }, TestContext.Current.CancellationToken).Diagnostics;

    /// <summary>Line and column of an offset, counted here independently of the engine's line table.</summary>
    private static (int Line, int Column) Where(string text, int offset)
    {
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            if (c is '\n' or '\r' or '\u2028' or '\u2029')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExpectedJsonIsCommittedAndAgreesWithTheReport(string id)
    {
        string dir = Path.Combine(GoldenDir, id);
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        IReadOnlyList<Diagnostic> diagnostics = Check(source);
        string json = DiagnosticJson.Render(diagnostics, new JsonRenderOptions { Indented = true }) + "\n";
        string path = Path.Combine(dir, "expected.json");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, json);
        }

        Assert.Equal(File.ReadAllText(path), json);

        // Read it back as a consumer would and compare with the text report and with an independent line count.
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("oneecma.report", root.GetProperty("format").GetString());
        JsonElement list = root.GetProperty("diagnostics");
        Assert.Equal(diagnostics.Count, list.GetArrayLength());
        Assert.Equal(diagnostics.Count(d => d.Severity == Severity.Error), root.GetProperty("summary").GetProperty("errors").GetInt32());

        string expectedText = File.ReadAllText(Path.Combine(dir, "expected.txt"));
        string[] headlines = Headline().Matches(expectedText).Select(m => m.Groups[1].Value).ToArray();
        string[] arrows = Arrow().Matches(expectedText).Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value).ToArray();
        Assert.Equal(headlines.Length, list.GetArrayLength());

        int i = 0;
        foreach (JsonElement d in list.EnumerateArray())
        {
            Assert.Equal(headlines[i], d.GetProperty("headline").GetString());
            JsonElement span = d.GetProperty("span");
            int start = span.GetProperty("start").GetInt32();
            int end = span.GetProperty("end").GetInt32();
            (int line, int column) = Where(source, start);
            Assert.Equal(line, span.GetProperty("line").GetInt32());
            Assert.Equal(column, span.GetProperty("column").GetInt32());
            (line, column) = Where(source, end);
            Assert.Equal(line, span.GetProperty("endLine").GetInt32());
            Assert.Equal(column, span.GetProperty("endColumn").GetInt32());
            Assert.True(start <= end && end <= source.Length);
            if (!TextArrowDiffers.Contains(id))
            {
                Assert.Equal(arrows[i], span.GetProperty("line").GetInt32() + ":" + span.GetProperty("column").GetInt32());
            }

            Assert.Equal("SyntaxError", d.GetProperty("kind").GetString());
            Assert.Equal("error", d.GetProperty("severity").GetString());
            Assert.Equal("case.js", d.GetProperty("source").GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Array, d.GetProperty("secondary").ValueKind);
            Assert.Equal(JsonValueKind.Array, d.GetProperty("notes").ValueKind);
            Assert.Equal(JsonValueKind.Array, d.GetProperty("help").ValueKind);
            Assert.Equal(JsonValueKind.Array, d.GetProperty("fixes").ValueKind);
            foreach (JsonElement fix in d.GetProperty("fixes").EnumerateArray())
            {
                foreach (JsonElement edit in fix.GetProperty("edits").EnumerateArray())
                {
                    Assert.InRange(edit.GetProperty("start").GetInt32(), 0, source.Length);
                }
            }

            // Slots for later report features are absent, not null or empty.
            foreach (string later in new[] { "stack", "provenance", "didYouMean", "asyncStack", "mapping" })
            {
                Assert.False(d.TryGetProperty(later, out _), later);
            }

            i++;
        }
    }

    // Corpus expectations the renderer cannot reproduce exactly (see the syntax golden tests).
    private static readonly HashSet<string> TextArrowDiffers = ["syn-012-missing-comma-arguments", "syn-027-backtick-in-template"];

    [Fact]
    public void RepairsComeFirstWithTheRepairedSource()
    {
        string dir = Path.Combine(GoldenDir, "syn-001-extra-close-paren");
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        RepairResult result = SyntaxRepair.Repair(source, new ParseOptions { FileName = "case.js" }, cancellationToken: TestContext.Current.CancellationToken);
        using JsonDocument doc = JsonDocument.Parse(DiagnosticJson.Render(result));
        JsonElement root = doc.RootElement;
        Assert.Equal(result.Repairs.Count, root.GetProperty("summary").GetProperty("repairs").GetInt32());
        JsonElement first = root.GetProperty("repairs")[0];
        Assert.Equal("R001", first.GetProperty("id").GetString());
        Assert.Equal(result.Repairs[0].Title, first.GetProperty("title").GetString());
        Assert.Equal(result.Repairs[0].Edits.Count, first.GetProperty("edits").GetArrayLength());
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "repaired.js")), root.GetProperty("repairedSource").GetString());
        Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength());

        // The property order puts the repairs ahead of the diagnostics.
        string[] order = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.True(Array.IndexOf(order, "repairs") < Array.IndexOf(order, "diagnostics"));

        // Source that already parses has neither repairs nor a repaired source.
        using JsonDocument clean = JsonDocument.Parse(DiagnosticJson.Render(SyntaxRepair.Repair("let a = 1;", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(clean.RootElement.TryGetProperty("repairs", out _));
        Assert.False(clean.RootElement.TryGetProperty("repairedSource", out _));
    }

    [Fact]
    public void LoneSurrogatesAndControlCharactersStayValidJson()
    {
        IReadOnlyList<Diagnostic> diagnostics = Check("let s = '\ud800' + ;\u0001");
        string json = DiagnosticJson.Render(diagnostics);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
        Assert.Equal("{\"version\":1", json[..12]);
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void MappedDiagnosticsCarryTheMappingObject()
    {
        string dir = Path.Combine(GoldenDir, "map-001-syntax-error-in-generated-code");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
        JsonElement d = doc.RootElement.GetProperty("diagnostics")[0];
        Assert.Equal("original.ts", d.GetProperty("source").GetProperty("name").GetString());
        JsonElement m = d.GetProperty("mapping");
        Assert.True(m.GetProperty("spansAreOriginal").GetBoolean());
        Assert.Equal("case.js", m.GetProperty("generated").GetProperty("source").GetString());
        Assert.Equal(5, m.GetProperty("generated").GetProperty("line").GetInt32());
        Assert.Equal(34, m.GetProperty("generated").GetProperty("column").GetInt32());
        Assert.Equal(42, m.GetProperty("original").GetProperty("column").GetInt32());
        Assert.Equal(42, d.GetProperty("span").GetProperty("column").GetInt32());
    }

    [Fact]
    public void SchemaFileDescribesTheCurrentVersion()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "diagnostic-report.schema.json")));
        JsonElement root = schema.RootElement;
        Assert.Equal(DiagnosticJson.Version, root.GetProperty("properties").GetProperty("version").GetProperty("const").GetInt32());
        Assert.Equal(DiagnosticJson.Format, root.GetProperty("properties").GetProperty("format").GetProperty("const").GetString());
        string[] required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("diagnostics", required);
        // Every field the writer emits for a diagnostic is declared.
        JsonElement diag = root.GetProperty("$defs").GetProperty("diagnostic").GetProperty("properties");
        using JsonDocument sample = JsonDocument.Parse(DiagnosticJson.Render(Check("let a = ;")));
        foreach (JsonProperty p in sample.RootElement.GetProperty("diagnostics")[0].EnumerateObject())
        {
            Assert.True(diag.TryGetProperty(p.Name, out _), p.Name);
        }
    }
}
