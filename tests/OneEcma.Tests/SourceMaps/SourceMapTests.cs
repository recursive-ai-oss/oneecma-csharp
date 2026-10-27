using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.SourceMaps;
using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Tests.SourceMaps;

public sealed class SourceMapTests
{
    private const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Encodes one signed value as a base64 VLQ (ECMA-426 section 6).</summary>
    internal static string Vlq(int value)
    {
        long u = value < 0 ? ((long)-value << 1) | 1 : (long)value << 1;
        var sb = new StringBuilder();
        do
        {
            int digit = (int)(u & 31);
            u >>= 5;
            sb.Append(B64[u > 0 ? digit | 32 : digit]);
        }
        while (u > 0);
        return sb.ToString();
    }

    /// <summary>Builds a mappings string from (generated line, generated column, source, original line, original column, name) tuples; -1 omits a field.</summary>
    internal static string Encode(params (int Line, int Col, int Src, int OLine, int OCol, int Name)[] segs)
    {
        var sb = new StringBuilder();
        int line = 0, col = 0, src = 0, ol = 0, oc = 0, name = 0;
        bool first = true;
        foreach (var g in segs)
        {
            while (line < g.Line)
            {
                sb.Append(';');
                line++;
                col = 0;
                first = true;
            }

            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(Vlq(g.Col - col));
            col = g.Col;
            if (g.Src >= 0)
            {
                sb.Append(Vlq(g.Src - src)).Append(Vlq(g.OLine - ol)).Append(Vlq(g.OCol - oc));
                (src, ol, oc) = (g.Src, g.OLine, g.OCol);
                if (g.Name >= 0)
                {
                    sb.Append(Vlq(g.Name - name));
                    name = g.Name;
                }
            }
        }

        return sb.ToString();
    }

    private static string MapJson(string mappings, string sources = "\"a.ts\"", string names = "", string extra = "") =>
        "{\"version\":3,\"sources\":[" + sources + "],\"names\":[" + names + "],\"mappings\":\"" + mappings + "\"" + extra + "}";

    [Fact]
    public void VlqExamplesFromTheSpec()
    {
        Assert.Equal("iB", Vlq(17));
        Assert.Equal("V", Vlq(-10));
        Assert.Equal("A", Vlq(0));
    }

    [Fact]
    public void LookupTakesTheLastMappingAtOrBeforeThePosition()
    {
        // line 0: col 0 -> a.ts 0:0, col 10 -> a.ts 2:4 (name n); line 2: col 3 -> a.ts 5:1
        string mappings = "AAAA,UAEI" + "A" + ";;" + "GAKC";
        SourceMapParseResult r = SourceMap.Parse(MapJson(mappings, names: "\"n\""));
        Assert.True(r.IsValid, string.Join(";", r.Problems.Select(p => p.Message)));
        SourceMap map = r.Map!;
        Assert.Equal(3, map.MappingCount);
        Assert.True(map.TryGetOriginalPosition(0, 0, out OriginalPosition p0));
        Assert.Equal((0, 0), (p0.Line, p0.Column));
        Assert.True(map.TryGetOriginalPosition(0, 9, out p0));
        Assert.Equal((0, 0), (p0.Line, p0.Column));
        Assert.True(map.TryGetOriginalPosition(0, 10, out OriginalPosition p1));
        Assert.Equal((2, 4), (p1.Line, p1.Column));
        // The spec's rule is "last mapping before", even across lines: line 1 has none, so line 0's last applies.
        Assert.True(map.TryGetOriginalPosition(1, 50, out p1));
        Assert.Equal((2, 4), (p1.Line, p1.Column));
        Assert.True(map.TryGetOriginalPosition(2, 100, out OriginalPosition p2));
        Assert.Equal(2, p2.GeneratedLine);
        Assert.False(map.TryGetOriginalPosition(-1, 0, out _));
        Assert.Empty(SourceMap.Parse(MapJson("")).Map!.GetOriginalPositions(0, 0));
    }

    [Fact]
    public void UnmappedSegmentEndsTheRangeAndEveryMappingAtOnePositionContributes()
    {
        // Two mappings at 0:0 (both originals returned), then a one-field segment at column 5 that maps nothing.
        SourceMap map = SourceMap.Parse(MapJson("AAAA,AACA,KAAA,K")).Map!;
        Assert.Equal(2, map.GetOriginalPositions(0, 0).Count);
        Assert.Empty(map.GetOriginalPositions(0, 40));
    }

