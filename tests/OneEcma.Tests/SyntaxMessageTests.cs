using System.Text.Json;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Compares the first syntax error of each snippet in <c>testdata/syntax-messages/cases.json</c> with what Node
/// printed for it (v22.22.1 and v24.22.1 agree on every snippet). Pattern-body errors of regular expression
/// literals are the RegExp validator's, not the parser's, and are skipped here.
/// </summary>
public class SyntaxMessageTests
{
    public static IEnumerable<TheoryDataRow<string, string?, int>> Cases()
    {
        string path = Path.Combine(RepoPaths.Root, "testdata", "syntax-messages", "cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            string? message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (message is not null && message.StartsWith("Invalid regular expression: /", StringComparison.Ordinal))
            {
                continue;
            }

            int line = e.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
            yield return new TheoryDataRow<string, string?, int>(e.GetProperty("src").GetString()!, message, line);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FirstErrorMatchesNode(string src, string? message, int line)
    {
        var r = JsParser.Parse(src, null, TestContext.Current.CancellationToken);
        if (message is null)
        {
            Assert.False(r.HasErrors, src);
            return;
        }

        Assert.True(r.HasErrors, "expected an error for: " + src);
        Assert.Equal(message, r.Diagnostics[0].Headline);
        Assert.Equal(line, r.Diagnostics[0].Position.Line);
    }
}
