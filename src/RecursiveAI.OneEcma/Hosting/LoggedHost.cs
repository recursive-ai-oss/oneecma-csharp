using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

/// <summary>The state of recording or replaying one run: the log, the reader when replaying, and the host calls and tasks in flight.</summary>
internal sealed class Recorder
{
    internal Recorder(RecordLog log, LogReader? reader)
    {
        Log = log;
        Reader = reader;
    }

    internal RecordLog Log { get; }

    internal LogReader? Reader { get; }

    internal bool Replaying => Reader is not null;

    /// <summary>Number of entry calls (scripts evaluated, modules loaded, job drains) started so far; the first is 0.</summary>
    internal int EntryCount;

    internal long NextCallId;

    /// <summary>The id of the host call being made, so a task bridge started inside it can name itself in the log.</summary>
    internal long CurrentCall = -1;

    internal bool TaskStarted;

    /// <summary>
    /// Log writes that belong after the entry of the host call being recorded (the settlement of a task that was already complete);
    /// null outside a recorded host call.
    /// </summary>
    internal List<Action>? Deferred;

    /// <summary>Runs <paramref name="write"/> now, or after the current host call's entry when one is being recorded.</summary>
    internal void Emit(Action write)
    {
        if (Deferred is not null)
        {
            Deferred.Add(write);
        }
        else
        {
            write();
        }
    }

    /// <summary>Promises of replayed host tasks that have not settled, by call id.</summary>
    internal readonly Dictionary<long, PromiseObject> PendingTasks = [];

    /// <summary>The host member and the script frames at the call of each pending replayed task, so a failure of the task is reported at the call, as in the recording.</summary>
    internal readonly Dictionary<long, (string Member, List<StackFrameInfo> Frames)> PendingSites = [];

    internal string? Divergence;

    internal Exception Diverged(string expected, LogTag actual)
    {
        Divergence ??= "the replay asked for " + expected + " but the log has " + actual;
        return new ReplayDivergedException(Divergence);
    }

    // ---- writing ---------------------------------------------------------------------------------------

    private double _lastClock;

    internal void WriteClock(double v)
    {
        Log.Tag(LogTag.Clock);
        if (v == Math.Floor(v) && Math.Abs(v) < 9e15)
        {
            Log.Byte(0);
            Log.Zigzag((long)(v - _lastClock));
            _lastClock = v;
        }
        else
        {
            Log.Byte(1);
            Log.Double(v);
        }
    }

    internal void WriteHostCallValue(string member, JsValue result)
    {
        Log.Tag(LogTag.HostCall);
        Log.Str(member);
        if (TaskStarted)
        {
            Log.Byte(4);
            Log.Varint((ulong)CurrentCall);
        }
        else if (ValueCodec.CanEncode(result))
        {
            Log.Byte(0);
            ValueCodec.Encode(Log, result);
        }
        else
        {
            // A function, a host object or another value that is not data: replay calls the host function again.
            Log.Byte(3);
        }
    }

    internal void WriteHostThrow(string member, JsValue thrown)
    {
        Log.Tag(LogTag.HostCall);
        Log.Str(member);
        if (ValueCodec.CanEncode(thrown))
        {
            Log.Byte(2);
            ValueCodec.Encode(Log, thrown);
        }
        else
        {
            Log.Byte(3);
        }
    }

    internal void WriteDotNet(string tag, string member, Exception e, long settleId = -1)
    {
        Log.Tag(settleId >= 0 ? LogTag.Settle : LogTag.HostCall);
        if (settleId >= 0)
        {
            Log.Varint((ulong)settleId);
        }
        else
        {
            Log.Str(member);
        }

        Log.Byte(1);
        Log.Str(e.GetType().Name);
        Log.Str(e.GetType().FullName ?? e.GetType().Name);
        Log.Str(e.Message);
        List<StackFrameInfo> frames = HostErrors.ParseFrames(e);
        Log.Varint((ulong)frames.Count);
        foreach (StackFrameInfo f in frames)
        {
            Log.Str(f.Function);
            Log.Str(f.Source.Name);
            Log.Varint((ulong)f.HostLine);
        }
    }

