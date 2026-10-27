using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma;

/// <summary>Settings for <see cref="ScriptEngine.Replay"/>.</summary>
public sealed class ReplayOptions
{
    /// <summary>Resource limits for the replay; default: the engine defaults. Use the limits of the recorded run for an exact reproduction.</summary>
    public EngineLimits? Limits { get; set; }

    /// <summary>The recorded run's setting of <see cref="ScriptEngineOptions.ThrowOnUnhandledRejection"/>. Default true.</summary>
    public bool ThrowOnUnhandledRejection { get; set; } = true;

    /// <summary>The recorded run's setting of <see cref="ScriptEngineOptions.ProperTailCalls"/>.</summary>
    public bool ProperTailCalls { get; set; }

    /// <summary>The recorded run's setting of <see cref="ScriptEngineOptions.InstallTest262Host"/>.</summary>
    public bool InstallTest262Host { get; set; }

    /// <summary>The setup the recorded run did in <see cref="ScriptEngineOptions.Configure"/>; global functions made with <see cref="ScriptEngine.DefineGlobalFunction"/> are rebuilt from the log without it.</summary>
    public Action<ScriptEngine>? Configure { get; set; }

    /// <summary>When true (the default) the replay runs with full tracing, so the reports it produces carry provenance, suggestions and locals.</summary>
    public bool Trace { get; set; } = true;

    /// <summary>Stop after this entry call (0 is the first script evaluated); -1 (the default) replays the whole log.</summary>
    public int StopAfterEntry { get; set; } = -1;
}

/// <summary>What a replay produced.</summary>
public sealed class ReplayResult
{
    /// <summary>The completion value of the last entry call that finished without error; undefined if none did.</summary>
    public JsValue Value { get; internal set; }

    /// <summary>The error the last entry call threw, or null.</summary>
    public JsException? Exception { get; internal set; }

    /// <summary>The report of <see cref="Exception"/> (also in <c>Exception.Report</c>), or null.</summary>
    public Diagnostic? Report => Exception?.Report;

    /// <summary>The limit the last entry call hit, or null.</summary>
    public JsLimitException? Limit { get; internal set; }

    /// <summary>The number of entry calls replayed.</summary>
    public int Entries { get; internal set; }

    /// <summary>Null when the replay followed the log; otherwise what differed (the code, the host functions or the engine version changed since the recording).</summary>
    public string? Divergence { get; internal set; }

    /// <summary>The engine the replay ran in, for reading the final state.</summary>
    public ScriptEngine Engine { get; internal set; } = null!;
}

public sealed partial class ScriptEngine
{
    /// <summary>
    /// Re-runs a recorded run in a fresh engine, answering the clock, random numbers, host calls and modules from the log alone.
    /// The values, the exceptions and the report bytes come out the same as in the recording.
    /// </summary>
    /// <param name="log">A log from <see cref="RecordLog"/>.</param>
    /// <param name="options">Limits and setup; <see langword="null"/> means defaults.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>What the replay produced; <see cref="ReplayResult.Divergence"/> says if it left the log.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The log is not replayable (<see cref="RecordLog.NotReplayableReason"/>).</exception>
    public static ReplayResult Replay(RecordLog log, ReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!log.IsReplayable)
        {
            throw new InvalidOperationException("The record log cannot be replayed: " + log.NotReplayableReason + ".");
        }

        options ??= new ReplayOptions();
        var rec = new Recorder(log, new LogReader(log));
        var result = new ReplayResult();
        try
        {
            var engine = new ScriptEngine(new ScriptEngineOptions
            {
                Limits = options.Limits,
                ThrowOnUnhandledRejection = options.ThrowOnUnhandledRejection,
                ProperTailCalls = options.ProperTailCalls,
                InstallTest262Host = options.InstallTest262Host,
                Configure = options.Configure,
                ReplayRecorder = rec,
                TraceReplay = options.Trace,
                ExplainErrors = false,
            });
            result.Engine = engine;
            engine.Drive(rec, options, result, cancellationToken);
        }
        catch (ReplayDivergedException e)
        {
            result.Divergence = e.Message;
        }
        catch (FormatException e)
        {
            result.Divergence = "the log is damaged: " + e.Message;
        }

