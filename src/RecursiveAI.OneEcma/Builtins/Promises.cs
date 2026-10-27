namespace RecursiveAI.OneEcma;

internal sealed class PromiseObject : JsObject
{
    internal int State; // 0 pending, 1 fulfilled, 2 rejected
    internal JsValue Result;
    internal List<Reaction>? Reactions;
    internal bool IsHandled;

    internal PromiseObject(JsObject proto)
        : base(proto)
    {
    }

    internal override string ClassName => "Promise";
}

internal sealed class Reaction
{
    internal PromiseCapability? Capability;
    internal JsValue OnFulfilled;
    internal JsValue OnRejected;
    internal Action<JsValue>? NativeFulfilled;
    internal Action<JsValue>? NativeRejected;
}

internal sealed class PromiseCapability
{
    internal JsObject Promise = null!;
    internal JsValue Resolve;
    internal JsValue Reject;
}

/// <summary>Promise abstract operations (ECMA-262 27.2.1 to 27.2.5).</summary>
internal static class Promises
{
    internal static PromiseObject NewPromise(Realm r) => new(r.PromisePrototype);

    /// <summary>CreateResolvingFunctions (27.2.1.3).</summary>
    internal static (JsObject Resolve, JsObject Reject) ResolvingFunctions(Realm r, PromiseObject p)
    {
        bool already = false;
        var resolve = r.Fn(string.Empty, 1, (t, a, nt) =>
        {
            if (already)
            {
                return default;
            }

            already = true;
            ResolveWith(p, Ops.Arg(a, 0));
            return default;
        });
        var reject = r.Fn(string.Empty, 1, (t, a, nt) =>
        {
            if (already)
            {
                return default;
            }

            already = true;
            Reject(p, Ops.Arg(a, 0));
            return default;
        });
        return (resolve, reject);
    }

    /// <summary>The promise resolve function body (27.2.1.3.2).</summary>
    internal static void ResolveWith(PromiseObject p, JsValue resolution)
    {
        if (p.State != 0)
        {
            return;
        }

        if (ReferenceEquals(resolution.Ref, p))
        {
            Reject(p, JsValue.FromObject(Agent.Current.Realm.NewError("TypeError", "Chaining cycle detected for promise #<Promise>")));
            return;
        }

        if (resolution.Ref is not JsObject ro)
        {
            Fulfill(p, resolution);
            return;
        }

        JsValue then;
        try
        {
            then = ro.Get("then");
        }
        catch (JsException e)
        {
            Reject(p, e.Value);
            return;
        }

        if (!Ops.IsCallable(then))
        {
            Fulfill(p, resolution);
            return;
        }

        Realm realm = Agent.Current.Realm;
        Agent.Current.EnqueueJob(() =>
        {
            // NewPromiseResolveThenableJob (27.2.2.2)
            (JsObject res, JsObject rej) = ResolvingFunctions(realm, p);
            try
            {
                ((JsObject)then.Ref!).Call(resolution, [JsValue.FromObject(res), JsValue.FromObject(rej)]);
            }
            catch (JsException e)
            {
                rej.Call(default, [e.Value]);
            }
        });
    }

    internal static void Fulfill(PromiseObject p, JsValue v)
    {
        List<Reaction>? rs = p.Reactions;
        p.Reactions = null;
        p.Result = v;
        p.State = 1;
        if (rs is not null)
        {
            foreach (Reaction r in rs)
            {
                EnqueueReaction(r, v, true);
            }
        }
    }

    internal static void Reject(PromiseObject p, JsValue reason)
    {
        if (p.State != 0)
        {
            return;
        }

        List<Reaction>? rs = p.Reactions;
        p.Reactions = null;
        p.Result = reason;
        p.State = 2;
        if (!p.IsHandled)
        {
            Agent.Current.Host.PromiseRejectionTracker(p, false);
        }

        if (rs is not null)
        {
            foreach (Reaction r in rs)
            {
                EnqueueReaction(r, reason, false);
            }
        }
    }

    private static void EnqueueReaction(Reaction r, JsValue arg, bool fulfilled)
    {
        Agent.Current.EnqueueJob(() => RunReaction(r, arg, fulfilled));
    }