    internal void LogSettlement(long callId, JsPromise promise)
    {
        if (Replaying || callId < 0)
        {
            return;
        }

        Log.Tag(LogTag.Settle);
        Log.Varint((ulong)callId);
        JsPromiseState state = promise.State;
        if (state != JsPromiseState.Pending && ValueCodec.CanEncode(promise.Result))
        {
            Log.Byte((byte)(state == JsPromiseState.Fulfilled ? 0 : 2));
            ValueCodec.Encode(Log, promise.Result);
        }
        else
        {
            Log.MarkNotReplayable("a host task settled with a value that is not data, or with another promise");
            Log.Byte(3);
        }
    }

    // ---- reading ---------------------------------------------------------------------------------------

    /// <summary>Settles the replayed host tasks whose settlement is next in the log, in recorded order.</summary>
    internal void SettleReady()
    {
        LogReader r = Reader!;
        while (r.Peek == LogTag.Settle && PendingTasks.TryGetValue(r.PeekVarintAfterTag(), out PromiseObject? p))
        {
            r.Tag();
            long id = (long)r.Varint();
            PendingTasks.Remove(id);
            PendingSites.Remove(id, out (string Member, List<StackFrameInfo> Frames) site);
            try
            {
                JsValue v = ReadOutcome(r, site.Member ?? "task", Agent.Current.Realm, out bool opaque, out _, site.Frames);
                if (opaque)
                {
                    throw Diverged("a settled task value", LogTag.Settle);
                }

                Promises.ResolveWith(p, v);
            }
            catch (JsException je)
            {
                Promises.Reject(p, je.Value);
            }
        }
    }

    internal LogReader Expect(LogTag tag)
    {
        LogReader r = Reader!;
        if (r.Peek != tag)
        {
            throw Diverged(tag.ToString(), r.Peek);
        }

        r.Tag();
        return r;
    }

    /// <summary>Reads the outcome of a host call or task settlement and applies it: returns the value, throws the recorded error, or reports an opaque result.</summary>
    internal static JsValue ReadOutcome(LogReader r, string member, Realm realm, out bool opaque, out long taskId, IReadOnlyList<StackFrameInfo>? scriptFrames = null)
    {
        opaque = false;
        taskId = -1;
        switch (r.Next())
        {
            case 0:
                return ValueCodec.Decode(r, realm);
            case 1:
                {
                    string typeName = r.Str();
                    string fullName = r.Str();
                    string message = r.Str();
                    var frames = new List<StackFrameInfo>();
                    for (ulong n = r.Varint(); n > 0; n--)
                    {
                        string fn = r.Str();
                        string file = r.Str();
                        int line = (int)r.Varint();
                        frames.Add(new StackFrameInfo(fn, new SourceText(string.Empty, file), new Span(0, 0)) { IsHost = true, HostLine = line });
                    }

                    throw HostErrors.FromParts(typeName, fullName, message, frames, null, member, scriptFrames);
                }

            case 2:
                throw new JsException(ValueCodec.Decode(r, realm));
            case 3:
                opaque = true;
                return default;
            case 4:
                taskId = (long)r.Varint();
                return default;
            default:
                throw new FormatException("Bad host call outcome in the record log.");
        }
    }
}

/// <summary>Raised when a replay asks for something the log does not have next: the code or the host differs from the recorded run.</summary>
internal sealed class ReplayDivergedException(string message) : Exception(message);

/// <summary>
/// Sits between the engine and the real host. When recording it forwards every call and logs the answer; when replaying it
/// answers from the log and never touches the real host.
/// </summary>
internal sealed class LoggedHost(IJsHost inner, Recorder rec) : IJsHost
{
    private string? _locale;
    private string? _zone;

    internal IJsHost Inner => inner;

