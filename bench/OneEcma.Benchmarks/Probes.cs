using RecursiveAI.OneEcma.ExecutorSpike;
using RecursiveAI.OneEcma.ExecutorSpike.Bytecode;
using RecursiveAI.OneEcma.ExecutorSpike.Closure;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Benchmarks;

/// <summary>One-off measurements that are not timing runs: reachable call depth per host stack size, parser depth limits.</summary>
internal static class Probes
{
    internal static void Run()
    {
        Console.WriteLine("## Reachable JavaScript call depth (limit MaxDepth = 1,000,000; f(n) recurses n deep)");
        Console.WriteLine("| host stack | closure tree | bytecode |");
        Console.WriteLine("|---|---|---|");
        foreach (int mb in new[] { 1, 8, 64 })
        {
            long a = MaxDepth(mb, n => ClosureEngine.Evaluate(Src(n), new SpikeLimits { MaxDepth = 1_000_000 }));
            long b = MaxDepth(mb, n => BytecodeEngine.Evaluate(Src(n), new SpikeLimits { MaxDepth = 1_000_000 }));
            Console.WriteLine($"| {mb} MB | {a} | {b} |");
        }

        Console.WriteLine();
        Console.WriteLine("## Parser depth on flat chains (does the front end accept 100,000 links?)");
        foreach ((string name, string src) in new[]
        {
            ("a + a + ... (binary)", "var a = 1; a" + string.Concat(Enumerable.Repeat(" + a", 100_000))),
            ("a.b.b.b ... (member)", "var a = {}; a" + string.Concat(Enumerable.Repeat(".b", 100_000))),
            ("f()()()... (call)", "var f = 1; f" + string.Concat(Enumerable.Repeat("()", 100_000))),
            ("a = a = a ... (assign)", "var a; " + string.Concat(Enumerable.Repeat("a = ", 100_000)) + "1"),
        })
        {
            ParseResult p = RunOnThread(256 * 1024, () => JsParser.Parse(src));
            Console.WriteLine($"- {name}: errors={p.HasErrors} {(p.HasErrors ? p.Diagnostics[0].Headline : string.Empty)}");
        }
    }

    /// <summary>One cold evaluation in a fresh process (parse included): milliseconds, managed bytes allocated, peak working set in KB.</summary>
    internal static void Cold(string engine, string script)
    {
        string src = script == "startup" ? "1 + 1;" : SpikeScripts.All.First(x => x.Name == script).Source;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        object? r = engine switch
        {
            "jint" => new Jint.Engine().Evaluate(src).ToObject(),
            "closure" => ClosureEngine.Evaluate(src),
            "bytecode" => BytecodeEngine.Evaluate(src),
            _ => throw new ArgumentException("engine must be jint, closure or bytecode", nameof(engine)),
        };
        sw.Stop();
        long peak = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024;
        Console.WriteLine($"{engine},{script},{sw.Elapsed.TotalMilliseconds:F1},{GC.GetTotalAllocatedBytes()},{peak},{r}");
    }

    private static string Src(long n) => $"function f(n){{ return n === 0 ? 0 : 1 + f(n - 1); }} f({n})";

    private static T RunOnThread<T>(int stack, Func<T> f)
    {
        T? r = default;
        var t = new Thread(() => r = f(), stack);
        t.Start();
        t.Join();
        return r!;
    }

    private static long MaxDepth(int stackMb, Func<long, object?> run)
    {
        long lo = 100;
        long hi = 400_000;
        while (lo < hi)
        {
            long mid = (lo + hi + 1) / 2;
            bool ok = RunOnThread(stackMb * 1024 * 1024, () =>
            {
                try
                {
                    return Equals(run(mid), (double)mid);
                }
                catch (SpikeLimitException)
                {
                    return false;
                }
            });
            if (ok)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
