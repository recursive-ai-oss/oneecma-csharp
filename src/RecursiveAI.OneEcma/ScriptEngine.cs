using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Entry point of the interpreter. This is a placeholder: the engine is under construction
/// and every execution entry point throws <see cref="NotSupportedException"/> until it lands.
/// </summary>
public sealed class ScriptEngine
{
    /// <summary>The library version, as a constant so reading it needs no reflection.</summary>
    public const string Version = "0.0.0-dev";

    // Instance state the real engine will replace; keeps the members instance members.
    private int _requests;

    /// <summary>Evaluates a script and returns its completion value converted to a string.</summary>
    /// <param name="source">The script text.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Never returns in this placeholder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Always: the interpreter is not implemented yet.</exception>
    public string Evaluate(string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _requests++;
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("The interpreter is not implemented yet.");
    }

    /// <summary>Parses a script and reports its syntax and early errors without running it.</summary>
    /// <param name="source">The script text.</param>
    /// <param name="options">Parse options; <see langword="null"/> means a sloppy classic script named <c>&lt;anonymous&gt;</c>.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Every error found; an empty list when the source is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public IReadOnlyList<Diagnostic> Check(string source, ParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _requests++;
        return JsParser.Parse(source, options, cancellationToken).Diagnostics;
    }

    /// <summary>
    /// Like <see cref="Check"/>, but when the source does not parse it tries the syntax-only repairs of the repair table. Off unless
    /// called; see <see cref="SyntaxRepair.Repair(string, ParseOptions?, RepairOptions?, CancellationToken)"/> for the rules.
    /// </summary>
    /// <param name="source">The script text.</param>
    /// <param name="options">Parse options; <see langword="null"/> means a sloppy classic script named <c>&lt;anonymous&gt;</c>.</param>
    /// <param name="repair">Repair limits; <see langword="null"/> means the defaults.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    /// <returns>The repaired source, the repairs applied first-to-last and the diagnostics that remain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public RepairResult CheckAndRepair(string source, ParseOptions? options = null, RepairOptions? repair = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _requests++;
        return SyntaxRepair.Repair(source, options, repair, cancellationToken);
    }
}
