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
    /// <summary>
    /// Step budget: one unit per call, per loop back-edge and per unit of work in long-running built-ins. BigInt arithmetic is charged
    /// by operand size before it runs (a multiplication of two <c>n</c>-word numbers costs about <c>n</c>-squared over 400 steps; division,
    /// parsing and conversion to digits are charged the same way), so one operation that would exceed what is left ends the run before
    /// it starts, and a long multiplication or exponentiation polls cancellation and the wall clock as it goes. Division and the
    /// digit conversion of numbers under 2,048 words cannot be interrupted, so one of those is refused above 100 million steps
    /// (about a second and a half). Comparing, hashing or collating strings of 4,096 characters or more costs fuel in proportion to their length (equality and ordering about one step per 64 characters, a collation compare 12 steps per character), so a loop of long compares reaches the wall-clock poll. Default 4e9.
    /// </summary>
    public long Fuel { get; set; } = 4_000_000_000L;

    /// <summary>
    /// Allocation volume in bytes, counted cumulatively as bytes requested, not live bytes: nothing is ever given back. Before the engine
    /// allocates something whose size a script controls it adds the size to this total, and exceeding the limit ends the run with a
    /// <see cref="LimitKind.Memory"/> report instead of allocating. Charged: array element storage (16 bytes per slot of the new capacity
    /// each time it grows), typed-array and buffer bytes, collection entries, string characters (2 bytes each, when a rope is flattened or a
    /// string is built), lists gathered from iterators, and property storage past 32 properties per object. Also charged, at estimates of
    /// their real footprint: script and bound function objects (a closure about 220 bytes), environment records and call frames when a new
    /// one is made (frames and environments reused after a call are free), generator and async frames, arguments objects, promises,
    /// reactions and every queued job, global symbol registry entries, error objects with their captured stack, <c>WeakRef</c>, <c>Proxy</c>,
    /// RegExp compilation, Intl objects, the result of every BigInt operation on numbers over 64 words, the sort keys of a long collation compare while it runs, and every value a host function returns or a host task delivers (strings, BigInts, arrays and objects by size). A .NET <see cref="OutOfMemoryException"/> that reaches the engine ends the run as a memory limit too. Default 2 GiB.
    /// </summary>
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

    /// <summary>True for the top-level code of a script, module or eval, which a report shows as <c>&lt;top-level&gt;</c>.</summary>
    public bool IsTopLevel { get; init; }

    /// <summary>The bindings visible in this frame's function (or script) scope with a short preview of each value, present only in a report built by a traced replay; capped (see the JSON documentation).</summary>
    public IReadOnlyList<LocalInfo>? Locals { get; init; }

    /// <summary>The scope the frame was executing in, kept for the explainer of a traced replay.</summary>
    internal Env? ScopeEnv { get; init; }

    /// <summary>The realm of the frame.</summary>
    internal Realm? ScopeRealm { get; init; }

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
            _deadline = (host is LoggedHost lh0 ? lh0.Inner.MonotonicMs() : host.MonotonicMs()) + limits.WallClockMs;
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

    /// <summary>Records (or replays) everything the run takes from the host; null only inside a replay-free internal engine.</summary>
    internal Recorder? Recorder;

    /// <summary>The traced-replay state, or null for a normal run.</summary>
    internal TraceState? Trace;

    // Limit polling reads the real clock without logging it: how often it is read depends on fuel, not on the script.
    internal double PollClock() => Host is LoggedHost lh ? lh.Inner.MonotonicMs() : Host.MonotonicMs();

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

    /// <summary>Estimated bytes of one global-symbol-registry entry besides the key's characters: the symbol (with its description reference), the dictionary entry and the key string header.</summary>
    internal const int SymbolRegistryEntryBytes = 160;

    /// <summary>Estimated bytes one queued job holds: the queue slot, the delegate and the closure it captures.</summary>
    internal const int JobBytes = 128;

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

    /// <summary>The headline of a run that ended on a real out-of-memory failure; it names no figure, because the budget did not trip.</summary>
    internal const string MemoryExhaustedHeadline = "Execution ran out of memory: the process could not allocate more";

    /// <summary>
    /// The limit that stands for a .NET <see cref="OutOfMemoryException"/> reaching the engine: the host or the runtime could not
    /// satisfy an allocation the budget did not cover. Script cannot catch it, and the run ends like any other memory limit.
    /// </summary>
    internal JsLimitException MemoryExhausted()
    {
        Recorder?.Log.MarkNotReplayable("the process ran out of memory");
        return Limit(LimitKind.Memory, MemoryExhaustedHeadline);
    }

    /// <summary>Strings at least this long cost fuel in proportion to their length when compared or hashed.</summary>
    internal const int LongTextChars = 4096;

    /// <summary>Polls cancellation and the wall clock without spending fuel; long native kernels call it between pieces of work.</summary>
    internal void CheckInterrupts()
    {
        if (_describingLimit > 0)
        {
            return;
        }

        if (_cancel.IsCancellationRequested)
        {
            throw Limit(LimitKind.Cancelled, "Execution was cancelled");
        }

        if (_deadline > 0 && PollClock() > _deadline)
        {
            throw Limit(LimitKind.WallClock, "Execution exceeded the wall-clock limit of " + Limits.WallClockMs.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " ms");
        }
    }

    /// <summary>Largest amount of work (in steps) one kernel that cannot be interrupted midway may be charged; about a second and a half.</summary>
    internal const long MaxUninterruptibleWork = 100_000_000;

    // Roughly what one step costs in wall time, used to refuse a kernel that cannot finish before the deadline.
    private const double MsPerStep = 0.000015;

    /// <summary>
    /// Charges <paramref name="steps"/> of fuel for a native kernel whose cost grows with its operands, before it runs. A kernel that
    /// would exceed the remaining fuel never starts. One that cannot be interrupted midway (<paramref name="interruptible"/> false)
    /// is also refused when it is larger than <see cref="MaxUninterruptibleWork"/> or could not finish before the wall-clock deadline;
    /// an interruptible one polls <see cref="CheckInterrupts"/> itself between pieces.
    /// </summary>
    internal void ChargeWork(long steps, bool interruptible)
    {
        if (_describingLimit > 0 || steps <= 0)
        {
            return;
        }

        if (!interruptible)
        {
            if (steps > MaxUninterruptibleWork)
            {
                throw Limit(LimitKind.Fuel, "Execution exceeded the step limit: one BigInt operation needs about " + steps.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " steps and may take at most " + MaxUninterruptibleWork.ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
            }

            if (_deadline > 0 && steps > 65536 && PollClock() + (steps * MsPerStep) > _deadline)
            {
                throw Limit(LimitKind.WallClock, "Execution exceeded the wall-clock limit of " + Limits.WallClockMs.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " ms");
            }
        }

        Charge(steps);
    }

    private void Refill()
    {
        // Give back what the finished chunk did not use (negative overshoot is consumed).
        _fuelTotal += Fuel < 0 ? Fuel : 0;
        if (_cancel.IsCancellationRequested)
        {
            throw Limit(LimitKind.Cancelled, "Execution was cancelled");
        }

        if (_deadline > 0 && PollClock() > _deadline)
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
        if (_describingLimit > 0)
        {
            return;
        }

        _allocated += bytes;
        if (_allocated > Limits.MaxAllocatedBytes)
        {
            throw Limit(LimitKind.Memory, "Execution exceeded the allocation limit of " + Limits.MaxAllocatedBytes.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " bytes");
        }
    }

    // Describing a limit or explaining an error reads frame locals and values, which can flatten ropes. That work belongs to the report,
    // not to the script: it is not charged (so it cannot raise a limit from inside its own description, nor make a traced replay hit a
    // limit the recorded run did not), and it leaves no cache behind that would hide a later charge.
    private int _describingLimit;

    /// <summary>True while a report is being built; allocation is not charged and ropes flatten without caching.</summary>
    internal bool Diagnosing => _describingLimit > 0;

    /// <summary>Runs <paramref name="work"/> as diagnostics work (see <see cref="Diagnosing"/>).</summary>
    internal T Diagnose<T>(Func<T> work)
    {
        _describingLimit++;
        try
        {
            return work();
        }
        finally
        {
            _describingLimit--;
        }
    }

    internal JsLimitException Limit(LimitKind kind, string message)
    {
        _describingLimit++;
        try
        {
            return new(kind, message, CaptureFrames(20));
        }
        finally
        {
            _describingLimit--;
        }
    }

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

    /// <summary>Gives back bytes that were charged for something that has now gone (a job that ran, a reaction that fired); the total never goes below zero.</summary>
    internal void Release(long bytes)
    {
        if (_describingLimit > 0)
        {
            return;
        }

        _allocated = Math.Max(0, _allocated - bytes);
    }

    internal void EnqueueJob(Action job)
    {
        // 9.5 HostEnqueuePromiseJob: a queued job is retained until it runs, so a loop that queues without yielding grows without bound.
        Alloc(JobBytes);
        Jobs.Enqueue(job);
        Host.JobEnqueued();
    }

    /// <summary>Runs queued jobs until the queue is empty (HostEnqueuePromiseJob consumer).</summary>
    internal void RunJobs()
    {
        // ClearKeptObjects (9.10.3): the synchronous run that led here has ended, and so has each job below.
        Weak.ClearKept();
        while (true)
        {
            while (Jobs.Count > 0)
            {
                Action job = Jobs.Dequeue();
                Release(JobBytes); // the queue holds a job only until it runs
                Tick();
                job();
                Weak.ClearKept();
            }

            if (IdleHook is not null && IdleHook())
            {
                continue;
            }

            // A host-defined collection point at the end of the macrotask (9.10.4.1 note): without one a script that only
            // allocates WeakRef targets in a long run would pin them all. Collecting can queue cleanup jobs, which run next.
            if (Weak.HasTargets && Host.WantsWeakCollection())
            {
                Weak.Collect(this);
                if (Jobs.Count > 0)
                {
                    continue;
                }
            }

            break;
        }
    }
}
