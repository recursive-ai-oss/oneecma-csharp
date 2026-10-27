using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.SourceMaps;
using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Cli;

internal static class Program
{
    private const string UsageText =
        "usage: oneecma run <file>\n" +
        "       oneecma check [--module] [--format text|json] [--source-map <map>] [--repair [--out <path>]] <file>\n" +
        "       oneecma repl\n";

    private static int Check(ScriptEngine engine, string[] args)
    {
        bool module = false;
        bool repair = false;
        bool json = false;
        string? outPath = null;
        string? mapPath = null;
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
                case "--source-map" when i + 1 < args.Length:
                    mapPath = args[++i];
                    break;
                case "--format" when i + 1 < args.Length:
                    string format = args[++i];
                    if (format is not ("text" or "json"))
                    {
                        return Usage();
                    }

                    json = format == "json";
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

        IReadOnlyList<Diagnostic> diagnostics;
        IReadOnlyList<AppliedRepair> repairs = [];
        RepairResult? result = null;
        if (repair)
        {
            result = ScriptEngine.CheckAndRepair(source, options);
            diagnostics = result.Diagnostics;
            repairs = result.Repairs;
        }
        else
        {
            diagnostics = ScriptEngine.Check(source, options);
        }

        // Only a file that has something to report needs its source map.
        if (diagnostics.Count > 0)
        {
            diagnostics = ApplySourceMap(new SourceText(source, path), mapPath, diagnostics);
        }

        bool failed = diagnostics.Any(d => d.Severity == Severity.Error);
        string? repaired = result is { Changed: true } ? result.RepairedSource : null;
        if (json)
        {
            Console.Out.WriteLine(DiagnosticJson.Render(diagnostics, repairs, repaired));
        }
        else if (result is null)
        {
            if (diagnostics.Count > 0)
            {
                Console.Error.Write(DiagnosticRenderer.RenderAll(diagnostics, render));
            }
        }
        else
        {
            Console.Error.Write(DiagnosticRenderer.RenderRepairs(repairs, diagnostics, render));
        }

        if (failed || (repair && result is not null && !result.Parses))
        {
            return 1;
        }

        // The repaired source goes to the file or to stdout, so `oneecma check --repair a.js > fixed.js` works.
        if (repair)
        {
            if (outPath is not null)
            {
                File.WriteAllText(outPath, result!.RepairedSource);
            }
            else if (!json)
            {
                Console.Out.Write(result!.RepairedSource);
            }
        }

        return 0;
    }

    /// <summary>
    /// Moves diagnostics about generated code to the original source when a source map is given with --source-map or the file
    /// ends with a sourceMappingURL comment (a data: URL, or a file next to it). A map that cannot be used adds warnings, never errors.
    /// </summary>
    private static IReadOnlyList<Diagnostic> ApplySourceMap(SourceText generated, string? mapPath, IReadOnlyList<Diagnostic> diagnostics)
    {
        SourceMapParseResult? loaded;
        if (mapPath is not null)
        {
            loaded = SourceMap.Parse(SourceMapLoader.StripXssiPrefix(File.ReadAllText(mapPath)), mapPath, null, mapPath);
        }
        else
        {
            loaded = SourceMapLoader.Load(generated, ReadFile);
        }

        if (loaded is null)
        {
            return diagnostics;
        }

        var all = new List<Diagnostic>(diagnostics.Count);
        all.AddRange(loaded.Map is null ? diagnostics : SourceMapReports.RemapAll(diagnostics, loaded.Map, ReadFile));
        all.AddRange(loaded.ToDiagnostics());
        return all;
    }

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static int Run(ScriptEngine engine, string[] args)
    {
        bool module = args.Length == 3 && args[1] == "--module";
        string path = args[^1];
        engine.DefineGlobalFunction("print", 1, (_, a) =>
        {
            var parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                parts[i] = engine.ToDisplayString(a[i]);
            }

            Console.Out.WriteLine(string.Join(' ', parts));
            return JsValue.Undefined;
        });
        engine.Evaluate("globalThis.console = { log: print, error: print, warn: print, info: print, debug: print };");
        try
        {
            if (module)
            {
                engine.EvaluateModule(Path.GetFullPath(path));
            }
            else
            {
                engine.Evaluate(File.ReadAllText(path), Path.GetFileName(path));
            }

            return 0;
        }
        catch (JsException e)
        {
            Console.Error.Write(e.Report is { } r ? r.ToString() : e.Message);
            return 1;
        }
        catch (JsLimitException e)
        {
            Console.Error.Write(e.Report is { } r ? r.ToString() : e.Message);
            return 1;
        }
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

        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new FileHost() });
        try
        {
            switch (args[0])
            {
                case "run" when args.Length is 2 or 3:
                    return Run(engine, args);
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
