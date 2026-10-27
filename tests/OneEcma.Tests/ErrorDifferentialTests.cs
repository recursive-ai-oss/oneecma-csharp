using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Runs every snippet of testdata/errors/cases.txt and compares the error's <c>name</c>, <c>message</c> and <c>stack</c> with what
/// Node printed for it (testdata/errors/expected.json, recorded by tools/errors-diff/record.js). The two known differences are
/// listed with their reasons in testdata/errors/documented.txt; a documented case that starts to match fails the test, so the
/// list cannot go stale.
/// </summary>
public sealed class ErrorDifferentialTests
{
    private sealed record Observed(bool Threw, string? Name, string? Message, string? Stack, string? Primitive);

    private static List<(string Id, string Source)> LoadCases()
    {
        string text = File.ReadAllText(Path.Combine(RepoPaths.Root, "testdata", "errors", "cases.txt"));
        var list = new List<(string, string)>();
        string? id = null;
        var body = new StringBuilder();
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("//// ", StringComparison.Ordinal))
            {
                if (id is not null)
                {
                    list.Add((id, body.ToString().TrimEnd('\n') + "\n"));
                }

                id = line[5..].Trim();
                body.Clear();
            }
            else if (id is not null)
            {
                body.Append(line).Append('\n');
            }
        }

        if (id is not null)
        {
            list.Add((id, body.ToString().TrimEnd('\n') + "\n"));
        }

        return list;
    }

    private static Observed Run(string source)
    {
        var engine = new ScriptEngine(new ScriptEngineOptions
        {
            ThrowOnUnhandledRejection = false,
            Limits = new EngineLimits { Fuel = 400_000_000, MaxAllocatedBytes = 512L << 20 },
        });
        JsValue v;
        bool threw = false;
        try
        {
            engine.Evaluate(source, "case.js", TestContext.Current.CancellationToken);
            v = engine.GetProperty(engine.Global, "__e");
        }
        catch (JsException e)
        {
            threw = true;
            v = e.Value;
        }

        if (v.AsObject is { } o)
        {
            return new Observed(threw, Str(engine, o, "name"), Str(engine, o, "message"), Str(engine, o, "stack"), null);
        }

        return new Observed(threw, null, null, null, engine.ToDisplayString(v));
    }

    private static string Str(ScriptEngine engine, JsObject o, string key) => engine.ToDisplayString(engine.GetProperty(o, key));

    private static Dictionary<string, string> Documented()
    {
        string path = Path.Combine(RepoPaths.Root, "testdata", "errors", "documented.txt");
        var d = new Dictionary<string, string>();
        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int bar = line.IndexOf('|', StringComparison.Ordinal);
                d[line[..bar].Trim()] = line[(bar + 1)..].Trim();
            }
        }

        return d;
    }

    private static List<string> Differences(JsonElement expected, Observed got)
    {
        var diffs = new List<string>();
        if (expected.TryGetProperty("primitive", out JsonElement prim))
        {
            if (got.Primitive != prim.GetString())
            {
                diffs.Add($"primitive: node {prim.GetString()} / ours {got.Primitive ?? got.Message}");
            }

            return diffs;
        }

        if (got.Name is null)
        {
            diffs.Add("no error object: ours " + got.Primitive);
            return diffs;
        }

        foreach ((string key, string? ours) in new[] { ("name", got.Name), ("message", got.Message), ("stack", got.Stack) })
        {
            string want = expected.GetProperty(key).GetString()!;
            if (want != ours)
            {
                diffs.Add($"{key}:\n    node: {want.Replace("\n", "\n          ")}\n    ours: {ours?.Replace("\n", "\n          ")}");
            }
        }

        return diffs;
    }

    [Fact]
    public void NameMessageAndStackMatchNode()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "testdata", "errors", "expected.json")));
        JsonElement cases = doc.RootElement.GetProperty("cases");
        Dictionary<string, string> documented = Documented();
        var failures = new StringBuilder();
        int mismatches = 0;
        int total = 0;
        foreach ((string id, string source) in LoadCases())
        {
            total++;
            List<string> diffs = Differences(cases.GetProperty(id), Run(source));
            bool isDocumented = documented.ContainsKey(id);
            if (diffs.Count > 0 && !isDocumented)
            {
                mismatches++;
                failures.Append("== ").Append(id).Append('\n').Append(string.Join('\n', diffs.Select(x => "  " + x))).Append('\n');
            }
            else if (diffs.Count == 0 && isDocumented)
            {
                failures.Append("== ").Append(id).Append(": documented as different but now matches\n");
                mismatches++;
            }
        }

        string? dump = Environment.GetEnvironmentVariable("ONEECMA_ERRORS_DUMP");
        if (dump is not null)
        {
            File.WriteAllText(dump, failures.ToString());
        }

        Assert.True(mismatches == 0, $"{mismatches} of {total} snippets differ from Node:\n{failures}");
        Assert.True(total >= 150);
    }
}
