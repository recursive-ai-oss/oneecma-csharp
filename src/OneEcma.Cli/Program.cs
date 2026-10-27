using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Cli;

internal static class Program
{
    private const string UsageText =
        "usage: oneecma run <file>\n" +
        "       oneecma check [--module] [--repair [--out <path>]] <file>\n" +
        "       oneecma repl\n";

    private static int Check(ScriptEngine engine, string[] args)
    {
        bool module = false;
        bool repair = false;
        string? outPath = null;
        string? path = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--module":
                    module = true;
                    break;
                case "--repair":
                    repair = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                default:
                    if (path is not null || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Usage();
                    }

                    path = args[i];
                    break;
            }
        }

        if (path is null || (outPath is not null && !repair))
        {
            return Usage();
        }

        string source = File.ReadAllText(path);
        var options = new ParseOptions { Goal = module ? ParseGoal.Module : ParseGoal.Script, FileName = path };
        bool ansi = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var render = new RenderOptions { Ansi = ansi };
        if (!repair)
        {
            IReadOnlyList<Diagnostic> diagnostics = engine.Check(source, options);
            if (diagnostics.Count == 0)
            {
                return 0;
            }

            Console.Error.Write(DiagnosticRenderer.RenderAll(diagnostics, render));
            return 1;
        }

        RepairResult result = engine.CheckAndRepair(source, options);
        Console.Error.Write(DiagnosticRenderer.RenderRepairResult(result, render));
        if (!result.Parses)
        {
            return 1;
        }

        // The repaired source goes to the file or to stdout, so `oneecma check --repair a.js > fixed.js` works.
        if (outPath is not null)
        {
            File.WriteAllText(outPath, result.RepairedSource);
        }
        else
        {
            Console.Out.Write(result.RepairedSource);
        }

        return 0;
    }

    private static int Usage()
    {
        Console.Error.Write(UsageText);
        return 2;
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Out.Write(UsageText);
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] is "--version")
        {
            Console.Out.WriteLine(ScriptEngine.Version);
            return 0;
        }

        var engine = new ScriptEngine();
        try
        {
            switch (args[0])
            {
                case "run" when args.Length == 2:
                    Console.Out.WriteLine(engine.Evaluate(File.ReadAllText(args[1])));
                    return 0;
                case "check" when args.Length >= 2:
                    return Check(engine, args);
                case "repl" when args.Length == 1:
                    Console.Error.WriteLine("oneecma: repl is not implemented yet");
                    return 1;
                default:
                    return Usage();
            }
        }
        catch (NotSupportedException e)
        {
            Console.Error.WriteLine("oneecma: " + e.Message);
            return 1;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine("oneecma: " + e.Message);
            return 1;
        }
    }
}
