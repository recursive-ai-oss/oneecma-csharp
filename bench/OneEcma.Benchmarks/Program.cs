using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace RecursiveAI.OneEcma.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
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
