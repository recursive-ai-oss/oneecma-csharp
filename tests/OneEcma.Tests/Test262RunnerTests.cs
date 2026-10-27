using RecursiveAI.OneEcma.Test262;

namespace RecursiveAI.OneEcma.Tests;

public class Test262RunnerTests
{
    private const string Positive = """
        // Copyright (C) 2020 Someone. All rights reserved.
        /*---
        esid: sec-foo
        description: >
          A multi-line
          description.
        flags: [onlyStrict, async]
        includes: [propertyHelper.js, compareArray.js]
        features:
          - Symbol
          - Temporal
        locale: [en-US, de]
        ---*/
        assert(true);
        """;

    private const string Negative = """
        /*---
        description: bad
        negative:
          phase: parse
          type: SyntaxError
        flags: [module]
        ---*/
        $DONOTEVALUATE();
        """;

    [Fact]
    public void ParsesInlineAndBlockLists()
    {
        TestMetadata m = TestMetadata.Parse(Positive);
        Assert.Equal(["onlyStrict", "async"], m.Flags);
        Assert.Equal(["propertyHelper.js", "compareArray.js"], m.Includes);
        Assert.Equal(["Symbol", "Temporal"], m.Features);
        Assert.Equal(["en-US", "de"], m.Locales);
        Assert.False(m.IsNegative);
    }

    [Fact]
    public void ParsesNegativeBlock()
    {
        TestMetadata m = TestMetadata.Parse(Negative);
        Assert.Equal("parse", m.NegativePhase);
        Assert.Equal("SyntaxError", m.NegativeType);
        Assert.True(m.HasFlag("module"));
    }

    [Fact]
    public void FileWithoutFrontmatterHasNoFlags()
    {
        TestMetadata m = TestMetadata.Parse("var x = 1;");
        Assert.Empty(m.Flags);
        Assert.False(m.IsNegative);
    }

    private static Scope LoadScope()
    {
        string path = Path.Combine(RepoPaths.Root, "tools", "test262", "scope.txt");
        return Scope.Load(path);
    }

    [Fact]
    public void ScopeSkipsLaterEditionFeaturesOnly()
    {
        Scope s = LoadScope();
        Assert.Equal("Temporal", s.FindOutOfScopeFeature(["Symbol", "Temporal"]));
        Assert.Null(s.FindOutOfScopeFeature(["upsert", "json-parse-with-source", "iterator-sequencing", "uint8array-base64", "Math.sumPrecise", "Error.isError", "Array.fromAsync", "IsHTMLDDA"]));
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("en-GB", true)]
    [InlineData("es-US", true)]
    [InlineData("pt-BR", true)]
    [InlineData("de", true)]
    [InlineData("en-US-u-ca-gregory", true)]
    [InlineData("ar", false)]
    [InlineData("es-ES", false)]
    [InlineData("zh-Hans-CN", false)]
    [InlineData("fr-CA", false)]
    public void ScopeLocales(string tag, bool supported)
    {
        Assert.Equal(supported, LoadScope().FindUnsupportedLocale([tag]) is null);
    }

    [Fact]
    public void OneUnsupportedLocaleSkipsTheTest()
    {
        Assert.Equal("ja", LoadScope().FindUnsupportedLocale(["en", "ja"]));
    }

