using BenchmarkDotNet.Attributes;
using RecursiveAI.OneEcma.ExecutorSpike;
using RecursiveAI.OneEcma.ExecutorSpike.Bytecode;
using RecursiveAI.OneEcma.ExecutorSpike.Closure;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Benchmarks;

/// <summary>
/// The same source on the closure-tree executor, the bytecode executor and Jint. Every iteration builds a fresh
/// realm/engine from an already parsed program, so setup, compilation and execution are all inside the number.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ExecutorBenchmarks
{
    private ParseResult _parsed = null!;
    private Jint.Prepared<Acornima.Ast.Script> _jintScript;
    private string _source = string.Empty;
    private object _expected = 0.0;

    /// <summary>Name of the script under test.</summary>
    [Params("startup", "fib25", "loops", "props", "closures", "strings", "vectors", "calc", "qsort", "throws")]
    public string Script { get; set; } = "fib25";

    [GlobalSetup]
    public void Setup()
    {
        if (Script == "startup")
        {
            _source = "1 + 1;";
            _expected = 2.0;
        }
        else
        {
            var s = SpikeScripts.All.First(x => x.Name == Script);
            _source = s.Source;
            _expected = s.Expected;
        }

        _parsed = JsParser.Parse(_source);
        _jintScript = Jint.Engine.PrepareScript(_source);
        Check("closure", ClosureEngine.Run(_parsed, _source));
        Check("bytecode", BytecodeEngine.Run(_parsed, _source));
        Check("jint", RunJint());
    }

    private void Check(string who, object? got)
    {
        if (!Equals(got, _expected))
        {
            throw new InvalidOperationException($"{who} gave {got} for {Script}, expected {_expected}");
        }
    }

    private object? RunJint() => new Jint.Engine().Evaluate(_jintScript).ToObject();

    /// <summary>Jint 4.17.0, the reference.</summary>
    [Benchmark(Baseline = true)]
    public object? JintRun() => RunJint();

    /// <summary>Executor A: closure-compiled tree.</summary>
    [Benchmark]
    public object? ClosureTree() => ClosureEngine.Run(_parsed, _source);

    /// <summary>Executor B: register bytecode.</summary>
    [Benchmark]
    public object? Bytecode() => BytecodeEngine.Run(_parsed, _source);
}

/// <summary>Value representation kernels: the same arithmetic loop over a struct value, a class per number and a boxed double.</summary>
[MemoryDiagnoser]
public class ValueRepBenchmarks
{
    private readonly int _n = 5_000_000;

    private static readonly object NumTag = new();

    private readonly struct SV(object? r, double d)
    {
        internal readonly object? Ref = r;
        internal readonly double Num = d;
    }

    private abstract class CV;

    private sealed class CNum(double d) : CV
    {
        internal readonly double D = d;
    }

    /// <summary>Plain doubles: the floor.</summary>
    [Benchmark(Baseline = true)]
    public double RawDouble()
    {
        double s = 0;
        for (int i = 0; i < _n; i++)
        {
            double t = i * 3.0;
            s = s + t;
            s = s - 1;
        }

        return s;
    }

    /// <summary>16-byte struct (reference tag plus double), as in the spike executors.</summary>
    [Benchmark]
    public double StructValue()
    {
        var regs = new SV[4];
        regs[0] = new SV(NumTag, 0);
        for (int i = 0; i < _n; i++)
        {
            regs[1] = new SV(NumTag, i);
            SV a = regs[1];
            regs[2] = ReferenceEquals(a.Ref, NumTag) ? new SV(NumTag, a.Num * 3.0) : default;
            SV s = regs[0];
            SV t = regs[2];
            regs[0] = ReferenceEquals(s.Ref, NumTag) && ReferenceEquals(t.Ref, NumTag) ? new SV(NumTag, s.Num + t.Num) : default;
            s = regs[0];
            regs[0] = ReferenceEquals(s.Ref, NumTag) ? new SV(NumTag, s.Num - 1) : default;
        }

        return regs[0].Num;
    }

    /// <summary>A heap object per number result.</summary>
    [Benchmark]
    public double ClassValue()
    {
        var regs = new CV?[4];
        regs[0] = new CNum(0);
        for (int i = 0; i < _n; i++)
        {
            regs[1] = new CNum(i);
            regs[2] = regs[1] is CNum a ? new CNum(a.D * 3.0) : null;
            regs[0] = regs[0] is CNum s && regs[2] is CNum t ? new CNum(s.D + t.D) : null;
            regs[0] = regs[0] is CNum s2 ? new CNum(s2.D - 1) : null;
        }

        return ((CNum)regs[0]!).D;
    }

    /// <summary>A boxed double per number result.</summary>
    [Benchmark]
    public double BoxedObject()
    {
        var regs = new object?[4];
        regs[0] = 0.0;
        for (int i = 0; i < _n; i++)
        {
            regs[1] = (double)i;
            regs[2] = regs[1] is double a ? a * 3.0 : null;
            regs[0] = regs[0] is double s && regs[2] is double t ? s + t : null;
            regs[0] = regs[0] is double s2 ? s2 - 1 : null;
        }

        return (double)regs[0]!;
    }
}
