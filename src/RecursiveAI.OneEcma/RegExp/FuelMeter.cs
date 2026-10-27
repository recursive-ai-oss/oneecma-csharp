namespace RecursiveAI.OneEcma.RegExp;

/// <summary>
/// A budget of abstract work units shared by everything that runs script-controlled loops. The RegExp matcher
/// charges one unit per instruction step, per backtrack and per character a repeated single-character atom
/// consumes, so a catastrophic pattern ends in a limit instead of a hang.
/// </summary>
/// <remarks>
/// The matcher draws units in chunks (<see cref="ChunkSize"/>) through <see cref="TryConsume"/> and counts them
/// down locally, so the call is rare. A host that also wants a wall-clock or cancellation check derives from this
/// class and overrides <see cref="TryConsume"/>: returning <see langword="false"/> stops the work with a limit result.
/// Instances are not thread-safe.
/// </remarks>
public class FuelMeter
{
    /// <summary>Units the matcher takes from the meter in one call.</summary>
    public const int ChunkSize = 1024;

    private long _remaining;

    /// <summary>Creates a meter holding <paramref name="units"/> units.</summary>
    /// <param name="units">The budget; zero or negative means nothing can be consumed.</param>
    public FuelMeter(long units)
    {
        _remaining = units;
    }

    /// <summary>A meter that never runs out.</summary>
    public static FuelMeter Unlimited => new(long.MaxValue);

    /// <summary>Units left.</summary>
    public long Remaining => _remaining;

    /// <summary>
    /// Takes <paramref name="n"/> units if that many remain. When fewer remain the meter is drained to zero and
    /// the call returns <see langword="false"/>.
    /// </summary>
    /// <param name="n">Units wanted; non-negative.</param>
    /// <returns><see langword="true"/> if the units were granted.</returns>
    public virtual bool TryConsume(long n)
    {
        if (n <= _remaining)
        {
            _remaining -= n;
            return true;
        }

        _remaining = 0;
        return false;
    }

    /// <summary>Gives back units that were taken with <see cref="TryConsume"/> but not used.</summary>
    /// <param name="n">Units to return; non-negative.</param>
    public virtual void Refund(long n)
    {
        if (n > 0)
        {
            _remaining = _remaining > long.MaxValue - n ? long.MaxValue : _remaining + n;
        }
    }
}

/// <summary>Resource limits for one match call besides fuel.</summary>
public sealed class RegExpLimits
{
    /// <summary>The limits used when a caller passes none: 64 MiB of backtrack stack.</summary>
    public static RegExpLimits Default { get; } = new();

    /// <summary>
    /// Upper bound, in bytes, of the matcher's backtrack stack. Exceeding it ends the match with
    /// <see cref="RegExpOutcome.LimitExceeded"/> and <see cref="RegExpLimitKind.Memory"/>. Default 64 MiB.
    /// </summary>
    public long MaxBacktrackBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>Result of a match call.</summary>
public enum RegExpOutcome
{
    /// <summary>No match at any start position tried.</summary>
    NoMatch = 0,

    /// <summary>A match was found.</summary>
    Match = 1,

    /// <summary>Fuel or memory ran out first; see <see cref="RegExpLimitKind"/>.</summary>
    LimitExceeded = 2,
}

/// <summary>Which limit ended a match call.</summary>
public enum RegExpLimitKind
{
    /// <summary>No limit was hit.</summary>
    None = 0,

    /// <summary>The fuel meter refused more units.</summary>
    Fuel = 1,

    /// <summary>The backtrack stack reached its byte limit.</summary>
    Memory = 2,
}
