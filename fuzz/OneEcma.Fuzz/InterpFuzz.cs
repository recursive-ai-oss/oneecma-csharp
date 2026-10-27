using System.Diagnostics;
using System.Text;
using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Mutational fuzzer for the interpreter. Seeds are .js files (test262 tests, corpus scripts); every mutant is run
/// as a script and as a module under tight fuel, memory and wall-clock limits. The only allowed outcomes are a value, a
/// <see cref="JsException"/>, a <see cref="JsLimitException"/> and cancellation. Anything else (an internal error, a host
/// exception, a hang past the deadline) is a finding written to the output directory.
/// usage: interp &lt;seconds&gt; &lt;outdir&gt; [seed] [seeddir...]; interp-file &lt;path&gt; replays one input.
/// </summary>
internal static class InterpFuzz
{
    private static readonly string[] Splices =
    [
        "await ", "yield ", "async ", "function* ", "class A extends null {}", "new.target", "super.x", "with (this) ", "eval('", "arguments",
        "Symbol.iterator", "Promise.resolve().then(", "for await (x of ", "try { ", "} finally { ", "throw ", "delete ", "typeof ",
        "0n", "1e308", "-0", "NaN", "[...", "...[", "{...", "`${", "=> ", "?.", "??=", "**", "#p", "static {", "get ", "set ",
        "import('x')", "import.meta", "debugger;", "label: ", "continue label;", "x = x", "[x] = [x]", "({x} = {x})", "'use strict';",
    ];

    private sealed class FuzzHost : DefaultHost
    {
        public FuzzHost()
            : base(7)
        {
        }

        public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes) =>
            specifier == "./dep.js" ? new HostModule("dep.js", "export let v = 1; export function f() { return v; } export default class {}") : null;
    }

    internal static string? Check(string source, bool module)
    {
        var limits = new EngineLimits { Fuel = 300_000, MaxAllocatedBytes = 24L << 20, WallClockMs = 3000, MaxCallDepth = 2000, MaxStringLength = 1 << 22 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new FuzzHost(), Limits = limits, InstallTest262Host = true, ThrowOnUnhandledRejection = false });
        try
        {
            if (module)
            {
                engine.EvaluateModule("./main.js");
            }
            else
            {
                engine.Evaluate(source, "fuzz.js");
            }

            return null;
        }
        catch (JsException e)
        {
            return e.Report is null ? "JsException without a report" : Render(e.Report);
        }
        catch (JsLimitException e)
        {
            return e.Report is null ? "JsLimitException without a report" : Render(e.Report);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message + "\n" + (e.InnerException ?? e).StackTrace;
        }
    }

    // Rendering must not throw either; null means the report was fine.
    private static string? Render(Diagnostic d)
    {
        try
        {
            _ = d.ToString();
            _ = DiagnosticJson.Render([d]);
            return null;
        }
        catch (Exception e)
        {
            return "rendering the report threw " + e.GetType().Name + ": " + e.Message;
        }
    }

    private sealed class ModuleHost : DefaultHost
    {
        private readonly string _main;

        public ModuleHost(string main)
            : base(7) => _main = main;

        public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes) =>
            specifier == "./main.js" ? new HostModule("main.js", _main) : specifier == "./dep.js" ? new HostModule("dep.js", "export let v = 1; export function f() { return v; } export default class {}") : null;
    }

    private static string? CheckModule(string source)
    {
        var limits = new EngineLimits { Fuel = 300_000, MaxAllocatedBytes = 24L << 20, WallClockMs = 3000, MaxCallDepth = 2000, MaxStringLength = 1 << 22 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new ModuleHost(source), Limits = limits, InstallTest262Host = true, ThrowOnUnhandledRejection = false });
        try
        {
            engine.EvaluateModule("./main.js");
            return null;
        }
        catch (JsException e)
        {
            return e.Report is null ? "JsException without a report" : Render(e.Report);
        }
        catch (JsLimitException e)
        {
            return e.Report is null ? "JsLimitException without a report" : Render(e.Report);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message + "\n" + (e.InnerException ?? e).StackTrace;
        }
    }

    /// <summary>Runs one input in both modes on a worker thread; reports a failure text or a hang.</summary>
    internal static string? RunOne(string source, int deadlineSeconds)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            result = Check(source, false) ?? CheckModule(source);
            if (result is not null && (result.StartsWith("JsException without", StringComparison.Ordinal) || result.Contains("threw", StringComparison.Ordinal) || result.Contains("Exception:", StringComparison.Ordinal)))
            {
                return;
            }

            result = null;
        }, 16 * 1024 * 1024);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(deadlineSeconds)))
        {
            return "hang: no result after " + deadlineSeconds + " seconds";
        }

        return result;
    }

    internal static int RunRandom(int seconds, string outDir, int seed, IEnumerable<string> seedDirs)
    {
        Directory.CreateDirectory(outDir);
        var seeds = new List<string>();
        foreach (string d in seedDirs)
        {
            foreach (string f in Directory.EnumerateFiles(d, "*.js", SearchOption.AllDirectories))
            {
                if (new FileInfo(f).Length < 32 * 1024 && !f.Contains("_FIXTURE", StringComparison.Ordinal))
                {
                    seeds.Add(File.ReadAllText(f));
                }
            }
        }

        if (seeds.Count == 0)
        {
            seeds.Add("var x = 1; function f(a) { return a + x; } f(2);");
        }

        var rng = new Random(seed);
        long executions = 0;
        int findings = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            string s = seeds[rng.Next(seeds.Count)];
            string m = Mutate(s, rng);
            string? failure = RunOne(m, 20);
            executions++;
            if (failure is not null)
            {
                findings++;
                string path = Path.Combine(outDir, "interp-" + findings.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".js");
                File.WriteAllText(path, m);
                File.WriteAllText(path + ".txt", failure);
                Console.Error.WriteLine("finding " + findings + ": " + failure.Split('\n')[0]);
                if (failure.StartsWith("hang", StringComparison.Ordinal))
                {
                    Console.WriteLine($"interp: executions={executions} findings={findings} (stopped on a hang)");
                    return 1;
                }
            }
        }

        Console.WriteLine($"interp: executions={executions} findings={findings} seeds={seeds.Count} seconds={clock.Elapsed.TotalSeconds:F0}");
        return findings == 0 ? 0 : 1;
    }

    private static string Mutate(string s, Random rng)
    {
        var sb = new StringBuilder(s);
        int edits = 1 + rng.Next(4);
        for (int i = 0; i < edits && sb.Length > 0; i++)
        {
            int pos = rng.Next(sb.Length);
            switch (rng.Next(6))
            {
                case 0:
                    sb.Remove(pos, Math.Min(sb.Length - pos, 1 + rng.Next(12)));
                    break;
                case 1:
                    sb.Insert(pos, Splices[rng.Next(Splices.Length)]);
                    break;
                case 2:
                    {
                        int len = Math.Min(sb.Length - pos, 1 + rng.Next(40));
                        sb.Insert(rng.Next(sb.Length), sb.ToString(pos, len));
                        break;
                    }

                case 3:
                    sb[pos] = (char)(32 + rng.Next(95));
                    break;
                case 4:
                    {
                        int a = rng.Next(sb.Length);
                        int b = rng.Next(sb.Length);
                        (sb[a], sb[b]) = (sb[b], sb[a]);
                        break;
                    }

                default:
                    sb.Insert(pos, "import x from './dep.js'; ");
                    break;
            }
        }

        return sb.ToString();
    }
}
