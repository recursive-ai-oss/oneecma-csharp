using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    private sealed class StringIteratorObject : JsObject
    {
        internal string? Text;
        internal int Pos;

        internal StringIteratorObject(JsObject proto)
            : base(proto)
        {
        }
    }

    // ---- Promise ------------------------------------------------------------------------------------------------------------

    private static void SetupPromise(Realm r)
    {
        var proto = r.NewObject();
        r.PromisePrototype = proto;
        NativeFunction ctor = r.Ctor("Promise", 1, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Promise constructor cannot be invoked without 'new'");
            }

            if (!Ops.IsCallable(A(a, 0)))
            {
                throw Errors.Type("Promise resolver " + Ops.DisplayValue(A(a, 0)) + " is not a function");
            }

            var p = new PromiseObject(Ops.GetPrototypeFromConstructor(nt, x => x.PromisePrototype));
            (JsObject res, JsObject rej) = Promises.ResolvingFunctions(r, p);
            try
            {
                Ops.Call(A(a, 0), default, [JsValue.FromObject(res), JsValue.FromObject(rej)]);
            }
            catch (JsException e)
            {
                rej.Call(default, [e.Value]);
            }

            return JsValue.FromObject(p);
        }, proto);
        r.PromiseConstructor = ctor;
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Promise"), PropFlags.Configurable);
        r.Method(proto, "then", 2, (t, a, nt) =>
        {
            if (t.Ref is not PromiseObject p)
            {
                throw Errors.Type("Method Promise.prototype.then called on incompatible receiver " + Ops.DisplayValue(t));
            }

            JsObject c = Ops.SpeciesConstructor(p, ctor);
            PromiseCapability cap = Promises.NewCapability(JsValue.FromObject(c));
            Promises.PerformThen(p, A(a, 0), A(a, 1), cap);
            return JsValue.FromObject(cap.Promise);
        });
        r.Method(proto, "catch", 1, (t, a, nt) => Ops.Invoke(t, "then", [default, A(a, 0)]));
        r.Method(proto, "finally", 1, (t, a, nt) =>
        {
            if (t.Ref is not JsObject p)
            {
                throw Errors.Type("Promise.prototype.finally called on a non-object");
            }

            JsObject c = Ops.SpeciesConstructor(p, ctor);
            JsValue onFinally = A(a, 0);
            if (!Ops.IsCallable(onFinally))
            {
                return Ops.Invoke(t, "then", [onFinally, onFinally]);
            }

            var thenFinally = r.Fn(string.Empty, 1, (t2, a2, nt2) =>
            {
                JsValue result = Ops.Call(onFinally, default);
                JsObject promise = Promises.PromiseResolve(JsValue.FromObject(c), result);
                JsValue value = A(a2, 0);
                return Ops.Invoke(JsValue.FromObject(promise), "then", [JsValue.FromObject(r.Fn(string.Empty, 0, (t3, a3, nt3) => value))]);
            });
            var catchFinally = r.Fn(string.Empty, 1, (t2, a2, nt2) =>
            {
                JsValue result = Ops.Call(onFinally, default);
                JsObject promise = Promises.PromiseResolve(JsValue.FromObject(c), result);
                JsValue reason = A(a2, 0);
                return Ops.Invoke(JsValue.FromObject(promise), "then", [JsValue.FromObject(r.Fn(string.Empty, 0, (t3, a3, nt3) => throw new JsException(reason)))]);
            });
            return Ops.Invoke(t, "then", [JsValue.FromObject(thenFinally), JsValue.FromObject(catchFinally)]);
        });
        r.Method(ctor, "resolve", 1, (t, a, nt) =>
        {
            if (!t.IsObject)
            {
                throw Errors.Type("PromiseResolve called on non-object");
            }

            return JsValue.FromObject(Promises.PromiseResolve(t, A(a, 0)));
        });
        r.Method(ctor, "reject", 1, (t, a, nt) =>
        {
            PromiseCapability cap = Promises.NewCapability(t);
            Ops.Call(cap.Reject, default, [A(a, 0)]);
            return JsValue.FromObject(cap.Promise);
        });
        r.Method(ctor, "withResolvers", 0, (t, a, nt) =>
        {
            PromiseCapability cap = Promises.NewCapability(t);
            var o = r.NewObject();
            o.CreateDataProperty("promise", JsValue.FromObject(cap.Promise));
            o.CreateDataProperty("resolve", cap.Resolve);
            o.CreateDataProperty("reject", cap.Reject);
            return JsValue.FromObject(o);
        });
        foreach (string kind in new[] { "all", "allSettled", "any", "race" })
        {
            r.Method(ctor, kind, 1, (t, a, nt) => Combinator(r, kind, t, A(a, 0)));
        }
    }

    private static JsValue Combinator(Realm r, string kind, JsValue c, JsValue iterable)
    {
        PromiseCapability cap = Promises.NewCapability(c);
        try
        {
            JsValue resolveFn = ((JsObject)c.Ref!).Get("resolve");
            if (!Ops.IsCallable(resolveFn))
            {
                throw Errors.Type("Promise resolve or reject function is not callable");
            }

            IteratorRecord rec = Ops.GetIterator(iterable);
            var values = new List<JsValue>();
            int remaining = 1;
            int index = 0;
            void Finish()
            {
                if (--remaining == 0)
                {
                    if (kind == "any")
                    {
                        var err = r.NewError("AggregateError", "All promises were rejected");
                        err.DefineDirect("errors", JsValue.FromObject(Ops.CreateArray(values)), PropFlags.Hidden);
                        Ops.Call(cap.Reject, default, [JsValue.FromObject(err)]);
                    }
                    else
                    {
                        Ops.Call(cap.Resolve, default, [JsValue.FromObject(Ops.CreateArray(values))]);
                    }
                }
            }

            try
            {
                while (Ops.IteratorStepValue(rec, out JsValue next))
                {
                    int i = index++;
                    values.Add(default);
                    JsValue np = Ops.Call(resolveFn, c, [next]);
                    bool called = false;
                    JsValue onF;
                    JsValue onR;
                    if (kind == "race")
                    {
                        onF = cap.Resolve;
                        onR = cap.Reject;
                    }
                    else
                    {
                        remaining++;
                        JsValue Settled(string status, string key, JsValue v)
                        {
                            var o = r.NewObject();
                            o.CreateDataProperty("status", Ops.Str(status));
                            o.CreateDataProperty(key, v);
                            return JsValue.FromObject(o);
                        }

                        JsValue Handler(Func<JsValue, JsValue>? map, bool passReject)
                        {
                            return JsValue.FromObject(r.Fn(string.Empty, 1, (t, a, nt) =>
                            {
                                if (called)
                                {
                                    return default;
                                }

                                called = true;
                                if (passReject)
                                {
                                    Ops.Call(cap.Reject, default, [A(a, 0)]);
                                    return default;
                                }

                                values[i] = map is null ? A(a, 0) : map(A(a, 0));
                                Finish();
                                return default;
                            }));
                        }

                        switch (kind)
                        {
                            case "all":
                                onF = Handler(null, false);
                                onR = cap.Reject;
                                break;
                            case "allSettled":
                                onF = Handler(v => Settled("fulfilled", "value", v), false);
                                onR = Handler(v => Settled("rejected", "reason", v), false);
                                break;
                            default:
                                onF = cap.Resolve;
                                onR = Handler(null, false);
                                break;
                        }
                    }

                    Ops.Invoke(np, "then", [onF, onR]);
                }

                if (kind != "race")
                {
                    Finish();
                }
            }
            catch (JsException)
            {
                if (!rec.Done)
                {
                    Ops.IteratorCloseSilently(rec);
                }

                throw;
            }
        }
        catch (JsException e)
        {
            Ops.Call(cap.Reject, default, [e.Value]);
        }

        return JsValue.FromObject(cap.Promise);
    }

    // ---- generators ---------------------------------------------------------------------------------------------------------------

    private static void SetupGenerators(Realm r)
    {
        // %GeneratorFunction.prototype% and %GeneratorFunction.prototype.prototype%
        r.GeneratorFunctionPrototype = new JsObject(r.FunctionPrototype);
        r.GeneratorPrototype = new JsObject(r.IteratorPrototype);
        r.Method(r.GeneratorPrototype, "next", 1, (t, a, nt) => Generators.Resume(t, ResumeMode.Next, A(a, 0), "next"));
        r.Method(r.GeneratorPrototype, "return", 1, (t, a, nt) => Generators.Resume(t, ResumeMode.Return, A(a, 0), "return"));
        r.Method(r.GeneratorPrototype, "throw", 1, (t, a, nt) => Generators.Resume(t, ResumeMode.Throw, A(a, 0), "throw"));
        r.GeneratorPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Generator"), PropFlags.Configurable);
        r.GeneratorFunctionPrototype.DefineDirect("prototype", JsValue.FromObject(r.GeneratorPrototype), PropFlags.Configurable);
        r.GeneratorPrototype.DefineDirect("constructor", JsValue.FromObject(r.GeneratorFunctionPrototype), PropFlags.Configurable);
        r.GeneratorFunctionPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("GeneratorFunction"), PropFlags.Configurable);
        MakeFunctionCtor(r, "GeneratorFunction", "generator", r.GeneratorFunctionPrototype);

        r.AsyncGeneratorFunctionPrototype = new JsObject(r.FunctionPrototype);
        r.AsyncGeneratorPrototype = new JsObject(r.AsyncIteratorPrototype);
        r.Method(r.AsyncGeneratorPrototype, "next", 1, (t, a, nt) => Generators.AsyncEnqueue(t, ResumeMode.Next, A(a, 0)));
        r.Method(r.AsyncGeneratorPrototype, "return", 1, (t, a, nt) => Generators.AsyncEnqueue(t, ResumeMode.Return, A(a, 0)));
        r.Method(r.AsyncGeneratorPrototype, "throw", 1, (t, a, nt) => Generators.AsyncEnqueue(t, ResumeMode.Throw, A(a, 0)));
        r.AsyncGeneratorPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("AsyncGenerator"), PropFlags.Configurable);
        r.AsyncGeneratorFunctionPrototype.DefineDirect("prototype", JsValue.FromObject(r.AsyncGeneratorPrototype), PropFlags.Configurable);
        r.AsyncGeneratorPrototype.DefineDirect("constructor", JsValue.FromObject(r.AsyncGeneratorFunctionPrototype), PropFlags.Configurable);
        r.AsyncGeneratorFunctionPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("AsyncGeneratorFunction"), PropFlags.Configurable);
        MakeFunctionCtor(r, "AsyncGeneratorFunction", "asyncGenerator", r.AsyncGeneratorFunctionPrototype);

        r.AsyncFunctionPrototype = new JsObject(r.FunctionPrototype);
        r.AsyncFunctionPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("AsyncFunction"), PropFlags.Configurable);
        MakeFunctionCtor(r, "AsyncFunction", "async", r.AsyncFunctionPrototype);

        // %AsyncFromSyncIteratorPrototype% (27.1.4)
        r.AsyncFromSyncIteratorPrototype = new JsObject(r.AsyncIteratorPrototype);
        foreach (string m in new[] { "next", "return", "throw" })
        {
            r.Method(r.AsyncFromSyncIteratorPrototype, m, 1, (t, a, nt) => AsyncFromSync.Step((AsyncFromSyncObject)t.Ref!, m, a));
        }
    }

    private static void MakeFunctionCtor(Realm r, string name, string kind, JsObject proto)
    {
        var f = new NativeFunction(r, name, 1, (t, a, nt) => DynamicFunction(r, a, nt, kind), true, (JsObject)r.Intrinsics["%Function%"]);
        f.DefineDirect("prototype", JsValue.FromObject(proto), PropFlags.None);
        proto.DefineDirect("constructor", JsValue.FromObject(f), PropFlags.Configurable);
        r.Intrinsics["%" + name + "%"] = f;
        r.Intrinsics[kind] = f;
    }
}