    [Fact]
    public void DocumentedFailuresMatchExactPrefixAndGlob()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "# c\nbuilt-ins/Array/a.js | exact\nbuilt-ins/Proxy/ | whole dir\nlanguage/**/x-*.js | glob\n");
            DocumentedFailures d = DocumentedFailures.Load(file);
            Assert.Equal("exact", d.Find("built-ins/Array/a.js"));
            Assert.Null(d.Find("built-ins/Array/b.js"));
            Assert.Equal("whole dir", d.Find("built-ins/Proxy/get/x.js"));
            Assert.Equal("glob", d.Find("language/expressions/x-1.js"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DocumentedFailureNeedsAReason()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "built-ins/Array/a.js\n");
            Assert.Throws<InvalidDataException>(() => DocumentedFailures.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static PreparedTest Run(string source, SourceMode mode = SourceMode.Sloppy) => new()
    {
        RelativePath = "t.js",
        FullPath = "t.js",
        Meta = TestMetadata.Parse(source),
        Mode = mode,
        Harness = [],
        Source = source,
    };

    [Fact]
    public void NegativeTestNeedsMatchingPhaseAndType()
    {
        PreparedTest t = Run(Negative, SourceMode.Module);
        Assert.Null(Runner.Judge(t, new BackendResult(true, "parse", "SyntaxError")));
        Assert.NotNull(Runner.Judge(t, new BackendResult(true, "runtime", "SyntaxError")));
        Assert.NotNull(Runner.Judge(t, new BackendResult(true, "parse", "TypeError")));
        Assert.NotNull(Runner.Judge(t, BackendResult.Passed));
    }

    [Fact]
    public void PositiveTestFailsOnAnyThrow()
    {
        PreparedTest t = Run("/*---\ndescription: x\n---*/\n");
        Assert.Null(Runner.Judge(t, BackendResult.Passed));
        Assert.NotNull(Runner.Judge(t, new BackendResult(true, "runtime", "Test262Error")));
        Assert.NotNull(Runner.Judge(t, new BackendResult(false, NotImplemented: true)));
    }

    [Fact]
    public void PreparerRunsBothModesUnlessFlagged()
    {
        string suite = Path.Combine(Path.GetTempPath(), "oneecma-prep-" + Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(suite, "harness"));
        try
        {
            foreach (string h in new[] { "assert.js", "sta.js", "doneprintHandle.js", "x.js" })
            {
                File.WriteAllText(Path.Combine(suite, "harness", h), "// " + h);
            }

            var prep = new TestPreparer(suite);
            Assert.Equal([SourceMode.Sloppy, SourceMode.Strict], Modes(prep, "/*---\ndescription: d\n---*/"));
            Assert.Equal([SourceMode.Strict], Modes(prep, "/*---\nflags: [onlyStrict]\n---*/"));
            Assert.Equal([SourceMode.Sloppy], Modes(prep, "/*---\nflags: [noStrict]\n---*/"));
            Assert.Equal([SourceMode.Module], Modes(prep, "/*---\nflags: [module]\n---*/"));

            var asyncRun = prep.Prepare("a.js", "a.js", "/*---\nflags: [async]\nincludes: [x.js]\n---*/", TestMetadata.Parse("/*---\nflags: [async]\nincludes: [x.js]\n---*/"), false)[0];
            Assert.Equal(["assert.js", "sta.js", "doneprintHandle.js", "x.js"], asyncRun.Harness.Select(h => h.Name));

            var raw = prep.Prepare("r.js", "r.js", "/*---\nflags: [raw]\n---*/", TestMetadata.Parse("/*---\nflags: [raw]\n---*/"), false);
            Assert.Single(raw);
            Assert.Empty(raw[0].Harness);

            PreparedTest strict = prep.Prepare("s.js", "s.js", "x;", new TestMetadata(), false)[1];
            Assert.StartsWith("\"use strict\";\n", strict.EffectiveSource, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(suite, recursive: true);
        }
    }

    [Fact]
    public void ReportListsStaleDocumentedPathsInTextAndJson()
    {
        var report = new Report(
        [
            new RecursiveAI.OneEcma.Test262.TestResult("language/a/one.js", TestStatus.Pass),
            new RecursiveAI.OneEcma.Test262.TestResult("language/b/stale-two.js", TestStatus.Pass, StaleDocumented: true),
            new RecursiveAI.OneEcma.Test262.TestResult("built-ins/c/stale-one.js", TestStatus.Pass, StaleDocumented: true),
        ]);
        Assert.Equal(["language/b/stale-two.js", "built-ins/c/stale-one.js"], report.StalePaths);
        string text = report.ToText("engine", 1);
        Assert.Contains("documented failures that now pass: 2", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("stale: built-ins/c/stale-one.js", StringComparison.Ordinal) < text.IndexOf("stale: language/b/stale-two.js", StringComparison.Ordinal));
        using var stream = new MemoryStream();
        report.WriteJson(stream, "engine", "sha", false);
        using var doc = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        Assert.Equal(["built-ins/c/stale-one.js", "language/b/stale-two.js"], doc.RootElement.GetProperty("staleDocumentedPaths").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
    }

    private static SourceMode[] Modes(TestPreparer prep, string source) =>
        prep.Prepare("t.js", "t.js", source, TestMetadata.Parse(source), false).Select(r => r.Mode).ToArray();
}