    [Fact]
    public void IndexMapOffsetsSectionsAndMergesSources()
    {
        string inner(string mappings) => MapJson(mappings, "\"shared.ts\"");
        string json = "{\"version\":3,\"sections\":[" +
            "{\"offset\":{\"line\":0,\"column\":0},\"map\":" + inner("AAAA") + "}," +
            "{\"offset\":{\"line\":3,\"column\":8},\"map\":" + inner("AAAA;AACA") + "}]}";
        SourceMapParseResult r = SourceMap.Parse(json);
        Assert.True(r.IsValid, string.Join(";", r.Problems.Select(p => p.Message)));
        SourceMap map = r.Map!;
        Assert.Single(map.Sources);
        Assert.Equal(3, map.MappingCount);
        Assert.True(map.TryGetOriginalPosition(3, 8, out OriginalPosition p));
        Assert.Equal((0, 0), (p.Line, p.Column));
        // The offset column applies only to the section's first line.
        Assert.True(map.TryGetOriginalPosition(4, 0, out p));
        Assert.Equal((1, 0), (p.Line, p.Column));
    }

    [Fact]
    public void SourceRootIgnoreListAndContentsAreDecoded()
    {
        string json = "{\"version\":3,\"sourceRoot\":\"src\",\"file\":\"out.js\",\"sources\":[\"a.ts\",null,\"lib/b.ts\"],\"sourcesContent\":[\"x\",null,\"y\"],\"ignoreList\":[2],\"names\":[],\"mappings\":\"AAAA\",\"x_google_ignoreList\":[0]}";
        SourceMap map = SourceMap.Parse(json, "/proj/dist/out.js.map").Map!;
        Assert.Equal("out.js", map.File);
        Assert.Equal("/proj/dist/src/a.ts", map.Sources[0].Url);
        Assert.Null(map.Sources[1].Url);
        Assert.Equal("/proj/dist/src/lib/b.ts", map.Sources[2].Url);
        Assert.True(map.Sources[2].Ignored);
        Assert.False(map.Sources[0].Ignored);
        Assert.Equal("y", map.Sources[2].Content);
    }

    [Theory]
    [InlineData("", "a.ts", "a.ts")]
    [InlineData(null, "a.ts", "a.ts")]
    [InlineData("/p/dist/out.js.map", "../src/a.ts", "/p/src/a.ts")]
    [InlineData("dist/out.js.map", "../src/a.ts", "src/a.ts")]
    [InlineData("out.js.map", "a.ts", "a.ts")]
    [InlineData("https://h.example/js/app.js.map", "../a.ts", "https://h.example/a.ts")]
    [InlineData("https://h.example/js/app.js.map", "/a.ts", "https://h.example/a.ts")]
    [InlineData("https://h.example/js/app.js.map", "https://o.example/a.ts", "https://o.example/a.ts")]
    [InlineData("C:/p/out.js.map", "a.ts", "C:/p/a.ts")]
    public void ResolveFoldsRelativeReferences(string? baseUrl, string reference, string expected) =>
        Assert.Equal(expected, SourceMapUrl.Resolve(baseUrl, reference));

    [Theory]
    [InlineData("var a;\n//# sourceMappingURL=a.js.map\n", "a.js.map")]
    [InlineData("var a;\n//# sourceMappingURL=a.js.map", "a.js.map")]
    [InlineData("var a;\r\n//@ sourceMappingURL=a.js.map  \r\n\r\n", "a.js.map")]
    [InlineData("var a;\n//# sourceMappingURL=a.js.map\n// trailing comment\n", "a.js.map")]
    [InlineData("var a;\n//# sourceMappingURL=a.js.map\nvar b;\n", null)]
    [InlineData("var a; //# sourceMappingURL=a.js.map\n", null)]
    [InlineData("var s = `\n//# sourceMappingURL=a.js.map\n// `", null)]
    [InlineData("//# sourceMappingURL=a'b.map\n", null)]
    [InlineData("//# sourceMappingURL=a b.map\n", null)]
    [InlineData("var a;\n/*# sourceMappingURL=a.js.map */\n", null)]
    [InlineData("", null)]
    public void ExtractFindsTheTrailingComment(string source, string? expected) => Assert.Equal(expected, SourceMapUrl.Extract(source));

