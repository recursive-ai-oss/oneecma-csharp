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

                case LogTag.AsyncBody:
                    {
                        r.Tag();
                        string source = r.Str();
                        string file = r.Str();
                        Finish(rec, result, () => EvaluateAsyncBody(source, file, token), options);
                        break;
                    }

                case LogTag.Callback:
                    {
                        // A function script gave to the host, called by .NET code outside any host function: an entry call.
                        r.Tag();
                        Finish(rec, result, () => Guard(() =>
                        {
                            rec.EntryCount++;
                            (JsValue value, JsException? thrown) = rec.RunRecordedCallback(_realm, nested: false);
                            TrackEntryPromise(rec.EntryCount - 1, value);
                            return thrown is null ? value : throw thrown;
                        }, token), options);
                        break;
                    }

                case LogTag.Await:
                    {
                        // The host marked the promise of an earlier entry call as handled before waiting for it (ScriptEngine.Await).
                        r.Tag();
                        int awaited = (int)r.Varint();
                        if (EntryPromise(awaited) is { } awaitedPromise)
                        {
                            awaitedPromise.IsHandled = true;
                        }

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
            T replayed = body();
            TrackEntryPromise(index, replayed);
            return replayed;
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
            TrackEntryPromise(index, value);
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

    /// <summary>
    /// Calls a script function on behalf of .NET code and logs the call so a replay re-enters the script at the same point. Inside a
    /// host function the call is written as a nested Callback event; made by itself (an event handler fired by the host, a
    /// continuation of an async host method) it is an entry call of its own. A function script never passed to a host function, or
    /// arguments that are not data, make the log not replayable.
    /// </summary>
    internal JsValue CallFromHost(Realm? realm, JsValue function, JsValue thisValue, JsValue[] args, CancellationToken token)
    {
        Recorder rec = _recorder;
        JsValue Body() => Ops.Call(function, thisValue, args);
        if (rec.Replaying)
        {
            // A live host function recomputed after a replay was handed the results of the callbacks it made.
            if (rec.Queued is { Count: > 0 } queue)
            {
                (JsValue value, JsException? thrown) = queue.Dequeue();
                return thrown is null ? value : throw thrown;
            }

            return Guard(Body, token, realm);
        }

        if (realm is not null && !ReferenceEquals(realm, _realm))
        {
            NoteNotReplayable("a script function was called in a second realm");
            return Guard(Body, token, realm);
        }

        if (!rec.Log.IsReplayable)
        {
            return Guard(Body, token, realm);
        }

        string reason = string.Empty;
        if (!InAgent(() => rec.CanDescribeCallback(function, thisValue, args, out reason)))
        {
            NoteNotReplayable(reason);
            return Guard(Body, token, realm);
        }

        bool alone = _active == 0 || (_inHostContinuation && !rec.InHostFn);
        if (!alone)
        {
            if (!rec.InHostFn)
            {
                NoteNotReplayable("a script function was called from .NET while script was running, outside the host function that received it");
                return Guard(Body, token, realm);
            }

            rec.WriteCallback(function, thisValue, args);
            rec.InHostFn = false;
            try
            {
                JsValue v = Body();
                rec.WriteCallbackEnd(false);
                return v;
            }
            catch (JsException e)
            {
                rec.WriteCallbackEnd(true);
                rec.LastCallbackError = e;
                throw;
            }
            finally
            {
                rec.InHostFn = true;
            }
        }

        int index = rec.EntryCount++;
        InAgent(() =>
        {
            rec.WriteCallback(function, thisValue, args);
            return 0;
        });
        bool continuation = _inHostContinuation;
        _inHostContinuation = false;
        try
        {
            JsValue value = Guard(Body, token, realm);
            EndEntry(null);
            TrackEntryPromise(index, value);
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
        finally
        {
            _inHostContinuation = continuation;
        }
    }

    /// <summary>Runs <paramref name="body"/> with the engine's agent current: as is inside a running call, in a guarded call otherwise.</summary>
    private T InAgent<T>(Func<T> body) => _active > 0 ? body() : Guard(body, default);

    private void EndEntry(string? failure)
    {
        _recorder.Log.Tag(LogTag.EntryEnd);
        _recorder.Log.Byte((byte)(failure is null ? 0 : 1));
        if (failure is not null)
        {
            _recorder.Log.Str(failure);
        }
    }

    private const int MaxTrackedPromises = 64;

    private readonly List<(int Index, PromiseObject Promise)> _entryPromises = [];

    /// <summary>Remembers the promise an entry call returned, so a later wait on it can be logged by entry number and explained.</summary>
    private void TrackEntryPromise<T>(int index, T value)
    {
        if (value is JsValue { Ref: PromiseObject p })
        {
            if (_entryPromises.Count >= MaxTrackedPromises)
            {
                _entryPromises.RemoveAt(0);
            }

            _entryPromises.Add((index, p));
        }
    }

    internal PromiseObject? EntryPromise(int index)
    {
        foreach ((int i, PromiseObject p) in _entryPromises)
        {
            if (i == index)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Called when the host starts waiting for a promise (<see cref="Await"/>): the wait marks the promise as handled, which changes
    /// what the job queue reports, so a recording notes it for the replay.
    /// </summary>
    private void NoteAwaited(PromiseObject promise)
    {
        if (_recorder.Replaying)
        {
            return;
        }

        foreach ((int i, PromiseObject p) in _entryPromises)
        {
            if (ReferenceEquals(p, promise))
            {
                _recorder.Log.Tag(LogTag.Await);
                _recorder.Log.Varint((ulong)i);
                return;
            }
        }
    }

    /// <summary>
    /// A rejection the host observed through <see cref="Await"/>: replays the whole log with tracing and swaps the traced report of the
    /// same rejection into <paramref name="e"/>, or notes why that was not possible.
    /// </summary>
    private void ExplainRejection(JsException e, PromiseObject promise)
    {
        if (!(_options?.ExplainErrors ?? true) || e.Report is not { Code: "runtime" } plain)
        {
            return;
        }

        int entry = -1;
        foreach ((int i, PromiseObject p) in _entryPromises)
        {
            if (ReferenceEquals(p, promise))
            {
                entry = i;
            }
        }

        if (entry < 0)
        {
            plain.Notes.Add("explanation unavailable: the rejected promise was not the result of an entry call, so the replay cannot find it");
            return;
        }

        ExplainBy(e, plain, -1, r =>
        {
            if (r.Engine.EntryPromise(entry) is not { State: 2 } rejected)
            {
                return null;
            }

            JsValue reason = rejected.Result;
            try
            {
                r.Engine.Guard<JsValue>(() => throw new JsException(reason), default);
            }
            catch (JsException replayed)
            {
                return replayed;
            }

            return null;
        });
    }

    /// <summary>Replays the run up to the failed entry with full tracing and, when it fails the same way, swaps in its richer report.</summary>
    private void Explain(JsException e, int index)
    {
        if (!(_options?.ExplainErrors ?? true) || e.Report is not { Code: "runtime" } plain)
        {
            return;
        }

        ExplainBy(e, plain, index, static r => r.Exception);
    }

    /// <summary>
    /// Replays the log (up to entry <paramref name="stopAfter"/>, -1 for all) and takes the exception <paramref name="pick"/> finds in the result.
    /// When it is the same failure its report replaces the plain one. When the explanation is lost for any reason, the plain report stays
    /// and gains a <c>note:</c> line naming the reason.
    /// </summary>
    private void ExplainBy(JsException e, Diagnostic plain, int stopAfter, Func<ReplayResult, JsException?> pick)
    {
        string lost;
        try
        {
            if (!_recorder.Log.IsReplayable)
            {
                lost = "the run cannot be replayed: " + _recorder.Log.NotReplayableReason;
            }
            else
            {
                ReplayResult r = Replay(_recorder.Log, new ReplayOptions
                {
                    Limits = _agent.Limits,
                    ThrowOnUnhandledRejection = _throwOnUnhandled,
                    ProperTailCalls = _agent.ProperTailCalls,
                    InstallTest262Host = _agent.InstallTest262Host,
                    Configure = _options?.Configure,
                    StopAfterEntry = stopAfter,
                });
                if (r.Divergence is not null)
                {
                    lost = "the replay diverged from the recorded run: " + r.Divergence;
                }
                else if (pick(r)?.Report is not { } rich)
                {
                    lost = "the replay did not fail again";
                }
                else if (rich.Headline != plain.Headline || rich.Kind != plain.Kind)
                {
                    lost = "the replay failed differently (" + rich.Kind + ": " + rich.Headline + ")";
                }
                else
                {
                    e.Report = rich;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The plain report stands: an explanation is a bonus and must never replace the error.
            lost = "the replay failed with " + ex.GetType().Name + ": " + ex.Message;
        }

        plain.Notes.Add("explanation unavailable: " + lost);
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
