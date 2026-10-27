using System.Text.Json;
using RecursiveAI.OneEcma.SourceMaps;

namespace RecursiveAI.OneEcma.Tests.SourceMaps;

/// <summary>
/// Runs the ECMA-426 conformance data of tc39/source-map-tests (fetched by <c>tools/source-map-tests/fetch.sh</c>) against the decoder.
/// The directory holding <c>source-map-spec-tests.json</c> comes from ONEECMA_SOURCE_MAP_TESTS; without it these tests skip.
/// </summary>
public sealed class SourceMapSpecTests
{
    private static string? Root => Environment.GetEnvironmentVariable("ONEECMA_SOURCE_MAP_TESTS");

    public static IEnumerable<TheoryDataRow<string>> SpecCases()
    {
        if (Root is not { } root || !File.Exists(Path.Combine(root, "source-map-spec-tests.json")))
        {
            yield return new TheoryDataRow<string>("");
            yield break;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "source-map-spec-tests.json")));
        foreach (JsonElement t in doc.RootElement.GetProperty("tests").EnumerateArray())
        {
            yield return new TheoryDataRow<string>(t.GetProperty("name").GetString()!);
        }
    }

    private static SourceMap Load(string root, string file, out SourceMapParseResult result)
    {
        result = SourceMap.Parse(File.ReadAllText(Path.Combine(root, "resources", file)));
        return result.Map!;
    }

    [Theory]
    [MemberData(nameof(SpecCases))]
    public void SpecCase(string name)
    {
        if (Root is not { } root || name.Length == 0)
        {
            Assert.Skip("ONEECMA_SOURCE_MAP_TESTS is not set to a source-map-tests checkout");
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "source-map-spec-tests.json")));
        JsonElement test = doc.RootElement.GetProperty("tests").EnumerateArray().First(t => t.GetProperty("name").GetString() == name);
        bool valid = test.GetProperty("sourceMapIsValid").GetBoolean();
        SourceMap? map = Load(root, test.GetProperty("sourceMapFile").GetString()!, out SourceMapParseResult result);
        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            // A broken map is described, never thrown.
            Assert.NotEmpty(result.ToDiagnostics());
            return;
        }

        Assert.NotNull(map);
        if (!test.TryGetProperty("testActions", out JsonElement actions))
        {
            return;
        }

        foreach (JsonElement a in actions.EnumerateArray())
        {
            switch (a.GetProperty("actionType").GetString())
            {
                case "checkMapping":
                    CheckPosition(map, a, map.GetOriginalPositions(a.GetProperty("generatedLine").GetInt32(), a.GetProperty("generatedColumn").GetInt32()));
                    break;
                case "checkMappingTransitive":
                    CheckTransitive(root, map, a);
                    break;
                case "checkIgnoreList":
                    foreach (JsonElement present in a.GetProperty("present").EnumerateArray())
                    {
                        Assert.Contains(map.Sources, s => s.Ignored && s.Url == present.GetString());
                    }

                    break;
                default:
                    Assert.Fail("unknown action " + a.GetProperty("actionType").GetString());
                    break;
            }
        }
    }

    private static void CheckTransitive(string root, SourceMap first, JsonElement a)
    {
        IReadOnlyList<OriginalPosition> found = first.GetOriginalPositions(a.GetProperty("generatedLine").GetInt32(), a.GetProperty("generatedColumn").GetInt32());
        foreach (JsonElement inter in a.GetProperty("intermediateMaps").EnumerateArray())
        {
            SourceMap next = Load(root, inter.GetString()!, out SourceMapParseResult r);
            Assert.True(r.IsValid);
            Assert.NotEmpty(found);
            OriginalPosition p = found[0];
            found = next.GetOriginalPositions(p.Line, p.Column);
        }

        CheckPosition(first, a, found);
    }

    private static void CheckPosition(SourceMap map, JsonElement a, IReadOnlyList<OriginalPosition> found)
    {
        if (a.GetProperty("originalLine").ValueKind == JsonValueKind.Null)
        {
            Assert.Empty(found);
            return;
        }

        Assert.NotEmpty(found);
        string? expectedSource = a.GetProperty("originalSource").GetString();
        OriginalPosition hit = found.FirstOrDefault(f => f.Source.Url == expectedSource && f.Line == a.GetProperty("originalLine").GetInt32() && f.Column == a.GetProperty("originalColumn").GetInt32())
            ?? found[0];
        Assert.Equal(expectedSource, hit.Source.Url);
        Assert.Equal(a.GetProperty("originalLine").GetInt32(), hit.Line);
        Assert.Equal(a.GetProperty("originalColumn").GetInt32(), hit.Column);
        JsonElement name = a.GetProperty("mappedName");
        Assert.Equal(name.ValueKind == JsonValueKind.Null ? null : name.GetString(), hit.Name);
        _ = map;
    }

    public static IEnumerable<TheoryDataRow<string>> DecodingCases()
    {
        if (Root is not { } root || !Directory.Exists(Path.Combine(root, "decoding")))
        {
            yield return new TheoryDataRow<string>("");
            yield break;
        }

        foreach (string f in Directory.GetFiles(Path.Combine(root, "decoding"), "*.map", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(Path.GetRelativePath(Path.Combine(root, "decoding"), f));
        }
    }

    /// <summary>The decoded records must equal the golden JSON (the fields of ECMA-426's Decoded Source Map Record).</summary>
    [Theory]
    [MemberData(nameof(DecodingCases))]
    public void DecodingGolden(string relative)
    {
        if (Root is not { } root || relative.Length == 0)
        {
            Assert.Skip("ONEECMA_SOURCE_MAP_TESTS is not set to a source-map-tests checkout");
            return;
        }

        string path = Path.Combine(root, "decoding", relative);
        SourceMapParseResult result = SourceMap.Parse(File.ReadAllText(path));
        // An .error marker means the spec lets a consumer report something. The only ones in the data are for
        // proposals this decoder does not implement (debug ids), so they only relax the check.
        if (!File.Exists(path + ".error"))
        {
            Assert.True(result.IsValid, string.Join("; ", result.Problems.Select(p => p.Message)));
        }

        SourceMap map = result.Map!;
        using JsonDocument golden = JsonDocument.Parse(File.ReadAllText(path + ".golden"));
        JsonElement g = golden.RootElement;

        JsonElement sources = g.GetProperty("sources");
        Assert.Equal(sources.GetArrayLength(), map.Sources.Count);
        int i = 0;
        foreach (JsonElement s in sources.EnumerateArray())
        {
            SourceMapSource mine = map.Sources[i++];
            Assert.Equal(Str(s.GetProperty("url")), mine.Url);
            Assert.Equal(Str(s.GetProperty("content")), mine.Content);
            Assert.Equal(s.GetProperty("ignored").GetBoolean(), mine.Ignored);
        }

        JsonElement mappings = g.GetProperty("mappings");
        Assert.Equal(mappings.GetArrayLength(), map.MappingCount);
        i = 0;
        foreach (JsonElement m in mappings.EnumerateArray())
        {
            DecodedMapping mine = map.GetMapping(i++);
            JsonElement gp = m.GetProperty("generatedPosition");
            Assert.Equal(gp.GetProperty("line").GetInt32(), mine.GeneratedLine);
            Assert.Equal(gp.GetProperty("column").GetInt32(), mine.GeneratedColumn);
            JsonElement op = m.GetProperty("originalPosition");
            if (op.ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(-1, mine.SourceIndex);
            }
            else
            {
                Assert.Equal(op.GetProperty("sourceIndex").GetInt32(), mine.SourceIndex);
                Assert.Equal(op.GetProperty("line").GetInt32(), mine.OriginalLine);
                Assert.Equal(op.GetProperty("column").GetInt32(), mine.OriginalColumn);
            }

            string? name = Str(m.GetProperty("name"));
            Assert.Equal(name, mine.NameIndex >= 0 ? map.Names[mine.NameIndex] : null);
        }
    }

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();
}
