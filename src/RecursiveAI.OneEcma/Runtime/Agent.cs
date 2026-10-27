using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Which resource limit ended a run.</summary>
public enum LimitKind
{
    /// <summary>The step budget (calls, loop back-edges, long-running built-ins) ran out.</summary>
    Fuel,

    /// <summary>Allocation volume passed the configured budget.</summary>
    Memory,

    /// <summary>The wall-clock deadline (read through the host clock) passed.</summary>
    WallClock,

    /// <summary>The cancellation token was signalled.</summary>
    Cancelled,

    /// <summary>Native code re-entered the interpreter too deeply (comparators, getters, proxy traps).</summary>
    NativeDepth,
}

/// <summary>Resource limits for one engine. Hitting fuel, memory, wall clock, cancellation or native depth ends the run and cannot be caught by script.</summary>
public sealed class EngineLimits
{
    /// <summary>Step budget: one unit per call, per loop back-edge and per unit of work in long-running built-ins. Default 4e9.</summary>
    public long Fuel { get; set; } = 4_000_000_000L;

    /// <summary>Allocation volume in bytes (see docs/runtime.md: bytes requested, not live bytes). Default 2 GiB.</summary>
    public long MaxAllocatedBytes { get; set; } = 2L << 30;

    /// <summary>Wall-clock budget in milliseconds measured with the host monotonic clock; zero or less means none. Default none.</summary>
    public double WallClockMs { get; set; }

    /// <summary>Script call depth at which a catchable RangeError is thrown. Default 10,000.</summary>
    public int MaxCallDepth { get; set; } = 10_000;

    /// <summary>Nesting of native-to-script re-entry before the run ends. Default 120.</summary>
    public int MaxNativeDepth { get; set; } = 120;

    /// <summary>Longest string in UTF-16 code units; longer results throw RangeError "Invalid string length". Default 536,870,888.</summary>
    public int MaxStringLength { get; set; } = 536_870_888;
}

/// <summary>Thrown when a resource limit ends the run. Script cannot catch it.</summary>
public sealed class JsLimitException : Exception
{
    internal JsLimitException(LimitKind kind, string message, IReadOnlyList<StackFrameInfo> frames)
        : base(message)
    {
        Kind = kind;
        Frames = frames;
    }

    /// <summary>The limit that was hit.</summary>
    public LimitKind Kind { get; }

    /// <summary>Script frames at the point of the limit, innermost first.</summary>
    public IReadOnlyList<StackFrameInfo> Frames { get; }

    /// <summary>The diagnostic describing the limit, set by the engine entry point.</summary>
    public Diagnostic? Report { get; internal set; }
}

/// <summary>A script frame in a captured stack.</summary>
/// <param name="Function">The function name, empty for top-level code.</param>
/// <param name="Source">The source the frame's code came from.</param>
/// <param name="Span">The span of the executing operation.</param>
/// <param name="IsAsync">True for a frame stitched from an async function.</param>
/// <param name="IsConstruct">True when the frame is a <c>new</c> call.</param>
public sealed record StackFrameInfo(string Function, SourceText Source, Span Span, bool IsAsync = false, bool IsConstruct = false)
{
    /// <summary>True for a .NET frame between script frames (a host method); then <see cref="Function"/> is the member name, <see cref="Source"/> names the file when known and <see cref="HostLine"/> is its line (0 when unknown).</summary>
    public bool IsHost { get; init; }

    /// <summary>The 1-based line of a .NET frame, or 0 when the runtime has no line information.</summary>
    public int HostLine { get; init; }

    /// <summary>The offset a stack trace reports for this frame (V8 points at a property name, an operator or a callee, not at the start of the expression); -1 when the span start is used.</summary>
    public int Pos { get; init; } = -1;

    /// <summary>The receiver's type name for a method call (the <c>Array</c> of <c>Array.map</c>), or null for a plain call.</summary>
    public string? TypeName { get; init; }

    /// <summary>True for a built-in function between script frames, shown as <c>at Array.map (&lt;anonymous&gt;)</c>.</summary>
    public bool IsNative { get; init; }