    /// <summary>PromiseReactionJob (27.2.2.1).</summary>
    private static void RunReaction(Reaction r, JsValue arg, bool fulfilled)
    {
        if (fulfilled ? r.NativeFulfilled is not null : r.NativeRejected is not null)
        {
            (fulfilled ? r.NativeFulfilled : r.NativeRejected)!(arg);
            return;
        }

        JsValue handler = fulfilled ? r.OnFulfilled : r.OnRejected;
        PromiseCapability? cap = r.Capability;
        JsValue result;
        bool threw = false;
        if (handler.IsUndefined)
        {
            result = arg;
            threw = !fulfilled;
        }
        else
        {
            try
            {
                result = ((JsObject)handler.Ref!).Call(default, [arg]);
            }
            catch (JsException e)
            {
                result = e.Value;
                threw = true;
            }
        }

        if (cap is null)
        {
            return;
        }

        Ops.Call(threw ? cap.Reject : cap.Resolve, default, [result]);
    }

    /// <summary>PerformPromiseThen (27.2.5.4.1).</summary>
    internal static void PerformThen(PromiseObject p, JsValue onFulfilled, JsValue onRejected, PromiseCapability? cap)
    {
        var r = new Reaction
        {
            Capability = cap,
            OnFulfilled = Ops.IsCallable(onFulfilled) ? onFulfilled : default,
            OnRejected = Ops.IsCallable(onRejected) ? onRejected : default,
        };
        AddReaction(p, r);
    }

    /// <summary>Await's continuation: PerformPromiseThen with native handlers and no result capability.</summary>
    internal static void ThenNative(PromiseObject p, Action<JsValue> ok, Action<JsValue> err) =>
        AddReaction(p, new Reaction { NativeFulfilled = ok, NativeRejected = err });

    private static void AddReaction(PromiseObject p, Reaction r)
    {
        if (p.State == 0)
        {
            (p.Reactions ??= []).Add(r);
        }
        else if (p.State == 1)
        {
            EnqueueReaction(r, p.Result, true);
        }
        else
        {
            if (!p.IsHandled)
            {
                Agent.Current.Host.PromiseRejectionTracker(p, true);
            }

            EnqueueReaction(r, p.Result, false);
        }

        p.IsHandled = true;
    }

    /// <summary>NewPromiseCapability (27.2.1.5).</summary>
    internal static PromiseCapability NewCapability(JsValue c)
    {
        Realm r = Agent.Current.Realm;
        if (ReferenceEquals(c.Ref, r.PromiseConstructor))
        {
            PromiseObject p = NewPromise(r);
            (JsObject res, JsObject rej) = ResolvingFunctions(r, p);
            return new PromiseCapability { Promise = p, Resolve = JsValue.FromObject(res), Reject = JsValue.FromObject(rej) };
        }

        if (!Ops.IsConstructor(c))
        {
            throw Errors.Type(Ops.DisplayValue(c) + " is not a constructor");
        }

        var cap = new PromiseCapability();
        var executor = r.Fn(string.Empty, 2, (t, a, nt) =>
        {
            if (!cap.Resolve.IsUndefined || !cap.Reject.IsUndefined)
            {
                throw Errors.Type("Promise executor has already been invoked with non-undefined arguments");
            }

            cap.Resolve = Ops.Arg(a, 0);
            cap.Reject = Ops.Arg(a, 1);
            return default;
        });
        JsValue promise = ((JsObject)c.Ref!).Construct([JsValue.FromObject(executor)], (JsObject)c.Ref!);
        if (!Ops.IsCallable(cap.Resolve) || !Ops.IsCallable(cap.Reject))
        {
            throw Errors.Type("Promise resolve or reject function is not callable");
        }

        cap.Promise = (JsObject)promise.Ref!;
        return cap;
    }

    /// <summary>PromiseResolve (27.2.4.7.1).</summary>
    internal static JsObject PromiseResolve(JsValue c, JsValue x)
    {
        if (x.Ref is PromiseObject xp)
        {
            JsValue xc = xp.Get("constructor");
            if (Ops.SameValue(xc, c))
            {
                return xp;
            }
        }

        PromiseCapability cap = NewCapability(c);
        Ops.Call(cap.Resolve, default, [x]);
        return cap.Promise;
    }

    /// <summary>The abstract Await operation's promise step: PromiseResolve(%Promise%, value).</summary>
    internal static PromiseObject ForAwait(Realm r, JsValue v)
    {
        if (v.Ref is PromiseObject xp && ReferenceEquals(xp.Get("constructor").Ref, r.PromiseConstructor))
        {
            return xp;
        }

        PromiseObject p = NewPromise(r);
        ResolveWith(p, v);
        return p;
    }
}
