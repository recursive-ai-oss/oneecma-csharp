using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Runs the scripts in testdata/builtins and compares their output with what the reference engine printed.</summary>
public class BuiltinsDifferentialTests
{
    public static IEnumerable<TheoryDataRow<string>> Scripts()
    {
        foreach (string f in Directory.GetFiles(Path.Combine(RepoPaths.Root, "testdata", "builtins"), "*.js").Order(StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(Path.GetFileNameWithoutExtension(f));
        }
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void OutputMatchesTheRecordedReferenceOutput(string name)
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "builtins");
        string source = File.ReadAllText(Path.Combine(dir, name + ".js"));
        string expected = File.ReadAllText(Path.Combine(dir, name + ".expected"));
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = 2_000_000_000, MaxAllocatedBytes = 1L << 30 } });
        var output = new StringBuilder();
        engine.DefineGlobalFunction("print", 1, (_, a) =>
        {
            for (int i = 0; i < a.Length; i++)
            {
                output.Append(i > 0 ? " " : string.Empty).Append(engine.ToDisplayString(a[i]));
            }

            output.Append('\n');
            return JsValue.Undefined;
        });
        engine.Evaluate("globalThis.console = { log: print };", cancellationToken: TestContext.Current.CancellationToken);
        engine.Evaluate(source, name + ".js", TestContext.Current.CancellationToken);
        string[] want = expected.Split('\n');
        // The recorded output went through UTF-8, which turns a lone surrogate into U+FFFD.
        string[] got = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(output.ToString())).Split('\n');
        for (int i = 0; i < Math.Min(want.Length, got.Length); i++)
        {
            Assert.True(want[i] == got[i], $"{name}.js output line {i + 1}:\n  reference: {Trunc(want[i])}\n  ours:      {Trunc(got[i])}");
        }

        Assert.Equal(want.Length, got.Length);
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