internal sealed class AsyncFromSyncObject : JsObject
{
    internal IteratorRecord Sync = null!;

    internal AsyncFromSyncObject(JsObject proto)
        : base(proto)
    {
    }
}

/// <summary>CreateAsyncFromSyncIterator (27.1.6).</summary>
internal static class AsyncFromSync
{
    internal static IteratorRecord Create(IteratorRecord sync)
    {
        Realm r = Agent.Current.Realm;
        var o = new AsyncFromSyncObject(r.AsyncFromSyncIteratorPrototype) { Sync = sync };
        return new IteratorRecord(o, o.Get("next"));
    }

    internal static JsValue Step(AsyncFromSyncObject o, string method, ReadOnlySpan<JsValue> args)
    {
        Realm r = Agent.Current.Realm;
        PromiseCapability cap = Promises.NewCapability(JsValue.FromObject(r.PromiseConstructor));
        IteratorRecord sync = o.Sync;
        try
        {
            JsValue result;
            if (method == "next")
            {
                result = args.Length > 0 ? Ops.Call(sync.Next, JsValue.FromObject(sync.Iterator), [args[0]]) : Ops.Call(sync.Next, JsValue.FromObject(sync.Iterator));
            }
            else
            {
                JsValue m = Ops.GetMethod(JsValue.FromObject(sync.Iterator), method);
                if (m.IsUndefined)
                {
                    if (method == "return")
                    {
                        Ops.Call(cap.Resolve, default, [JsValue.FromObject(Ops.CreateIterResult(Ops.Arg(args, 0), true))]);
                        return JsValue.FromObject(cap.Promise);
                    }

                    // 27.1.4.4 step 8: close the sync iterator (an error from return() wins), then reject with a TypeError.
                    Ops.IteratorClose(sync);
                    throw Errors.Type("The iterator does not provide a 'throw' method");
                }

                result = args.Length > 0 ? ((JsObject)m.Ref!).Call(JsValue.FromObject(sync.Iterator), [args[0]]) : ((JsObject)m.Ref!).Call(JsValue.FromObject(sync.Iterator), default);
            }

            if (result.Ref is not JsObject ro)
            {
                throw Errors.Type("Iterator result " + Ops.DisplayValue(result) + " is not an object");
            }

            bool done = Ops.ToBoolean(ro.Get("done"));
            JsValue value = ro.Get("value");
            bool closeOnRejection = method != "return";
            JsObject vw;
            try
            {
                vw = Promises.PromiseResolve(JsValue.FromObject(r.PromiseConstructor), value);
            }
            catch (JsException) when (!done && closeOnRejection)
            {
                // 27.1.5.4 step 7: a failing PromiseResolve closes the sync iterator before the rejection.
                Ops.IteratorCloseSilently(sync);
                throw;
            }

            Promises.PerformThen(
                (PromiseObject)vw,
                JsValue.FromObject(r.Fn(string.Empty, 1, (t, a, nt) => JsValue.FromObject(Ops.CreateIterResult(Ops.Arg(a, 0), done)))),
                done || !closeOnRejection
                    ? default
                    : JsValue.FromObject(r.Fn(string.Empty, 1, (t, a, nt) =>
                    {
                        Ops.IteratorCloseSilently(sync);
                        throw new JsException(Ops.Arg(a, 0));
                    })),
                cap);
        }
        catch (JsException e)
        {
            Ops.Call(cap.Reject, default, [e.Value]);
        }

        return JsValue.FromObject(cap.Promise);
    }
}
