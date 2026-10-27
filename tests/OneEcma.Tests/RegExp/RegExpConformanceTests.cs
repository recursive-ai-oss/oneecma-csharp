using RecursiveAI.OneEcma.RegExpTest262;

namespace RecursiveAI.OneEcma.Tests.RegExp;

/// <summary>
/// The test262 translators and the recorded Node differential, run as tests. The test262 checkout comes from the
/// ONEECMA_TEST262_ROOT environment variable (the directory holding <c>test/</c> and <c>harness/</c>); without it
/// those tests are skipped. The differential needs no download: <c>testdata/regexp/expected.json</c> is committed.
/// </summary>
public class RegExpConformanceTests
{
    // These literals fail in the lexer, not in pattern validation: line terminators inside a literal, a '/' inside the
    // body that ends it early, and escaped flag letters. The pattern validator is never reached for them.
    private static readonly string[] LexerOwned =
    [
        "S7.8.5_A1.2_T1.js", "S7.8.5_A1.2_T3.js", "S7.8.5_A1.2_T4.js", "S7.8.5_A1.5_T1.js", "S7.8.5_A1.5_T3.js",
        "S7.8.5_A2.2_T2.js", "S7.8.5_A2.5_T1.js", "S7.8.5_A2.5_T3.js", "early-err-flags-unicode-escape.js",
        "regexp-first-char-no-line-separator.js", "regexp-first-char-no-paragraph-separator.js",
        "regexp-source-char-no-line-separator.js", "regexp-source-char-no-paragraph-separator.js",
    ];

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
            .Where(r => !r.Passed && !LexerOwned.Contains(Path.GetFileName(r.Path)))
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
