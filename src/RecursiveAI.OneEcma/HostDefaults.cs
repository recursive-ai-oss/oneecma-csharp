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
