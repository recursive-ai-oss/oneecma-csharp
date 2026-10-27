using System.Text.RegularExpressions;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Runs every syntax case of the error golden corpus through the parser. The headline (V8's message),
/// the <c>--&gt;</c> location and the number of errors must match. Labels, help and notes are not compared
/// yet; the repair notes belong to the auto-repair pass.
/// </summary>
public sealed partial class GoldenSyntaxTests
{
    [GeneratedRegex(@"^error\[SyntaxError\]: (.*)$", RegexOptions.Multiline)]
    private static partial Regex Headline();

    [GeneratedRegex(@"^ *--> case\.js:(\d+):(\d+)$", RegexOptions.Multiline)]
    private static partial Regex Arrow();

    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden");
        foreach (string d in Directory.GetDirectories(dir, "syn-*").Order(StringComparer.Ordinal))
        {
            if (File.ReadAllText(Path.Combine(d, "meta.json")).Contains("\"kind\": \"syntax\"", StringComparison.Ordinal))
            {
                yield return new TheoryDataRow<string>(Path.GetFileName(d));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HeadlineLocationAndCountMatchTheCorpus(string id)
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden", id);
        string source = File.ReadAllText(Path.Combine(dir, "case.js"));
        string expected = File.ReadAllText(Path.Combine(dir, "expected.txt"));

        var result = JsParser.Parse(source, new ParseOptions { FileName = "case.js" }, TestContext.Current.CancellationToken);
        string actual = DiagnosticRenderer.RenderAll(result.Diagnostics);

        Assert.Equal(Headline().Matches(expected).Select(m => m.Groups[1].Value), Headline().Matches(actual).Select(m => m.Groups[1].Value));
        Assert.Equal(Arrow().Matches(expected).Select(m => m.Value.Trim()), Arrow().Matches(actual).Select(m => m.Value.Trim()));
    }
}
