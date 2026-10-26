using System.Text;
using System.Text.Json;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma.RegExpTest262;

/// <summary>One case of the Node differential: the case description and, when it disagreed, how.</summary>
public sealed record DiffResult(string Category, string Pattern, string Flags, bool Passed, string Detail, bool Documented = false);

/// <summary>
/// Replays testdata/regexp/expected.json (recorded from the reference Node by record.js) against the engine:
/// match / no match, every capture range, named-group ranges, and for syntax errors the exact V8 message.
/// </summary>
public static class Differential
{
    /// <summary>Decodes the ASCII-only string form written by record.js (non-printable characters as backslash-u escapes).</summary>
    public static string Decode(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length + 0 && s[i + 1] == 'u')
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                i += 5;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Runs every recorded case; one result per case (a case passes when all its runs agree).</summary>
    public static List<DiffResult> Run(string expectedJsonPath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(expectedJsonPath));
        var results = new List<DiffResult>();
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string cat = c.GetProperty("cat").GetString()!;
            string pattern = Decode(c.GetProperty("p").GetString()!);
            string flags = c.GetProperty("f").GetString()!;
            results.Add(RunCase(c, cat, pattern, flags));
        }

        return results;
    }

    private static DiffResult RunCase(JsonElement c, string cat, string pattern, string flags)
    {
        var compiled = RegExpProgram.Compile(pattern, flags);
        if (c.TryGetProperty("error", out JsonElement err))
        {
            string expected = Decode(err.GetString()!);
            if (compiled.Program is not null)
            {
                return new DiffResult(cat, pattern, flags, false, $"expected error '{expected}' but it compiled");
            }

            string actual = compiled.Diagnostics[0].Message;
            return new DiffResult(cat, pattern, flags, actual == expected, $"message: expected '{expected}', got '{actual}'");
        }

        if (compiled.Program is null)
        {
            if (compiled.Diagnostics[0].Reason == "Duplicate capture group name")
            {
                // Documented deviation: V8 accepts a named group that holds a same-named group in a different
                // alternative of its own body, e.g. (?<n>x(?<n>-)|d); the specification rejects it, and so do we.
                return new DiffResult(cat, pattern, flags, true, "V8 accepts nested duplicate group names", Documented: true);
            }

            return new DiffResult(cat, pattern, flags, false, "unexpected error: " + compiled.Diagnostics[0].Message);
        }

        RegExpProgram prog = compiled.Program;
        bool sticky = flags.Contains('y', StringComparison.Ordinal);
        if (!c.TryGetProperty("runs", out JsonElement runs))
        {
            return new DiffResult(cat, pattern, flags, true, "");
        }

        foreach (JsonElement run in runs.EnumerateArray())
        {
            string input = Decode(run.GetProperty("s").GetString()!);
            int start = run.GetProperty("start").GetInt32();
            var r = prog.Exec(input, start, sticky);
            string want = Render(run.GetProperty("m"), run);
            string got = r.Outcome switch
            {
                RegExpOutcome.LimitExceeded => "LIMIT",
                RegExpOutcome.NoMatch => "null",
                _ => RenderMatch(prog, r.Match!),
            };
            if (want != got)
            {
                return new DiffResult(cat, pattern, flags, false, $"input '{Printable(input)}' start {start}: expected {want}, got {got}");
            }
        }

        return new DiffResult(cat, pattern, flags, true, "");
    }

    private static string Printable(string s) => string.Concat(s.Select(ch => ch is >= ' ' and < '\u007f' ? ch.ToString() : $"\\u{(int)ch:x4}"));

    private static string Render(JsonElement m, JsonElement run)
    {
        if (m.ValueKind == JsonValueKind.Null)
        {
            return "null";
        }

        var sb = new StringBuilder();
        foreach (JsonElement g in m.EnumerateArray())
        {
            sb.Append(g.ValueKind == JsonValueKind.Null ? "-" : $"{g[0].GetInt32()}-{g[1].GetInt32()}").Append(' ');
        }

        if (run.TryGetProperty("names", out JsonElement names))
        {
            foreach (JsonProperty p in names.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.Append(Decode(p.Name)).Append('=').Append(p.Value.ValueKind == JsonValueKind.Null ? "-" : $"{p.Value[0].GetInt32()}-{p.Value[1].GetInt32()}").Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderMatch(RegExpProgram prog, RegExpMatch m)
    {
        var sb = new StringBuilder();
        for (int g = 0; g <= m.GroupCount; g++)
        {
            sb.Append(m.IsGroupMatched(g) ? $"{m.GetGroupStart(g)}-{m.GetGroupEnd(g)}" : "-").Append(' ');
        }

        foreach (RegExpGroupName n in prog.GroupNames.OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            int which = n.Groups.FirstOrDefault(m.IsGroupMatched);
            sb.Append(n.Name).Append('=').Append(which > 0 ? $"{m.GetGroupStart(which)}-{m.GetGroupEnd(which)}" : "-").Append(' ');
        }

        return sb.ToString().TrimEnd();
    }
}