    /// <summary>The 1-based line and column V8 reports: <see cref="Pos"/> when known, else the span start.</summary>
    public LinePosition Position => Source.GetPosition(Pos >= 0 ? Pos : Span.Start);
}

/// <summary>
/// The state of one engine: host, limits, fuel, the job queue and the running frame. Entry points set the
/// current agent for the calling thread, so abstract operations can find the current realm without threading it
/// through every call.
/// </summary>
internal sealed class Agent
{
    [ThreadStatic]
    private static Agent? t_current;

    private long _fuelTotal;
    private double _deadline;
    private long _allocated;
    private CancellationToken _cancel;

    internal Agent(IJsHost host, EngineLimits limits)
    {
        Host = host;
        Limits = limits;
        _fuelTotal = limits.Fuel;
        if (limits.WallClockMs > 0)
        {
            _deadline = host.MonotonicMs() + limits.WallClockMs;
        }

        Fuel = 0;
        Refill();
    }

    internal static Agent Current => t_current ?? throw new InvalidOperationException("No engine is running on this thread.");

    internal static Agent? CurrentOrNull => t_current;

    internal IJsHost Host { get; }

    internal EngineLimits Limits { get; }

    internal Realm Realm = null!;

    /// <summary>The root of shapes for objects with a null prototype; per agent so engines on different threads share no mutable shape state.</summary>
    internal readonly Shape NullProtoRoot = new(null, default, 0, null, false);

    internal Frame? Frame;

    /// <summary>The built-in function whose body is running (null while script runs), with its receiver and whether it was called with <c>new</c>; stack traces show it as a frame.</summary>
    internal NativeFunction? CurrentNative;

    internal JsValue CurrentNativeThis;

    internal bool CurrentNativeNew;

    /// <summary>When set, new realms get the test-harness globals (<c>print</c>, <c>$262</c>).</summary>
    internal bool InstallTest262Host;

    /// <summary>Whether calls in tail position of strict code reuse the frame (<see cref="ScriptEngineOptions.ProperTailCalls"/>).</summary>
    internal bool ProperTailCalls;

    internal int CallDepth;

    internal int NativeDepth;

    /// <summary>Fuel left in the current chunk; the hot path decrements this.</summary>
    internal long Fuel;

    internal readonly Queue<Action> Jobs = new();

    /// <summary>Called when the job queue is empty; returns true if it made progress (queued jobs or settled promises), so the loop runs again. Atomics.waitAsync uses it to expire timeouts on virtual time.</summary>
    internal Func<bool>? IdleHook;

    /// <summary>Weak-reference bookkeeping (WeakRef, FinalizationRegistry).</summary>
    internal readonly WeakTracker Weak = new();

    /// <summary>Promises rejected while nobody was waiting on them, in rejection order; the entry point reports what is left after the jobs ran.</summary>
    internal readonly List<PromiseObject> UnhandledRejections = [];

    internal readonly Dictionary<string, JsSymbol> SymbolRegistry = new(StringComparer.Ordinal);

    internal long Allocated => _allocated;

    internal static Agent? Enter(Agent a)
    {
        Agent? old = t_current;
        t_current = a;
        return old;
    }

    internal static void Leave(Agent? old) => t_current = old;

    internal void SetCancellation(CancellationToken t) => _cancel = t;

    /// <summary>Spends one unit; at the end of a chunk polls the clock and cancellation.</summary>
    internal void Tick()
    {
        if (--Fuel < 0)
        {
            Refill();
        }
    }

    internal void Charge(long units)
    {
        Fuel -= units;
        if (Fuel < 0)
        {
            Refill();
        }
    }

