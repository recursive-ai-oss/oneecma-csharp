using System.Collections.Concurrent;

namespace RecursiveAI.OneEcma;

public sealed partial class ScriptEngine
{
    private JsRealm? _mainRealm;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly object _gate = new();
    private TaskCompletionSource? _waiter;
    private HostSynchronizationContext? _hostContext;
    private int _pendingHostOps;

    /// <summary>The engine's realm, as the low-level host API sees it: create values, define properties, call functions.</summary>
    public JsRealm MainRealm => _mainRealm ??= new JsRealm(this, _realm);

    /// <summary>Creates another realm in this engine: its own global object and its own copy of every built-in. Values pass between realms freely.</summary>
    /// <returns>The new realm.</returns>
    public JsRealm CreateRealm()
    {
        Realm r = Guard(() => Realm.Create(_agent), default);
        return new JsRealm(this, r);
    }

    /// <summary>Installs an exported .NET type in the main realm; shorthand for <c>MainRealm.Install(type)</c>.</summary>
    /// <param name="type">A generated binding's <c>JsType</c>.</param>
    public void Install(JsHostType type) => MainRealm.Install(type);

    internal void ChargeAllocation(long bytes) => _agent.Alloc(bytes);

    /// <summary>
    /// The synchronization context installed while a host method that returns a task runs: <c>await</c> continuations inside it come
    /// back to the engine's thread when <see cref="RunUntilIdle"/>, <see cref="Await"/> or their async versions pump the engine.
    /// </summary>
    internal SynchronizationContext HostContext => _hostContext ??= new HostSynchronizationContext(this);

    internal void Post(Action action)
    {
        _inbox.Enqueue(action);
        lock (_gate)
        {
            _waiter?.TrySetResult();
            Monitor.PulseAll(_gate);
        }
    }

    internal void BeginHostOperation() => _pendingHostOps++;

    internal void EndHostOperation() => _pendingHostOps--;

    /// <summary>Whether host operations (tasks returned to script as promises) are still running.</summary>
    public bool HasPendingHostOperations => _pendingHostOps > 0;

    /// <summary>
    /// Runs promise jobs and the continuations of host tasks until the engine is idle: no job is queued and no host task is
    /// running. Blocks the calling thread while it waits for a host task.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait and any running script.</param>
    /// <exception cref="JsException">A job threw, or a promise was rejected and never handled.</exception>
    /// <exception cref="JsLimitException">A resource limit ended the run.</exception>
    public void RunUntilIdle(CancellationToken cancellationToken = default) => Pump(null, true, cancellationToken);

    /// <summary>Like <see cref="RunUntilIdle"/>, but waits for host tasks without blocking the calling thread.</summary>
    /// <param name="cancellationToken">Cancels the wait and any running script.</param>
    public Task RunUntilIdleAsync(CancellationToken cancellationToken = default) => PumpAsync(null, cancellationToken);

    /// <summary>
    /// Waits for a promise to settle by running jobs and host-task continuations, and returns its value. A value that is not a promise
    /// is returned as is. Blocks the calling thread while a host task runs.
    /// </summary>
    /// <param name="value">The promise, or any value.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The fulfilment value.</returns>
    /// <exception cref="JsException">The promise was rejected (the reason is in <see cref="JsException.Value"/>), or it can never settle.</exception>
    public JsValue Await(JsValue value, CancellationToken cancellationToken = default)
    {
        if (value.Ref is not PromiseObject p)
        {
            return value;
        }

        p.IsHandled = true;
        Pump(() => p.State != 0, true, cancellationToken);
        return Settled(p);
    }

    /// <summary>Like <see cref="Await"/>, but does not block the calling thread while a host task runs.</summary>
    /// <param name="value">The promise, or any value.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The fulfilment value.</returns>
    public async Task<JsValue> AwaitAsync(JsValue value, CancellationToken cancellationToken = default)
    {
        if (value.Ref is not PromiseObject p)
        {
            return value;
        }

        p.IsHandled = true;
        await PumpAsync(() => p.State != 0, cancellationToken).ConfigureAwait(false);
        return Settled(p);
    }

    /// <summary>Runs a script, then waits for what it started: if the completion value is a promise (an async function was called) its result is returned; host tasks are awaited without blocking the thread.</summary>
    /// <param name="source">The script text.</param>
    /// <param name="fileName">The name reports use.</param>
    /// <param name="cancellationToken">Cancels the run and the wait.</param>
    /// <returns>The completion value, or the promise's fulfilment value.</returns>
    public async Task<JsValue> EvaluateAsync(string source, string fileName = "<anonymous>", CancellationToken cancellationToken = default)
    {
        JsValue v = Evaluate(source, fileName, cancellationToken);
        if (v.Ref is PromiseObject)
        {
            return await AwaitAsync(v, cancellationToken).ConfigureAwait(false);
        }

        await RunUntilIdleAsync(cancellationToken).ConfigureAwait(false);
        return v;
    }

    private JsValue Settled(PromiseObject p)
    {
        if (p.State == 1)
        {
            return p.Result;
        }

        if (p.State == 2)
        {
            JsValue reason = p.Result;
            return Guard<JsValue>(() => throw new JsException(reason), default);
        }

        throw Guard<JsException>(() => Errors.Make("Error", "The promise never settles: no job is queued and no host operation is running"), default);
    }

    private bool PumpStep(Func<bool>? done, CancellationToken token, out bool finished)
    {
        while (_inbox.TryDequeue(out Action? a))
        {
            Guard(() =>
            {
                a();
                return 0;
            }, token);
        }

        RunJobs(token);
        finished = done is not null && done();
        return finished || (_pendingHostOps == 0 && _inbox.IsEmpty);
    }

    private void Pump(Func<bool>? done, bool block, CancellationToken token)
    {
        while (!PumpStep(done, token, out _))
        {
            if (block)
            {
                WaitForPost(token);
            }
        }
    }

    private void WaitForPost(CancellationToken token)
    {
        lock (_gate)
        {
            if (_inbox.IsEmpty)
            {
                Monitor.Wait(_gate, 50);
            }
        }

        token.ThrowIfCancellationRequested();
    }

    private Task WaitForPostAsync(CancellationToken token)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (!_inbox.IsEmpty)
            {
                return Task.CompletedTask;
            }

            tcs = _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return tcs.Task.WaitAsync(token);
    }

    private async Task PumpAsync(Func<bool>? done, CancellationToken token)
    {
        while (!PumpStep(done, token, out _))
        {
            await WaitForPostAsync(token).ConfigureAwait(false);
        }
    }

    private sealed class HostSynchronizationContext(ScriptEngine engine) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => engine.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;
    }
}
