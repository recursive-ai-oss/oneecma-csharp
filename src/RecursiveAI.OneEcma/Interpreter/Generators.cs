namespace RecursiveAI.OneEcma;

internal sealed class JsGenerator : JsObject
{
    internal Frame Frame;
    internal bool Running;

    internal JsGenerator(JsObject proto, Frame frame)
        : base(proto)
    {
        Frame = frame;
    }

    internal override string ClassName => "Generator";
}

internal sealed class AsyncRequest
{
    internal ResumeMode Mode;
    internal JsValue Value;
    internal PromiseCapability Cap = null!;
}

internal sealed class JsAsyncGenerator : JsObject
{
    internal Frame Frame;
    internal bool Executing;
    internal bool AwaitingReturn;
    internal Queue<AsyncRequest> Queue = new();

    internal JsAsyncGenerator(JsObject proto, Frame frame)
        : base(proto)
    {
        Frame = frame;
    }

    internal override string ClassName => "AsyncGenerator";
}

internal enum YieldStarKind
{
    Done,
    Yield,
    Await,
    Return,
}

internal readonly struct YieldStarResult
{
    internal YieldStarResult(YieldStarKind k, JsValue v)
    {
        Kind = k;
        Value = v;
    }

    internal YieldStarKind Kind { get; }

    internal JsValue Value { get; }
}

/// <summary>Generator objects, async generator queues and the await continuation (27.5, 27.6, 27.7).</summary>
internal static class Generators
{
    internal static JsValue Create(Frame f)
    {
        Realm r = f.Realm;
        bool isAsync = f.Code.IsAsync;
        JsObject fallback = isAsync ? r.AsyncGeneratorPrototype : r.GeneratorPrototype;
        JsObject proto = f.Func is { } fn && fn.Get("prototype").Ref is JsObject po ? po : fallback;
        if (isAsync)
        {
            var g = new JsAsyncGenerator(proto, f);
            f.Owner = g;
            return JsValue.FromObject(g);
        }

        var gen = new JsGenerator(proto, f);
        f.Owner = gen;
        return JsValue.FromObject(gen);
    }

    // ---- sync generators -----------------------------------------------------------------------------

    internal static JsValue Resume(JsValue thisValue, ResumeMode mode, JsValue value, string method)
    {
        if (thisValue.Ref is not JsGenerator g)
        {
            throw Errors.Type(method + " method called on incompatible receiver " + Ops.DisplayValue(thisValue));
        }

        if (g.Running)
        {
            throw Errors.Type("Generator is already running");
        }

        Frame f = g.Frame;
        if (f.State == FrameState.SuspendedStart && mode != ResumeMode.Next)
        {
            f.State = FrameState.Completed;
        }

        if (f.State == FrameState.Completed)
        {
            if (mode == ResumeMode.Throw)
            {
                throw new JsException(value);
            }

            return JsValue.FromObject(Ops.CreateIterResult(mode == ResumeMode.Return ? value : default, true));
        }

        f.Mode = mode;
        f.Sent = value;
        g.Running = true;
        try
        {
            JsValue v = Vm.RunEntry(Agent.Current, f);
            if (f.State == FrameState.SuspendedYield)
            {
                if (f.RawYield)
                {
                    f.RawYield = false;
                    return v;
                }

                return JsValue.FromObject(Ops.CreateIterResult(v, false));
            }

            return JsValue.FromObject(Ops.CreateIterResult(v, true));
        }
        catch
        {
            f.State = FrameState.Completed;
            throw;
        }
        finally
        {
            g.Running = false;
        }
    }

    // ---- yield* (15.5.5 YieldExpression : yield * AssignmentExpression) -----------------------------

    internal static YieldStarResult YieldStarStep(Frame f, JsValue[] st, ref int sp, bool isAsync)
    {
        var iter = (JsObject)st[sp - 3].Ref!;
        JsValue next = st[sp - 2];
        JsValue received = st[sp - 1];
        ResumeMode mode = f.Mode;
        JsValue inner;
        if (f.AwaitedInStar)
        {
            f.AwaitedInStar = false;
            if (f.Mode == ResumeMode.Throw)
            {
                // The awaited inner result was rejected: that is an abrupt completion of the yield* itself (15.5.5 step 7.a.vi).
                f.Mode = ResumeMode.Next;
                throw new JsException(received);
            }

            inner = received;
            mode = f.StarMode;
        }
        else
        {
            f.Mode = ResumeMode.Next;
            switch (mode)
            {
                case ResumeMode.Next:
                    inner = Ops.Call(next, JsValue.FromObject(iter), [received]);
                    break;
                case ResumeMode.Throw:
                    {
                        JsValue th = Ops.GetMethod(JsValue.FromObject(iter), "throw");
                        if (th.IsUndefined)
                        {
                            Ops.IteratorClose(new IteratorRecord(iter, next));
                            throw Errors.Type("The iterator does not provide a 'throw' method");
                        }

                        inner = ((JsObject)th.Ref!).Call(JsValue.FromObject(iter), [received]);
                        break;
                    }

                default:
                    {
                        JsValue rt = Ops.GetMethod(JsValue.FromObject(iter), "return");
                        if (rt.IsUndefined)
                        {
                            sp -= 3;
                            st[sp++] = received;
                            return new YieldStarResult(YieldStarKind.Return, received);
                        }

                        inner = ((JsObject)rt.Ref!).Call(JsValue.FromObject(iter), [received]);
                        break;
                    }
            }

            if (isAsync)
            {
                f.StarMode = mode;
                return new YieldStarResult(YieldStarKind.Await, inner);
            }
        }

        if (inner.Ref is not JsObject io)
        {
            throw Errors.Type("Iterator result " + Ops.DisplayValue(inner) + " is not an object");
        }

        bool done = Ops.ToBoolean(io.Get("done"));
        if (done)
        {
            JsValue value = io.Get("value");
            sp -= 3;
            st[sp++] = value;
            return new YieldStarResult(mode == ResumeMode.Return ? YieldStarKind.Return : YieldStarKind.Done, value);
        }

        if (isAsync)
        {
            return new YieldStarResult(YieldStarKind.Yield, io.Get("value"));
        }

        f.RawYield = true;
        return new YieldStarResult(YieldStarKind.Yield, inner);
    }

