using RecursiveAI.OneEcma.RegExpTest262;

namespace RecursiveAI.OneEcma.Tests.RegExp;

/// <summary>
/// The test262 translators and the recorded Node differential, run as tests. The test262 checkout comes from the
/// ONEECMA_TEST262_ROOT environment variable (the directory holding <c>test/</c> and <c>harness/</c>); without it
/// those tests are skipped. The differential needs no download: <c>testdata/regexp/expected.json</c> is committed.
/// </summary>
public class RegExpConformanceTests
{
    private static string Test262Root()
    {
        string? root = Environment.GetEnvironmentVariable("ONEECMA_TEST262_ROOT");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(Path.Combine(root, "test")))
        {
            Assert.Skip("ONEECMA_TEST262_ROOT is not set to a test262 checkout");
        }

        return root!;
    }

    [Theory]
    [InlineData("built-ins/RegExp/property-escapes/generated")]
    [InlineData("built-ins/RegExp/unicodeSets/generated")]
    [InlineData("built-ins/RegExp/CharacterClassEscapes")]
    public void GeneratedMembershipTestsPass(string dir)
    {
        var failed = Harness.RunGenerated(Test262Root(), dir).Where(r => !r.Passed).Select(r => r.Path + ": " + r.Detail).ToList();
        Assert.Empty(failed);
    }

    [Theory]
    [InlineData("language/literals/regexp")]
    [InlineData("built-ins/RegExp/property-escapes")]
    [InlineData("built-ins/RegExp/named-groups")]
    [InlineData("built-ins/RegExp/lookBehind")]
    [InlineData("built-ins/RegExp/unicodeSets")]
    [InlineData("built-ins/RegExp/match-indices")]
    [InlineData("annexB/built-ins/RegExp")]
    public void LiteralValidationTestsPass(string dir)
    {
        var failed = Harness.RunLiterals(Test262Root(), dir)
            .Where(r => !r.Passed)
            .Select(r => r.Path + ": " + r.Detail)
            .ToList();
        Assert.Empty(failed);
    }

    [Fact]
    public void NodeDifferentialHasNoUndocumentedMismatch()
    {
        string file = Path.Combine(RepoPaths.Root, "testdata", "regexp", "expected.json");
        var results = Differential.Run(file);
        Assert.True(results.Count > 800);
        Assert.Empty(results.Where(r => !r.Passed).Select(r => $"/{r.Pattern}/{r.Flags}: {r.Detail}"));
    }
}
