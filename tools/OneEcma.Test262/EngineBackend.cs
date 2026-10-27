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

        if (test.Mode == SourceMode.Module)
        {
            return new BackendResult(false, NotImplemented: true, Message: "modules");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var engine = new ScriptEngine();
        try
        {
            if (test.IsAsync)
            {
                return new BackendResult(false, NotImplemented: true, Message: "async tests need host print");
            }

            // The single-script entry point cannot load separate harness files, so the harness and
            // the test run as one script; in strict mode the directive leads the whole text.
            var text = new System.Text.StringBuilder();
            foreach (HarnessScript h in test.Harness)
            {
                text.Append(h.Source).Append('\n');
            }

            text.Append(test.Source);
            string full = test.Mode == SourceMode.Strict ? "\"use strict\";\n" + text : text.ToString();
            engine.Evaluate(full, cts.Token);
            return BackendResult.Passed;
        }
        catch (NotSupportedException e)
        {
            return new BackendResult(false, NotImplemented: true, Message: e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BackendResult(true, TimedOut: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Until the library has a script-error type, every failure of Check is a syntax error.
            string phase = test.ParseOnly ? "parse" : "runtime";
            string type = test.ParseOnly ? "SyntaxError" : e.GetType().Name;
            return new BackendResult(true, phase, type, e.Message);
        }
    }
}
