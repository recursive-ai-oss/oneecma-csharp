using RecursiveAI.OneEcma;

namespace RecursiveAI.OneEcma.AotSmoke;

/// <summary>Exported with [JsExport]: the generator writes its binding, so nothing here needs reflection under NativeAOT.</summary>
[JsExport]
public enum Mode
{
    Fast,
    Slow,
}

/// <summary>A by-value type: script passes and receives plain objects.</summary>
[JsExport]
public record Job(string Name, int Cost, Mode Mode = Mode.Fast);

/// <summary>A by-reference service with an async method, a callback and a property.</summary>
[JsExport]
public class Scheduler
{
    public int Done { get; set; }

    public Job Plan(Job job) => job with { Cost = job.Cost * 2 + Done };

    public async Task<int> RunAsync(Func<Job, int> measure, Job job)
    {
        await Task.Yield();
        Done++;
        return measure(job) + Done;
    }

    public void Fail() => throw new InvalidOperationException("scheduler stopped after " + Done);
}
