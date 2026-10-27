using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace RecursiveAI.OneEcma.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "probe")
        {
            Probes.Run();
            return;
        }

        if (args.Length == 3 && args[0] == "cold")
        {
            Probes.Cold(args[1], args[2]);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
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
