using System.Diagnostics;

namespace RecursiveAI.OneEcma;

/// <summary>
/// The default host: the system clock, a seeded generator, no modules. This is the one file of the engine
/// that may touch the clock or a source of randomness.
/// </summary>
public class DefaultHost : IJsHost
{
    private readonly Random _random;
    private readonly long _t0 = Stopwatch.GetTimestamp();

    /// <summary>Creates a host. A seed makes Math.random reproducible.</summary>
    public DefaultHost(int? seed = null) => _random = seed is int s ? new Random(s) : new Random();

    /// <inheritdoc/>
    public virtual double CurrentTimeMs() => Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);

    /// <inheritdoc/>
    public virtual double MonotonicMs() => Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;

    /// <inheritdoc/>
    public virtual double NextRandom() => _random.NextDouble();

    /// <inheritdoc/>
    public virtual string DefaultLocale => "en-US";

    /// <inheritdoc/>
    public virtual string TimeZoneId => "UTC";

    /// <inheritdoc/>
    public virtual bool TryGetTimeZoneOffset(string zoneId, double epochMs, out int offsetMs, out bool isDaylight)
    {
        bool known = Intl.BclTimeZoneSource.Shared.TryGetOffset(zoneId, epochMs, out Intl.TimeZoneOffset o);
        offsetMs = o.OffsetMs;
        isDaylight = o.IsDaylight;
        return known;
    }

    /// <inheritdoc/>
    public virtual HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes) => null;

    /// <inheritdoc/>
    public virtual void Print(string text)
    {
    }

    /// <summary>
    /// How much managed memory must have been allocated since the last weak-reference collection point (or since the lowest
    /// level seen) before <see cref="WantsWeakCollection"/> asks for another. Default 8 MiB.
    /// </summary>
    protected virtual long WeakCollectionGrowthBytes => 8L << 20;

    private long _weakBaseline = long.MaxValue;

    /// <summary>
    /// Asks for a collection point once managed memory has grown by <see cref="WeakCollectionGrowthBytes"/> since the lowest level
    /// seen at an earlier question, so a long run holds weak targets only while it is not allocating. The memory figure is the
    /// host's own and the answer is recorded by a recording engine, so a replay is exact.
    /// </summary>
    public virtual bool WantsWeakCollection()
    {
        long now = GC.GetTotalMemory(false);
        if (now < _weakBaseline)
        {
            _weakBaseline = now;
        }

        if (now - _weakBaseline < WeakCollectionGrowthBytes)
        {
            return false;
        }

        // A collection point follows; the next question measures from the level after it.
        _weakBaseline = long.MaxValue;
        return true;
    }

    /// <inheritdoc/>
    public virtual void JobEnqueued()
    {
    }

    /// <inheritdoc/>
    public virtual void PromiseRejectionTracker(JsObject promise, bool handled)
    {
    }

    /// <inheritdoc/>
    public virtual void InitializeImportMeta(string moduleId, JsObject meta)
    {
    }
}
