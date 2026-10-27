using RecursiveAI.OneEcma.Cli;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>The command-line front end: flags of <c>oneecma run</c>.</summary>
[Collection("Console")]
public sealed class CliTests
{
    private static (int Exit, string Err) RunCli(params string[] args)
    {
        TextWriter saved = Console.Error;
        var err = new StringWriter();
        Console.SetError(err);
        try
        {
            return (Program.Main(args), err.ToString().ReplaceLineEndings("\n"));
        }
        finally
        {
            Console.SetError(saved);
        }
    }

    [Fact]
    public void RunLocalsAddsTheBindingsOfEachFrame()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneecma-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "job.js");
            File.WriteAllText(file, "function total(order) {\n  const rate = 3;\n  return order.lines.length * rate;\n}\ntotal({ id: 7 });\n");
            (int plainExit, string plain) = RunCli("run", file);
            (int exit, string withLocals) = RunCli("run", "--locals", file);
            Assert.Equal(1, plainExit);
            Assert.Equal(1, exit);
            Assert.DoesNotContain("= locals in", plain, StringComparison.Ordinal);
            Assert.Contains("= locals in total: order = { id }, rate = 3", withLocals, StringComparison.Ordinal);
            Assert.StartsWith(plain.TrimEnd('\n'), withLocals, StringComparison.Ordinal);
            Assert.Equal(2, RunCli("run", "--bogus", file).Exit);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