    // ---- await ------------------------------------------------------------------------------------------

    /// <summary>Await (6.2.3.1): suspend the frame until the promise settles, then resume it from a job.</summary>
    internal static void AwaitInFrame(Frame f, JsValue v)
    {
        Realm r = f.Realm;
        PromiseObject p;
        try
        {
            p = Promises.ForAwait(r, v);
        }
        catch (JsException e)
        {
            JsValue err = e.Value;
            Agent.Current.EnqueueJob(() => ResumeFrame(f, ResumeMode.Throw, err));
            return;
        }

        Promises.ThenNative(p, val => ResumeFrame(f, ResumeMode.Next, val), err => ResumeFrame(f, ResumeMode.Throw, err));
    }

    private static void ResumeFrame(Frame f, ResumeMode mode, JsValue value)
    {
        f.Mode = mode;
        f.Sent = value;
        Agent ag = Agent.Current;
        switch (f.Owner)
        {
            case JsAsyncGenerator g:
                RunAsyncGenerator(g);
                break;
            case ModuleRecord m:
                ModuleLoader.ResumeModule(m);
                break;
            default:
                // Async function: the promise settles inside Run.
                Vm.RunEntry(ag, f);
                break;
        }
    }

    // ---- async generators ---------------------------------------------------------------------------------

    internal static JsValue AsyncEnqueue(JsValue thisValue, ResumeMode mode, JsValue value)
    {
        Realm r = Agent.Current.Realm;
        PromiseCapability cap = Promises.NewCapability(JsValue.FromObject(r.PromiseConstructor));
        if (thisValue.Ref is not JsAsyncGenerator g)
        {
            Ops.Call(cap.Reject, default, [JsValue.FromObject(r.NewError("TypeError", "next method called on incompatible receiver " + Ops.DisplayValue(thisValue)))]);
            return JsValue.FromObject(cap.Promise);
        }

        g.Queue.Enqueue(new AsyncRequest { Mode = mode, Value = value, Cap = cap });
        if (!g.Executing && !g.AwaitingReturn)
        {
            Drain(g);
        }

        return JsValue.FromObject(cap.Promise);
    }

    private static void Drain(JsAsyncGenerator g)
    {
        while (g.Queue.Count > 0 && !g.Executing && !g.AwaitingReturn)
        {
            AsyncRequest req = g.Queue.Peek();
            Frame f = g.Frame;
            if (f.State == FrameState.SuspendedStart && req.Mode != ResumeMode.Next)
            {
                f.State = FrameState.Completed;
            }

            if (f.State == FrameState.Completed)
            {
                if (req.Mode == ResumeMode.Return)
                {
                    g.AwaitingReturn = true;
                    PromiseObject p;
                    try
                    {
                        p = Promises.ForAwait(f.Realm, req.Value);
                    }
                    catch (JsException e)
                    {
                        g.AwaitingReturn = false;
                        g.Queue.Dequeue();
                        Ops.Call(req.Cap.Reject, default, [e.Value]);
                        continue;
                    }

                    Promises.ThenNative(
                        p,
                        v =>
                        {
                            g.AwaitingReturn = false;
                            g.Queue.Dequeue();
                            Ops.Call(req.Cap.Resolve, default, [JsValue.FromObject(Ops.CreateIterResult(v, true))]);
                            Drain(g);
                        },
                        e =>
                        {
                            g.AwaitingReturn = false;
                            g.Queue.Dequeue();
                            Ops.Call(req.Cap.Reject, default, [e]);
                            Drain(g);
                        });
                    return;
                }

                g.Queue.Dequeue();
                if (req.Mode == ResumeMode.Throw)
                {
                    Ops.Call(req.Cap.Reject, default, [req.Value]);
                }
                else
                {
                    Ops.Call(req.Cap.Resolve, default, [JsValue.FromObject(Ops.CreateIterResult(default, true))]);
                }

                continue;
            }

            f.Mode = req.Mode;
            f.Sent = req.Value;
            RunAsyncGenerator(g);
            return;
        }
    }

    private static void RunAsyncGenerator(JsAsyncGenerator g)
    {
        Frame f = g.Frame;
        g.Executing = true;
        AsyncRequest req = g.Queue.Peek();
        JsValue v;
        try
        {
            v = Vm.RunEntry(Agent.Current, f);
        }
        catch (JsException e)
        {
            f.State = FrameState.Completed;
            g.Executing = false;
            g.Queue.Dequeue();
            Ops.Call(req.Cap.Reject, default, [e.Value]);
            Drain(g);
            return;
        }

        if (f.State == FrameState.Awaiting)
        {
            return;
        }

        g.Executing = false;
        g.Queue.Dequeue();
        Ops.Call(req.Cap.Resolve, default, [JsValue.FromObject(Ops.CreateIterResult(v, f.State != FrameState.SuspendedYield))]);
        Drain(g);
    }
}
