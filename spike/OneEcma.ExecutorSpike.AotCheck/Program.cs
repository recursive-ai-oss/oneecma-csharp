using RecursiveAI.OneEcma.ExecutorSpike;
using RecursiveAI.OneEcma.ExecutorSpike.Bytecode;
using RecursiveAI.OneEcma.ExecutorSpike.Closure;

// Runs every spike script on both executors; a mismatch or a trimming/AOT warning fails the publish check.
int failures = 0;
foreach (var (name, source, expected) in SpikeScripts.All)
{
    foreach ((string engine, Func<string, object?> run) in new (string, Func<string, object?>)[]
    {
        ("closure", s => ClosureEngine.Evaluate(s)),
        ("bytecode", s => BytecodeEngine.Evaluate(s)),
    })
    {
        object? got = run(source);
        if (!Equals(got, expected))
        {
            Console.WriteLine($"MISMATCH {engine} {name}: {got} != {expected}");
            failures++;
        }
    }
}

Console.WriteLine(failures == 0 ? "ok" : "failed");
return failures;