    private void Refill()
    {
        // Give back what the finished chunk did not use (negative overshoot is consumed).
        _fuelTotal += Fuel < 0 ? Fuel : 0;
        if (_cancel.IsCancellationRequested)
        {
            throw Limit(LimitKind.Cancelled, "Execution was cancelled");
        }

        if (_deadline > 0 && Host.MonotonicMs() > _deadline)
        {
            throw Limit(LimitKind.WallClock, "Execution exceeded the wall-clock limit of " + Limits.WallClockMs.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " ms");
        }

        if (_fuelTotal <= 0)
        {
            throw Limit(LimitKind.Fuel, "Execution exceeded the step limit of " + Limits.Fuel.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " steps");
        }

        long take = Math.Min(_fuelTotal, 4096);
        _fuelTotal -= take;
        Fuel = take;
    }

    /// <summary>Accounts for an allocation of about <paramref name="bytes"/> bytes before it happens.</summary>
    internal void Alloc(long bytes)
    {
        _allocated += bytes;
        if (_allocated > Limits.MaxAllocatedBytes)
        {
            throw Limit(LimitKind.Memory, "Execution exceeded the allocation limit of " + Limits.MaxAllocatedBytes.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " bytes");
        }
    }

    internal JsLimitException Limit(LimitKind kind, string message) => new(kind, message, CaptureFrames(20));

    /// <summary>Walks the frame chain, innermost first.</summary>
    internal List<StackFrameInfo> CaptureFrames(int max, JsObject? skipUntil = null)
    {
        int room = skipUntil is null ? max : max + 16;
        int cut = -1;
        var list = new List<StackFrameInfo>();
        if (CurrentNative is { } top && !(CurrentNativeNew && HiddenConstructors.Contains(top.Slots[1].IsString ? top.Slots[1].AsString : string.Empty)) && NativeInfo(top, CurrentNativeThis, CurrentNativeNew) is { } topInfo)
        {
            list.Add(topInfo);
        }

        Frame? last = null;
        for (Frame? f = Frame; f is not null && list.Count < room; f = f.Caller)
        {
            list.Add(f.ToInfo());
            if (skipUntil is not null && ReferenceEquals(f.Func, skipUntil))
            {
                cut = list.Count - 1;
            }

            last = f;
            if (f.NativeCaller is { } n && list.Count < room && NativeInfo(n, f.NativeCallerThis, f.NativeCallerNew) is { } ni)
            {
                list.Add(ni);
            }
        }

        // Async stack: the awaiting async functions behind the bottom frame, as V8 shows them ("at async main").
        if (last is { Owner: PromiseObject p } && last.Code.IsAsync)
        {
            int guard = 0;
            while (p.AwaitedBy is { State: FrameState.Awaiting } y && list.Count < room && guard++ < 1000)
            {
                list.Add(y.ToInfo(async: true));
                if (y.Owner is not PromiseObject next)
                {
                    break;
                }

                p = next;
            }
        }

        if (cut >= 0)
        {
            list.RemoveRange(0, cut + 1);
        }

        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }

        return list;
    }

    // V8 leaves these constructors out of the trace of the error they raise or create.
    private static readonly HashSet<string> HiddenConstructors =
    [
        "Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "EvalError", "URIError", "AggregateError", "Array", "Proxy",
    ];

    private static StackFrameInfo? NativeInfo(NativeFunction fn, JsValue thisValue, bool isNew)
    {
        string name = fn.Slots[1].IsString ? fn.Slots[1].AsString : string.Empty;
        if (name.Length == 0)
        {
            return null!;
        }

        return new StackFrameInfo(name, Frame.NativeSource, new Span(0, 0), false, isNew) { IsNative = true, TypeName = isNew || thisValue.IsNullish ? null : Frame.TypeNameOf(thisValue) };
    }

    internal void EnqueueJob(Action job)
    {
        Jobs.Enqueue(job);
        Host.JobEnqueued();
    }

    /// <summary>Runs queued jobs until the queue is empty (HostEnqueuePromiseJob consumer).</summary>
    internal void RunJobs()
    {
        // ClearKeptObjects (9.10.3): the synchronous run that led here has ended, and so has each job below.
        Weak.ClearKept();
        do
        {
            while (Jobs.Count > 0)
            {
                Action job = Jobs.Dequeue();
                Tick();
                job();
                Weak.ClearKept();
            }
        }
        while (IdleHook is not null && IdleHook());
    }
}
