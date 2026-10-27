namespace RecursiveAI.OneEcma;

/// <summary>A module the host resolved: a stable identity (used as the cache key and as the file name in reports) and its source text.</summary>
/// <param name="Id">The resolved identity. Two specifiers resolving to the same Id share one module record.</param>
/// <param name="Source">The module source text.</param>
public sealed record HostModule(string Id, string Source);

/// <summary>
/// The only way the engine reaches the outside world. Everything nondeterministic or environmental crosses this
/// interface, so a recording host can log every value and a replaying host can feed them back.
/// </summary>
public interface IJsHost
{
    /// <summary>Milliseconds since the Unix epoch (Date.now, new Date()).</summary>
    double CurrentTimeMs();

    /// <summary>A monotonic clock in milliseconds, used for the wall-clock limit and performance.now.</summary>
    double MonotonicMs();

    /// <summary>A value in [0, 1) for Math.random.</summary>
    double NextRandom();

    /// <summary>The BCP 47 tag used when a locale is not given.</summary>
    string DefaultLocale { get; }

    /// <summary>The IANA time zone id used for local time.</summary>
    string TimeZoneId { get; }

    /// <summary>
    /// The offset of a time zone at one instant, for local time in <c>Date</c> and for Intl. The default reads the machine's
    /// time zone rules through the BCL; a host overrides it to supply its own rules (a recorded table, a tzdata reader) so a run
    /// does not depend on the machine.
    /// </summary>
    /// <param name="zoneId">An IANA zone id such as <c>America/Chicago</c> (normally <see cref="TimeZoneId"/>).</param>
    /// <param name="epochMs">The instant, in milliseconds since the Unix epoch.</param>
    /// <param name="offsetMs">Milliseconds east of UTC at that instant.</param>
    /// <param name="isDaylight">Whether daylight saving time is in effect.</param>
    /// <returns>False when the zone is unknown. Local-time operations of <c>Date</c> then throw a <c>RangeError</c> naming the zone, rather than reading it as UTC.</returns>
    bool TryGetTimeZoneOffset(string zoneId, double epochMs, out int offsetMs, out bool isDaylight)
    {
        bool known = RecursiveAI.OneEcma.Intl.BclTimeZoneSource.Shared.TryGetOffset(zoneId, epochMs, out RecursiveAI.OneEcma.Intl.TimeZoneOffset o);
        offsetMs = o.OffsetMs;
        isDaylight = o.IsDaylight;
        return known;
    }

    /// <summary>
    /// Resolves a module specifier. <paramref name="referrerId"/> is the Id of the importing module, or null for
    /// <c>import()</c> from a script. Returns null when the module does not exist.
    /// </summary>
    HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes);

    /// <summary>Receives text printed by the host-provided <c>print</c> function.</summary>
    void Print(string text);

    /// <summary>Called whenever a promise job is queued, in order; recording hosts log it.</summary>
    void JobEnqueued();

    /// <summary>Called when a promise is rejected with no handler (<paramref name="handled"/> false) and when a handler is added later (true).</summary>
    void PromiseRejectionTracker(JsObject promise, bool handled);

    /// <summary>Adds host properties to <c>import.meta</c> of the module with the given Id.</summary>
    void InitializeImportMeta(string moduleId, JsObject meta);

    /// <summary>
    /// Called at a weak-reference collection point (<c>$262.gc()</c> in the conformance host) with the ids of the
    /// WeakRef and FinalizationRegistry targets that were found unreachable and are now dead, ascending. Recording hosts
    /// log it. The default does nothing.
    /// </summary>
    /// <param name="ids">Creation-order ids of the dead weak targets.</param>
    void WeakTargetsCollected(IReadOnlyList<long> ids)
    {
    }

    /// <summary>
    /// Asked once the job queue has drained (the end of a macrotask) and the script has created a <c>WeakRef</c> or a
    /// <c>FinalizationRegistry</c> target at some point: whether to run a weak-reference collection point now. Until a collection point,
    /// every weak target is held strongly, so a long run that never reaches one keeps all of them alive; the host decides when the cost
    /// of collecting is worth it, for example once managed memory has grown. The answer is recorded, so a replay reaches the same points
    /// whatever the live host would say. The default is false (collection points only where a script asks, as <c>$262.gc()</c> does).
    /// </summary>
    /// <returns>True to run a collection point; the engine then calls <see cref="WeakTargetsCollected"/> with the dead targets.</returns>
    bool WantsWeakCollection() => false;

    /// <summary>
    /// Replay hook for <see cref="WeakTargetsCollected"/>: when a replaying host returns a list, the engine does not run
    /// the garbage collector at a collection point and treats exactly those ids as dead, so a recorded run reproduces.
    /// Returns null (the default) to let the collector decide.
    /// </summary>
    /// <returns>The recorded ids, or null.</returns>
    IReadOnlyList<long>? ReplayWeakCollection() => null;

    /// <summary>
    /// Whether <c>Atomics.wait</c> may suspend the running agent ([[CanBlock]], ECMA-262 9.7). The engine has one agent, so a
    /// permitted wait can only time out, at once and without sleeping. The default is true.
    /// </summary>
    bool CanBlock => true;
}
