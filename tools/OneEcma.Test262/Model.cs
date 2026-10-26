namespace RecursiveAI.OneEcma.Test262;

/// <summary>How the source text of a test is interpreted.</summary>
public enum SourceMode
{
    /// <summary>Global code, non-strict.</summary>
    Sloppy,

    /// <summary>Global code with a "use strict" directive prepended.</summary>
    Strict,

    /// <summary>Module code.</summary>
    Module,
}

/// <summary>A harness file evaluated in the test realm before the test.</summary>
public sealed record HarnessScript(string Name, string Source);

/// <summary>One test in one mode, ready for a backend.</summary>
public sealed class PreparedTest
{
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }

    public required TestMetadata Meta { get; init; }

    public required SourceMode Mode { get; init; }

    /// <summary>The raw flag: the source must run unmodified and no harness is loaded.</summary>
    public bool Raw { get; init; }

    public bool IsAsync { get; init; }

    /// <summary>Only parse the test source (and report syntax and early errors); do not run it.</summary>
    public bool ParseOnly { get; init; }

    /// <summary>Harness files in evaluation order. Empty for raw tests.</summary>
    public required IReadOnlyList<HarnessScript> Harness { get; init; }

    /// <summary>The test file text, unmodified.</summary>
    public required string Source { get; init; }

    /// <summary>The test text as it must be evaluated: with the strict directive in strict mode.</summary>
    public string EffectiveSource => Mode == SourceMode.Strict ? "\"use strict\";\n" + Source : Source;
}

/// <summary>What a backend observed when it ran a test.</summary>
/// <param name="Threw">An uncaught error ended the test (or, for async tests, $DONE reported failure).</param>
/// <param name="Phase">parse, resolution, runtime or async when <paramref name="Threw"/>.</param>
/// <param name="ErrorType">Constructor name of the error, such as SyntaxError or Test262Error.</param>
/// <param name="Message">The error message, for the failure detail.</param>
/// <param name="NotImplemented">The backend cannot run this kind of test at all.</param>
/// <param name="TimedOut">The per-test time limit passed.</param>
public sealed record BackendResult(
    bool Threw,
    string Phase = "",
    string ErrorType = "",
    string Message = "",
    bool NotImplemented = false,
    bool TimedOut = false)
{
    public static BackendResult Passed { get; } = new(false);
}

/// <summary>An engine the runner can drive.</summary>
public interface ITestBackend
{
    string Name { get; }

    /// <summary>The backend can parse without running, so parse-only mode works.</summary>
    bool SupportsParseOnly { get; }

    /// <summary>Whether blocking waits (Atomics.wait) are allowed on the main agent.</summary>
    bool CanBlock { get; }

    /// <summary>Test262 features the host cannot provide; tests needing them are skipped, not failed.</summary>
    IReadOnlySet<string> UnsupportedFeatures { get; }

    Task<BackendResult> RunAsync(PreparedTest test, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum TestStatus
{
    Pass,
    Fail,
    Documented,
    SkippedFeature,
    SkippedLocale,
    SkippedHost,
    SkippedParseOnly,
}

/// <summary>The verdict on one test across all its modes.</summary>
public sealed record TestResult(
    string RelativePath,
    TestStatus Status,
    string Detail = "",
    bool NotImplemented = false,
    bool StaleDocumented = false);
