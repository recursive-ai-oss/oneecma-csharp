using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace RecursiveAI.OneEcma.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "verify")
        {
            // Runs every row once on both engines and compares with the reference Node; no timing.
            Environment.ExitCode = Verify();
            return;
        }

        if (args.Length == 3 && args[0] == "once")
        {
            // One fresh-engine run of one row in this process, so an outer `time -v` reports its peak resident memory.
            (_, string src, _) = BenchWorkloads.All().First(x => x.Name == args[2]);
            string result;
            if (args[1] == "jint")
            {
                result = new Jint.Engine().Evaluate(src).ToString();
            }
            else
            {
                var engine = new ScriptEngine();
                result = engine.ToDisplayString(engine.Evaluate(src));
            }

            Console.WriteLine(result.Length);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }


    private static int Verify()
    {
        int bad = 0;
        foreach ((string name, string source, object expected) in BenchWorkloads.All())
        {
            string? why = BenchWorkloads.WhyOneEcmaCannotRun(source, expected);
            string jint;
            try
            {
                jint = BenchWorkloads.Matches(expected, new Jint.Engine().Evaluate(source).ToString()) ? "ok" : "DIFFERENT";
            }
            catch (Exception e)
            {
                jint = "ERROR " + e.GetType().Name + ": " + e.Message;
            }

            Console.WriteLine($"{name,-10} len={(expected as string)?.Length.ToString() ?? "num"} oneecma={why ?? "ok"}  jint={jint}");
            bad += why is null && jint == "ok" ? 0 : 1;
        }

        return bad;
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