        result.Divergence ??= rec.Divergence;
        return result;
    }

    private void Drive(Recorder rec, ReplayOptions options, ReplayResult result, CancellationToken token)
    {
        LogReader r = rec.Reader!;
        while (r.HasMore)
        {
            switch (r.Peek)
            {
                case LogTag.DefineFn:
                    r.Tag();
                    string fnName = r.Str();
                    int fnLength = (int)r.Varint();
                    DefineReplayStub(fnName, fnLength);
                    break;
                case LogTag.Settle:
                    Guard(() =>
                    {
                        rec.SettleReady();
                        return 0;
                    }, token);
                    if (r.Peek == LogTag.Settle)
                    {
                        throw rec.Diverged("an entry call", LogTag.Settle);
                    }

                    break;
                case LogTag.Eval:
                    {
                        r.Tag();
                        string source = r.Str();
                        string file = r.Str();
                        Finish(rec, result, () => Evaluate(source, file, token), options);
                        break;
                    }

                case LogTag.Module:
                    {
                        r.Tag();
                        string spec = r.Str();
                        Finish(rec, result, () => EvaluateModule(spec, token), options);
                        break;
                    }

                case LogTag.Jobs:
                    r.Tag();
                    Finish(rec, result, () =>
                    {
                        RunJobs(token);
                        return default;
                    }, options);
                    break;
                default:
                    throw rec.Diverged("the next entry call", r.Peek);
            }

            if (options.StopAfterEntry >= 0 && rec.EntryCount > options.StopAfterEntry)
            {
                return;
            }
        }
    }

    private static void Finish(Recorder rec, ReplayResult result, Func<JsValue> run, ReplayOptions options)
    {
        result.Exception = null;
        result.Limit = null;
        try
        {
            result.Value = run();
        }
        catch (JsException e)
        {
            result.Exception = e;
        }
        catch (JsLimitException e)
        {
            result.Limit = e;
        }

        result.Entries = rec.EntryCount;
        LogReader end = rec.Expect(LogTag.EntryEnd);
        bool failed = end.Next() == 1;
        string headline = failed ? end.Str() : string.Empty;
        bool gotFailure = result.Exception is not null || result.Limit is not null;
        string? gotHeadline = result.Exception?.Report?.Headline ?? result.Limit?.Message;
        if (failed != gotFailure || (failed && headline != gotHeadline))
        {
            rec.Divergence ??= failed
                ? "the recorded call failed with \"" + headline + "\" but the replay " + (gotFailure ? "failed with \"" + gotHeadline + "\"" : "succeeded")
                : "the recorded call succeeded but the replay failed with \"" + gotHeadline + "\"";
            throw new ReplayDivergedException(rec.Divergence);
        }
    }

    private void DefineReplayStub(string name, int length)
    {
        var f = _realm.Fn(name, length, HostCalls.Wrap((_, _) => throw new InvalidOperationException("The replay needs the host function '" + name + "' (its result was not recorded as data); define it in ScriptEngineOptions.Configure."), name));
        _realm.Global.DefineDirect(name, JsValue.FromObject(f), PropFlags.Hidden);
    }

    // ---- recording the entry calls -------------------------------------------------------------------

    /// <summary>Runs an entry call: logs it when it is the outermost call of a recording engine, and explains an uncaught error by replay.</summary>
    private T Entry<T>(LogTag tag, string? a, string? b, Func<T> body)
    {
        if (_active > 0)
        {
            return body();
        }

        int index = _recorder.EntryCount++;
        if (_recorder.Replaying)
        {
            return body();
        }

        _recorder.Log.Tag(tag);
        if (a is not null)
        {
            _recorder.Log.Str(a);
        }

        if (b is not null)
        {
            _recorder.Log.Str(b);
        }

        try
        {
            T value = body();
            EndEntry(null);
            return value;
        }
        catch (JsException e)
        {
            EndEntry(e.Report?.Headline ?? e.Message);
            Explain(e, index);
            throw;
        }
        catch (JsLimitException e)
        {
            EndEntry(e.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            EndEntry("cancelled");
            throw;
        }
    }

    private void EndEntry(string? failure)
    {
        _recorder.Log.Tag(LogTag.EntryEnd);
        _recorder.Log.Byte((byte)(failure is null ? 0 : 1));
        if (failure is not null)
        {
            _recorder.Log.Str(failure);
        }
    }

    /// <summary>Replays the run up to the failed entry with full tracing and, when it fails the same way, swaps in its richer report.</summary>
    private void Explain(JsException e, int index)
    {
        if (!(_options?.ExplainErrors ?? true) || !_recorder.Log.IsReplayable || e.Report is not { Code: "runtime" } plain)
        {
            return;
        }

        try
        {
            ReplayResult r = Replay(_recorder.Log, new ReplayOptions
            {
                Limits = _agent.Limits,
                ThrowOnUnhandledRejection = _throwOnUnhandled,
                ProperTailCalls = _agent.ProperTailCalls,
                InstallTest262Host = _agent.InstallTest262Host,
                Configure = _options?.Configure,
                StopAfterEntry = index,
            });
            if (r.Divergence is null && r.Exception?.Report is { } rich && rich.Headline == plain.Headline && rich.Kind == plain.Kind)
            {
                e.Report = rich;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The plain report stands: an explanation is a bonus and must never replace the error.
        }
    }

    internal JsValue EvaluateFrom(Realm realm, string source, string fileName, CancellationToken token)
    {
        if (ReferenceEquals(realm, _realm))
        {
            return Evaluate(source, fileName, token);
        }

        NoteNotReplayable("a script was evaluated in a second realm");
        return EvaluateIn(realm, source, fileName, token);
    }

    internal void NoteNotReplayable(string reason)
    {
        if (!_recorder.Replaying)
        {
            _recorder.Log.MarkNotReplayable(reason);
        }
    }
}
