using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace RecursiveAI.OneEcma.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "logsize")
        {
            // Size of the record log of each kernel: what recording keeps for a run that finishes.
            foreach ((string name, string source, _) in BenchScripts.All)
            {
                var engine = new ScriptEngine();
                engine.Evaluate(source);
                Console.WriteLine($"{name}: {engine.RecordLog.ToArray().Length} bytes, {engine.RecordLog.EventCount} events");
            }

            return;
        }

        if (args.Length >= 1 && args[0] == "longrun")
        {
            LongRun(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 12);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    /// <summary>
    /// Runs every kernel in sequence in this one process, round after round, the way a long-lived host would, and prints the
    /// time of each kernel per round (a fresh engine per run, then one engine reused) so a slowdown that grows with the process
    /// age shows as a trend down a column.
    /// </summary>
    private static void LongRun(int rounds)
    {
        // Jint running one kernel in the same process is the control: machine contention moves both, a real slowdown only ours.
        string controlSource = BenchScripts.All.First(x => x.Name == "props").Source;
        Console.WriteLine("round  mode  " + string.Join(" ", BenchScripts.All.Select(x => x.Name.PadLeft(9))) + "  jint-props");
        var shared = BenchScripts.All.ToDictionary(x => x.Name, _ => new ScriptEngine());
        for (int round = 0; round < rounds; round++)
        {
            foreach (bool reuse in new[] { false, true })
            {
                var cells = new List<string>();
                foreach ((string name, string source, _) in BenchScripts.All)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 3; i++)
                    {
                        (reuse ? shared[name] : new ScriptEngine()).Evaluate(source);
                    }

                    cells.Add((sw.Elapsed.TotalMilliseconds / 3).ToString("F1", System.Globalization.CultureInfo.InvariantCulture).PadLeft(9));
                }

                var control = System.Diagnostics.Stopwatch.StartNew();
                new global::Jint.Engine().Evaluate(controlSource);
                Console.WriteLine($"{round,5}  {(reuse ? "reuse" : "fresh")} {string.Join(" ", cells)} {control.Elapsed.TotalMilliseconds,11:F1}");
            }
        }
    }
}

/// <summary>Reference row so the harness has something to run; engine rows join it as the engine grows.</summary>
[MemoryDiagnoser]
public class StartupBenchmarks
{
    private readonly string _script = "let s = 0; for (let i = 0; i < 1000; i++) s += i; s";

    /// <summary>Evaluates a small loop in a fresh Jint engine.</summary>
    [Benchmark(Baseline = true)]
    public object? JintSmallLoop() => new Jint.Engine().Evaluate(_script).ToObject();
}