    [Fact]
    public void DataUrlsDecodeBase64AndPercentEncodedJson()
    {
        string json = MapJson("AAAA");
        string b64 = "data:application/json;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.True(SourceMapUrl.TryDecodeDataUrl(b64, out string text));
        Assert.Equal(json, text);
        Assert.True(SourceMapUrl.TryDecodeDataUrl("data:application/json," + Uri.EscapeDataString(json), out text));
        Assert.Equal(json, text);
        Assert.False(SourceMapUrl.TryDecodeDataUrl("data:application/json;base64,@@@", out _));
        Assert.False(SourceMapUrl.TryDecodeDataUrl("data:no-comma", out _));
        Assert.False(SourceMapUrl.TryDecodeDataUrl("http://x/y.map", out _));
        Assert.False(SourceMapUrl.TryDecodeDataUrl(b64, out _, maxLength: 5));
    }

    [Fact]
    public void LoaderFollowsInlineMapsAndHostFetchedMaps()
    {
        string json = MapJson("AAAA");
        string inline = "x\n//# sourceMappingURL=data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\n";
        SourceMapParseResult? r = SourceMapLoader.Load(new SourceText(inline, "out.js"));
        Assert.NotNull(r?.Map);

        var fetched = new List<string>();
        r = SourceMapLoader.Load(new SourceText("x\n//# sourceMappingURL=out.js.map\n", "dist/out.js"), u =>
        {
            fetched.Add(u);
            return ")]}'\n" + json;
        });
        Assert.Equal(["dist/out.js.map"], fetched);
        Assert.Equal("dist/a.ts", r!.Map!.Sources[0].Url);

        // No fetcher: only data: URLs are followed, nothing is read on the script's behalf.
        Assert.Null(SourceMapLoader.Load(new SourceText("x\n//# sourceMappingURL=out.js.map\n", "out.js")));
        Assert.Null(SourceMapLoader.Load(new SourceText("no comment", "out.js")));
        SourceMapParseResult? bad = SourceMapLoader.Load(new SourceText("//# sourceMappingURL=data:application/json;base64,@@\n", "out.js"));
        Assert.Null(bad!.Map);
        Assert.Equal("source-map-data-url", Assert.Single(bad.Problems).Code);
    }

