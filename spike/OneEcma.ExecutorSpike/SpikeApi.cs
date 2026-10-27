namespace RecursiveAI.OneEcma.ExecutorSpike;

/// <summary>Resource limits of a spike run.</summary>
public sealed class SpikeLimits
{
    /// <summary>Steps allowed (loop back-edges and calls).</summary>
    public long Fuel { get; set; } = long.MaxValue / 2;

    /// <summary>Maximum JavaScript call depth.</summary>
    public int MaxDepth { get; set; } = 10_000;
}

/// <summary>A JavaScript exception that reached the host.</summary>
public sealed class SpikeUncaughtException : Exception
{
    public SpikeUncaughtException(string message)
        : base(message)
    {
    }
}