    public double CurrentTimeMs()
    {
        if (rec.Replaying)
        {
            LogReader r = rec.Expect(LogTag.Clock);
            return r.Next() == 0 ? r.Clock() : r.Double();
        }

        double v = inner.CurrentTimeMs();
        rec.WriteClock(v);
        return v;
    }

    public double MonotonicMs()
    {
        if (rec.Replaying)
        {
            return rec.Expect(LogTag.Mono).Double();
        }

        double v = inner.MonotonicMs();
        rec.Log.Tag(LogTag.Mono);
        rec.Log.Double(v);
        return v;
    }

    public double NextRandom()
    {
        if (rec.Replaying)
        {
            return rec.Expect(LogTag.Random).Double();
        }

        double v = inner.NextRandom();
        rec.Log.Tag(LogTag.Random);
        rec.Log.Double(v);
        return v;
    }

    public string DefaultLocale => _locale ??= Cached(LogTag.Locale, () => inner.DefaultLocale);

    public string TimeZoneId => _zone ??= Cached(LogTag.ZoneId, () => inner.TimeZoneId);

    private string Cached(LogTag tag, Func<string> read)
    {
        if (rec.Replaying)
        {
            return rec.Expect(tag).Str();
        }

        string s = read();
        rec.Log.Tag(tag);
        rec.Log.Str(s);
        return s;
    }

    public bool TryGetTimeZoneOffset(string zoneId, double epochMs, out int offsetMs, out bool isDaylight)
    {
        if (rec.Replaying)
        {
            LogReader r = rec.Expect(LogTag.ZoneOffset);
            offsetMs = (int)r.Zigzag();
            byte flags = r.Next();
            isDaylight = (flags & 2) != 0;
            return (flags & 1) != 0;
        }

        bool known = inner.TryGetTimeZoneOffset(zoneId, epochMs, out offsetMs, out isDaylight);
        rec.Log.Tag(LogTag.ZoneOffset);
        rec.Log.Zigzag(offsetMs);
        rec.Log.Byte((byte)((known ? 1 : 0) | (isDaylight ? 2 : 0)));
        return known;
    }

    public HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
    {
        if (rec.Replaying)
        {
            LogReader r = rec.Expect(LogTag.Resolve);
            return r.Next() == 1 ? new HostModule(r.Str(), r.Str()) : null;
        }

        HostModule? m = inner.ResolveModule(specifier, referrerId, attributes);
        rec.Log.Tag(LogTag.Resolve);
        rec.Log.Byte((byte)(m is null ? 0 : 1));
        if (m is not null)
        {
            rec.Log.Str(m.Id);
            rec.Log.Str(m.Source);
        }

        return m;
    }

    public void Print(string text)
    {
        if (!rec.Replaying)
        {
            inner.Print(text);
        }
    }

    public void JobEnqueued()
    {
        if (!rec.Replaying)
        {
            inner.JobEnqueued();
        }
    }

    public void PromiseRejectionTracker(JsObject promise, bool handled)
    {
        if (!rec.Replaying)
        {
            inner.PromiseRejectionTracker(promise, handled);
        }
    }

    public void InitializeImportMeta(string moduleId, JsObject meta)
    {
        if (!rec.Replaying)
        {
            inner.InitializeImportMeta(moduleId, meta);
        }
    }

    public void WeakTargetsCollected(IReadOnlyList<long> ids)
    {
        if (rec.Replaying)
        {
            return;
        }

        rec.Log.Tag(LogTag.Weak);
        rec.Log.Varint((ulong)ids.Count);
        foreach (long id in ids)
        {
            rec.Log.Varint((ulong)id);
        }

        inner.WeakTargetsCollected(ids);
    }

    public IReadOnlyList<long>? ReplayWeakCollection()
    {
        if (!rec.Replaying)
        {
            return inner.ReplayWeakCollection();
        }

        LogReader r = rec.Expect(LogTag.Weak);
        var ids = new List<long>();
        for (ulong n = r.Varint(); n > 0; n--)
        {
            ids.Add((long)r.Varint());
        }

        return ids;
    }

    public bool CanBlock => inner.CanBlock;
}