    public static IEnumerable<TheoryDataRow<string, string>> Malformed() =>
    [
        new("", "source-map-invalid-json"),
        new("[]", "source-map-not-object"),
        new("{", "source-map-invalid-json"),
        new("{\"version\":3}", "source-map-mappings-type"),
        new("{\"version\":3,\"mappings\":1,\"sources\":[]}", "source-map-mappings-type"),
        new("{\"version\":3,\"mappings\":\"\"}", "source-map-sources-type"),
        new("{\"version\":2,\"mappings\":\"\",\"sources\":[]}", "source-map-version"),
        new(MapJson("A!"), "source-map-invalid-mappings"),
        new(MapJson("AA"), "source-map-invalid-mappings"),
        new(MapJson("AAAAAA,"), "source-map-invalid-mappings"),
        new(MapJson("AAAAAA"), "source-map-invalid-mappings"),
        new(MapJson("g"), "source-map-invalid-mappings"),
        new(MapJson("AAAA,,AAAA"), "source-map-invalid-mappings"),
        new(MapJson("+++++++++"), "source-map-invalid-mappings"),
        new(MapJson("ACAA"), "source-map-position-range"),
        new(MapJson("AAAA", extra: ",\"sourcesContent\":5"), "source-map-field-type"),
        new("{\"version\":3,\"sections\":5}", "source-map-sections-type"),
        new("{\"version\":3,\"sections\":[{\"map\":{}}]}", "source-map-section-offset"),
        new("{\"version\":3,\"sections\":[{\"offset\":{\"line\":0,\"column\":0}}]}", "source-map-section-map"),
    ];

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedMapsAreReportedAsWarnings(string json, string code)
    {
        SourceMapParseResult r = SourceMap.Parse(json, "bad.map");
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Code == code);
        IReadOnlyList<Diagnostic> diagnostics = r.ToDiagnostics();
        Assert.Equal(r.Problems.Count, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(Severity.Warning, d.Severity);
            Assert.Equal("SourceMap", d.Kind);
            Assert.Contains("bad.map", DiagnosticRenderer.Render(d), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MappingsBeforeAGrammarErrorAreKept()
    {
        SourceMapParseResult r = SourceMap.Parse(MapJson("AAAA,CAAC;AAAA,!"));
        Assert.False(r.IsValid);
        Assert.Equal(3, r.Map!.MappingCount);
    }

    [Fact]
    public void LimitsBoundSizeDepthMappingsAndSections()
    {
        string tooDeep = new string('[', 200) + new string(']', 200);
        Assert.Contains(SourceMap.Parse(tooDeep).Problems, p => p.Fatal);

        SourceMapParseResult r = SourceMap.Parse(MapJson("AAAA"), limits: new SourceMapLimits { MaxJsonLength = 10 });
        Assert.Null(r.Map);
        Assert.Equal("source-map-too-large", r.Problems[0].Code);

        r = SourceMap.Parse(MapJson("AAAA,CAAC,CAAC,CAAC"), limits: new SourceMapLimits { MaxMappings = 2 });
        Assert.Equal(2, r.Map!.MappingCount);
        Assert.Contains(r.Problems, p => p.Code == "source-map-too-many-mappings");

        string sections = "{\"version\":3,\"sections\":[" + string.Join(",", Enumerable.Range(0, 5).Select(i => "{\"offset\":{\"line\":" + i + ",\"column\":0},\"map\":" + MapJson("AAAA") + "}")) + "]}";
        r = SourceMap.Parse(sections, limits: new SourceMapLimits { MaxSections = 3 });
        Assert.Null(r.Map);

        // Millions of empty lines cost only the characters of the input.
        r = SourceMap.Parse(MapJson(new string(';', 3_000_000)));
        Assert.True(r.IsValid);

        r = SourceMap.Parse(MapJson("AAAA"), limits: new SourceMapLimits { MaxProblems = 1 });
        Assert.True(r.IsValid);
    }

    /// <summary>Regression from fuzzing: a JSON escape for half a surrogate pair made the string accessor throw.</summary>
    [Fact]
    public void LoneSurrogateEscapesInStringsAreKept()
    {
        SourceMapParseResult r = SourceMap.Parse("{\"version\":3,\"sources\":[\"a\\ud800\\n\"],\"sourcesContent\":[\"\\udc00x\"],\"mappings\":\"AAAA\"}");
        Assert.True(r.IsValid);
        Assert.Equal("a\ud800\n", r.Map!.Sources[0].Url);
        Assert.Equal("\udc00x", r.Map.Sources[0].Content);
    }

    [Fact]
    public void LoneSurrogateInAPropertyNameIsAFatalProblem()
    {
        SourceMapParseResult r = SourceMap.Parse("{\"version\":3,\"\\ud800\":1,\"sources\":[],\"mappings\":\"\"}");
        Assert.Contains(r.Problems, p => p.Code == "source-map-invalid-json");
    }

    [Fact]
    public void ProblemListIsBounded()
    {
        string json = "{\"version\":3,\"sources\":[" + string.Join(",", Enumerable.Repeat("5", 10000)) + "],\"mappings\":\"\"}";
        SourceMapParseResult r = SourceMap.Parse(json);
        Assert.Equal(100, r.Problems.Count);
    }

    [Fact]
    public void RemapMovesSpansToTheOriginalSourceAndKeepsTheGeneratedPosition()
    {
        // Generated line 2 is "x.f(1,2;" (missing ")"); the original is "  foo.bar( 1, 2;" on line 5. `x` stands for the
        // renamed `foo`, so that segment carries a name; the others are plain.
        const string generatedText = "q(a);\nx.f(1,2;\n";
        string orig = "\n\n\n\n  foo.bar( 1, 2;\n";
        string mappings = Encode((1, 0, 0, 4, 2, 0), (1, 2, 0, 4, 6, -1), (1, 3, 0, 4, 9, -1), (1, 4, 0, 4, 11, -1), (1, 6, 0, 4, 14, -1), (1, 7, 0, 4, 15, -1));
        SourceMap map = SourceMap.Parse(MapJson(mappings, "\"a.ts\"", "\"foo\"", ",\"sourcesContent\":[" + System.Text.Json.JsonSerializer.Serialize(orig) + "]")).Map!;
        var gen = new SourceText(generatedText, "out.js");
        var d = new Diagnostic("SyntaxError", "x", "missing ) after argument list", gen, new Span(generatedText.IndexOf(';', 6), generatedText.IndexOf(';', 6) + 1));
        d.WithLabel(new Span(6, 7), "the callee");
        d.WithLabel(new Span(0, 1), "elsewhere");
        d.Fixes.Add(new SuggestedFix("add )", [new TextEdit(12, 0, ")")]));

        Diagnostic m = SourceMapReports.Remap(d, map);
        Assert.Equal("a.ts", m.Source.Name);
        Assert.NotNull(m.Mapping);
        Assert.True(m.Mapping.SpansAreOriginal);
        Assert.Equal(new MappedLocation("out.js", 2, 8, 2, 9), m.Mapping.Generated);
        Assert.Equal(5, m.Position.Line);
        Assert.Empty(m.Fixes);
        string text = DiagnosticRenderer.Render(m);
        Assert.Contains("--> a.ts:5:16", text, StringComparison.Ordinal);
        Assert.Contains("the callee", text, StringComparison.Ordinal);
        Assert.Equal(3, m.SecondaryLabels.Single(l => l.Message == "the callee").Span.Length);
        Assert.Contains("foo.bar( 1, 2;", text, StringComparison.Ordinal);
        Assert.Contains("generated position: out.js:2:8", text, StringComparison.Ordinal);
        // The label on unmapped code (line 0 has no mapping before it... it maps to nothing) becomes a note.
        Assert.Contains("elsewhere (generated code at out.js:1:1)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemapWithoutSourceTextKeepsTheGeneratedSourceAndAddsTheOriginalPosition()
    {
        SourceMap map = SourceMap.Parse(MapJson("AAAA")).Map!;
        var gen = new SourceText("let a = ;\n", "out.js");
        var d = new Diagnostic("SyntaxError", "x", "Unexpected token ';'", gen, new Span(8, 9));
        Diagnostic m = SourceMapReports.Remap(d, map);
        Assert.Same(gen, m.Source);
        Assert.False(m.Mapping!.SpansAreOriginal);
        Assert.Contains("original position: a.ts:1:9 (the original source text is not available)", m.Notes);

        // A host can supply the text.
        Diagnostic withText = SourceMapReports.Remap(d, map, url => url == "a.ts" ? "let a = ;\n" : null);
        Assert.Equal("a.ts", withText.Source.Name);
    }

    [Fact]
    public void RemapLeavesUnmappedDiagnosticsAlone()
    {
        SourceMap map = SourceMap.Parse(MapJson("")).Map!;
        var d = new Diagnostic("SyntaxError", "x", "h", new SourceText("a b", "out.js"), new Span(0, 1));
        Assert.Same(d, SourceMapReports.Remap(d, map));
        Assert.Null(d.Mapping);
    }

    [Fact]
    public void GoldenCaseReportsTheGeneratedSyntaxErrorAtTheOriginalLocation()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden", "map-001-syntax-error-in-generated-code");
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        var generated = new SourceText(source, "case.js");
        IReadOnlyList<Diagnostic> diagnostics = JsParser.Parse(source, new ParseOptions { FileName = "case.js" }, TestContext.Current.CancellationToken).Diagnostics;
        SourceMapParseResult? loaded = SourceMapLoader.Load(generated, u => File.ReadAllText(Path.Combine(dir, u)));
        Assert.True(loaded!.IsValid);
        IReadOnlyList<Diagnostic> remapped = SourceMapReports.RemapAll(diagnostics, loaded.Map!);
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "expected.txt")), DiagnosticRenderer.RenderAll(remapped));
        string json = DiagnosticJson.Render(remapped, new JsonRenderOptions { Indented = true }) + "\n";
        string expectedPath = Path.Combine(dir, "expected.json");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(expectedPath, json);
        }

        Assert.Equal(File.ReadAllText(expectedPath), json);
    }
}
