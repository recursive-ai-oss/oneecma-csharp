using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Test262;

/// <summary>
/// Runs tests through the library's public entry points. Until the engine can run scripts with
/// host functions, anything it cannot do is reported as not implemented rather than failed.
/// </summary>
public sealed class EngineBackend : ITestBackend
{
    public string Name => "engine";

    public bool SupportsParseOnly => true;

    public bool CanBlock => true;

    public IReadOnlySet<string> UnsupportedFeatures { get; } = new HashSet<string>(StringComparer.Ordinal);

    public Task<BackendResult> RunAsync(PreparedTest test, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        return Task.Run(() => Run(test, timeout, cancellationToken), cancellationToken);
    }

    private static BackendResult Run(PreparedTest test, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (test.ParseOnly)
        {
            var options = new ParseOptions { Goal = test.Mode == SourceMode.Module ? ParseGoal.Module : ParseGoal.Script };
            var result = JsParser.Parse(test.EffectiveSource, options, cancellationToken);
            if (!result.HasErrors)
            {
                return BackendResult.Passed;
            }

            Diagnostic d = result.Diagnostics[0];
            return new BackendResult(true, "parse", d.Kind, d.Headline + " at " + d.Position.Line + ":" + d.Position.Column);
        }

        var host = new TestHost(test);
        var limits = new EngineLimits { WallClockMs = timeout.TotalMilliseconds, MaxAllocatedBytes = 768L << 20, Fuel = 20_000_000_000L };
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, Limits = limits, InstallTest262Host = true, ThrowOnUnhandledRejection = false });
        string phase = "runtime";
        try
        {
            foreach (HarnessScript h in test.Harness)
            {
                phase = "harness";
                engine.Evaluate(h.Source, h.Name, cancellationToken);
            }

            phase = "parse";
            if (test.Mode == SourceMode.Module)
            {
                phase = "parse";
                engine.EvaluateModule(test.FullPath, cancellationToken);
            }
            else
            {
                engine.Evaluate(test.EffectiveSource, Path.GetFileName(test.FullPath), cancellationToken);
            }

            if (test.IsAsync)
            {
                engine.RunJobs(cancellationToken);
                string? verdict = host.AsyncVerdict;
                if (verdict is null)
                {
                    return new BackendResult(true, "async", "Test262Error", "$DONE was never called");
                }

                if (verdict.Length > 0)
                {
                    return new BackendResult(true, "async", "Test262Error", verdict);
                }
            }

            return BackendResult.Passed;
        }
        catch (JsException e)
        {
            Diagnostic? r = e.Report;
            string kind = r?.Kind ?? "Error";
            string p = phase == "harness" || r is null || r.Code == "runtime" ? "runtime" : r.Code == "link" ? "resolution" : "parse";
            return new BackendResult(true, p, kind, (r?.Headline ?? e.Message) + (r is { Stack.Count: > 0 } ? " at " + r.Source.Name + ":" + r.Position.Line : ""));
        }
        catch (JsLimitException e)
        {
            return new BackendResult(true, "runtime", "Limit", e.Message) with { TimedOut = e.Kind is LimitKind.WallClock or LimitKind.Cancelled };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BackendResult(true, TimedOut: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new BackendResult(true, "runtime", e.GetType().Name, e.Message);
        }
    }

    /// <summary>Collects <c>print</c> output (for async tests) and serves module files next to the test.</summary>
    private sealed class TestHost(PreparedTest test) : DefaultHost(1)
    {
        public string? AsyncVerdict { get; private set; }

        public override void Print(string text)
        {
            if (text.StartsWith("Test262:AsyncTestComplete", StringComparison.Ordinal))
            {
                AsyncVerdict = "";
            }
            else if (text.StartsWith("Test262:AsyncTestFailure:", StringComparison.Ordinal))
            {
                AsyncVerdict = text["Test262:AsyncTestFailure:".Length..];
            }
        }

        public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
        {
            string baseDir = Path.GetDirectoryName(referrerId ?? test.FullPath)!;
            string full = Path.GetFullPath(Path.IsPathRooted(specifier) ? specifier : Path.Combine(baseDir, specifier));
            return File.Exists(full) ? new HostModule(full, File.ReadAllText(full)) : null;
        }
    }
}
