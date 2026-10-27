using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    // ---- iterators -------------------------------------------------------------------------------------------------

    private sealed class ArrayIteratorObject : JsObject
    {
        internal JsObject? Target;
        internal int Index;
        internal int Kind;

        internal ArrayIteratorObject(JsObject proto)
            : base(proto)
        {
        }
    }

    private sealed class StringIteratorObject : JsObject
    {
        internal string? Text;
        internal int Pos;

        internal StringIteratorObject(JsObject proto)
            : base(proto)
        {
        }
    }

    private static void SetupIterators(Realm r)
    {
        r.IteratorPrototype = r.NewObject();
        r.Method(r.IteratorPrototype, WellKnown.Iterator, "[Symbol.iterator]", 0, (t, a, nt) => t);
        r.AsyncIteratorPrototype = r.NewObject();
        r.Method(r.AsyncIteratorPrototype, WellKnown.AsyncIterator, "[Symbol.asyncIterator]", 0, (t, a, nt) => t);
        r.ArrayIteratorPrototype = new JsObject(r.IteratorPrototype);
        r.Method(r.ArrayIteratorPrototype, "next", 0, (t, a, nt) =>
        {
            if (t.Ref is not ArrayIteratorObject it)
            {
                throw Errors.Type("next method called on incompatible receiver " + Ops.DisplayValue(t));
            }

            if (it.Target is null)
            {
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }

            long len = Ops.LengthOfArrayLike(it.Target);
            if (it.Index >= len)
            {
                it.Target = null;
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }

            int i = it.Index++;
            JsValue idx = Ops.Num(i);
            if (it.Kind == 1)
            {
                return JsValue.FromObject(Ops.CreateIterResult(idx, false));
            }

            JsValue v = it.Target is JsArray ja && ja.TryGetDense((uint)i, out JsValue dv) ? dv : it.Target.Get(new PropertyKey(Ops.NumberToStr(i)));
            return JsValue.FromObject(Ops.CreateIterResult(it.Kind == 2 ? v : JsValue.FromObject(Ops.CreateArray([idx, v])), false));
        });
        r.ArrayIteratorPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Array Iterator"), PropFlags.Configurable);
    }

    private static JsValue MakeArrayIterator(Realm r, JsValue t, int kind)
    {
        JsObject o = Ops.ToObject(t);
        return JsValue.FromObject(new ArrayIteratorObject(r.ArrayIteratorPrototype) { Target = o, Kind = kind });
    }

    // ---- Array ---------------------------------------------------------------------------------------------------------

    /// <summary>The property key of an array index; charges one step so loops over huge array-likes end in a limit.</summary>
    private static string IdxKey(long i)
    {
        Agent.Current.Tick();
        return Ops.NumberToStr(i);
    }

    private static void SetupArray(Realm r)
    {
        JsObject proto = r.ArrayPrototype;
        NativeFunction ctor = r.Ctor("Array", 1, (t, a, nt) =>
        {
            JsObject p = nt is null ? r.ArrayPrototype : Ops.GetPrototypeFromConstructor(nt, x => x.ArrayPrototype);
            var arr = new JsArray(p);
            if (a.Length == 1 && a[0].IsNumber)
            {
                double n = a[0].Num;
                if (n != Ops.ToUint32(n))
                {
                    throw Errors.Range("Invalid array length");
                }

                arr.Length = (uint)n;
            }
            else
            {
                foreach (JsValue v in a)
                {
                    arr.Push(v);
                }
            }

            return JsValue.FromObject(arr);
        }, proto);
        r.Method(ctor, "isArray", 1, (t, a, nt) => Ops.Bool(Ops.IsArray(A(a, 0))));
        r.Method(ctor, "of", 0, (t, a, nt) =>
        {
            var arr = new JsArray(r.ArrayPrototype);
            foreach (JsValue v in a)
            {
                arr.Push(v);
            }

            return JsValue.FromObject(arr);
        });
        r.Method(ctor, "from", 1, (t, a, nt) =>
        {
            var arr = new JsArray(r.ArrayPrototype);
            JsValue src = A(a, 0);
            JsValue map = A(a, 1);
            if (!map.IsUndefined && !Ops.IsCallable(map))
            {
                throw Errors.Type(Ops.DisplayValue(map) + " is not a function");
            }

            JsValue m = src.IsNullish ? default : Ops.GetMethod(src, new PropertyKey(WellKnown.Iterator));
            long i = 0;
            if (!m.IsUndefined)
            {
                foreach (JsValue v in Ops.IterableToList(src, m))
                {
                    arr.Push(map.IsUndefined ? v : Ops.Call(map, A(a, 2), [v, Ops.Num(i)]));
                    i++;
                }
            }
            else
            {
                JsObject o = Ops.ToObject(src);
                long len = Ops.LengthOfArrayLike(o);
                for (; i < len; i++)
                {
                    JsValue v = o.Get(IdxKey(i));
                    arr.Push(map.IsUndefined ? v : Ops.Call(map, A(a, 2), [v, Ops.Num(i)]));
                }
            }

            return JsValue.FromObject(arr);
        });
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        r.Method(proto, "push", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            if (o is JsArray ja && ja.Dense is not null && ja.Count == ja.Length && ja.Extensible && ja.LengthWritable)
            {
                foreach (JsValue v in a)
                {
                    ja.Push(v);
                }

                return Ops.Num(ja.Length);
            }

            long len = Ops.LengthOfArrayLike(o);
            foreach (JsValue v in a)
            {
                o.SetOrThrow(IdxKey(len++), v);
            }

            o.SetOrThrow("length", Ops.Num(len));
            return Ops.Num(len);
        });
        r.Method(proto, "pop", 0, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            if (len == 0)
            {
                o.SetOrThrow("length", Ops.Num(0));
                return default;
            }

            PropertyKey k = IdxKey(len - 1);
            JsValue v = o.Get(k);
            if (!o.Delete(k))
            {
                throw Errors.Type("Cannot delete property '" + k + "' of " + Ops.DisplayValue(t));
            }

            o.SetOrThrow("length", Ops.Num(len - 1));
            return v;
        });
        r.Method(proto, "join", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            string sep = A(a, 0).IsUndefined ? "," : Ops.ToStr(A(a, 0));
            var sb = new StringBuilder();
            for (long i = 0; i < len; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }

                JsValue v = o.Get(IdxKey(i));
                if (!v.IsNullish)
                {
                    sb.Append(Ops.ToStr(v));
                }

                Agent.Current.Tick();
                if (sb.Length > Agent.Current.Limits.MaxStringLength)
                {
                    throw Errors.Range("Invalid string length");
                }
            }

            return Ops.Str(sb.ToString());
        });
        r.Method(proto, "toString", 0, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            JsValue j = o.Get("join");
            return Ops.IsCallable(j) ? Ops.Call(j, JsValue.FromObject(o)) : Ops.Str(ObjectToString(JsValue.FromObject(o)));
        });
        r.Method(proto, "indexOf", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            double n = Ops.ToIntegerOrInfinity(A(a, 1));
            long i = n >= 0 ? (long)Math.Min(n, len) : (long)Math.Max(len + n, 0);
            for (; i < len; i++)
            {
                PropertyKey k = IdxKey(i);
                if (o.HasProperty(k) && Ops.StrictEquals(o.Get(k), A(a, 0)))
                {
                    return Ops.Num(i);
                }
            }

            return Ops.Num(-1);
        });
        r.Method(proto, "includes", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            double n = Ops.ToIntegerOrInfinity(A(a, 1));
            long i = n >= 0 ? (long)Math.Min(n, len) : (long)Math.Max(len + n, 0);
            for (; i < len; i++)
            {
                if (Ops.SameValueZero(o.Get(IdxKey(i)), A(a, 0)))
                {
                    return JsValue.True;
                }
            }

            return JsValue.False;
        });
        r.Method(proto, "forEach", 1, (t, a, nt) => Iterate(t, a, 0));
        r.Method(proto, "map", 1, (t, a, nt) => Iterate(t, a, 1));
        r.Method(proto, "filter", 1, (t, a, nt) => Iterate(t, a, 2));
        r.Method(proto, "some", 1, (t, a, nt) => Iterate(t, a, 3));
        r.Method(proto, "every", 1, (t, a, nt) => Iterate(t, a, 4));
        r.Method(proto, "reduce", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            if (!Ops.IsCallable(A(a, 0)))
            {
                throw Errors.Type(Ops.DisplayValue(A(a, 0)) + " is not a function");
            }

            long i = 0;
            JsValue acc = default;
            if (a.Length > 1)
            {
                acc = a[1];
            }
            else
            {
                bool found = false;
                for (; i < len && !found; i++)
                {
                    PropertyKey k = IdxKey(i);
                    if (o.HasProperty(k))
                    {
                        acc = o.Get(k);
                        found = true;
                    }
                }

                if (!found)
                {
                    throw Errors.Type("Reduce of empty array with no initial value");
                }
            }

            for (; i < len; i++)
            {
                PropertyKey k = IdxKey(i);
                if (o.HasProperty(k))
                {
                    acc = Ops.Call(A(a, 0), default, [acc, o.Get(k), Ops.Num(i), JsValue.FromObject(o)]);
                }
            }

            return acc;
        });
        r.Method(proto, "slice", 2, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            long k = RelIndex(A(a, 0), len, 0);
            long fin = RelIndex(A(a, 1), len, len);
            var res = new JsArray(r.ArrayPrototype);
            long n = 0;
            for (; k < fin; k++, n++)
            {
                PropertyKey pk = IdxKey(k);
                if (o.HasProperty(pk))
                {
                    res.CreateDataPropertyOrThrow(IdxKey(n), o.Get(pk));
                }
            }

            res.SetOrThrow("length", Ops.Num(n));
            return JsValue.FromObject(res);
        });
        r.Method(proto, "concat", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            var res = new JsArray(r.ArrayPrototype);
            long n = 0;
            foreach (JsValue item in new[] { JsValue.FromObject(o) }.Concat(a.ToArray()))
            {
                bool spread = item.Ref is JsObject io && (io.Get(new PropertyKey(WellKnown.IsConcatSpreadable)) is { IsUndefined: false } sv ? Ops.ToBoolean(sv) : Ops.IsArray(item));
                if (spread)
                {
                    var io2 = (JsObject)item.Ref!;
                    long len = Ops.LengthOfArrayLike(io2);
                    for (long i = 0; i < len; i++, n++)
                    {
                        PropertyKey pk = IdxKey(i);
                        if (io2.HasProperty(pk))
                        {
                            res.CreateDataPropertyOrThrow(IdxKey(n), io2.Get(pk));
                        }
                    }
                }
                else
                {
                    res.CreateDataPropertyOrThrow(IdxKey(n++), item);
                }
            }

            res.SetOrThrow("length", Ops.Num(n));
            return JsValue.FromObject(res);
        });
        r.Method(proto, "reverse", 0, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            long len = Ops.LengthOfArrayLike(o);
            for (long lo = 0, hi = len - 1; lo < hi; lo++, hi--)
            {
                PropertyKey lk = IdxKey(lo);
                PropertyKey hk = IdxKey(hi);
                bool le = o.HasProperty(lk);
                JsValue lv = le ? o.Get(lk) : default;
                bool he = o.HasProperty(hk);
                JsValue hv = he ? o.Get(hk) : default;
                if (he)
                {
                    o.SetOrThrow(lk, hv);
                }
                else
                {
                    o.Delete(lk);
                }

                if (le)
                {
                    o.SetOrThrow(hk, lv);
                }
                else
                {
                    o.Delete(hk);
                }
            }

            return JsValue.FromObject(o);
        });
        NativeFunction values = r.Method(proto, "values", 0, (t, a, nt) => MakeArrayIterator(r, t, 2));
        proto.DefineDirect(new PropertyKey(WellKnown.Iterator), JsValue.FromObject(values), PropFlags.Hidden);
        r.ArrayProtoValues = JsValue.FromObject(values);
        r.Method(proto, "keys", 0, (t, a, nt) => MakeArrayIterator(r, t, 1));
        r.Method(proto, "entries", 0, (t, a, nt) => MakeArrayIterator(r, t, 3));
    }

    private static long RelIndex(JsValue v, long len, long dflt)
    {
        if (v.IsUndefined)
        {
            return dflt;
        }

        double n = Ops.ToIntegerOrInfinity(v);
        return n < 0 ? (long)Math.Max(len + n, 0) : (long)Math.Min(n, len);
    }

    private static JsValue Iterate(JsValue t, ReadOnlySpan<JsValue> a, int kind)
    {
        JsObject o = Ops.ToObject(t);
        long len = Ops.LengthOfArrayLike(o);
        JsValue f = A(a, 0);
        if (!Ops.IsCallable(f))
        {
            throw Errors.Type(Ops.DisplayValue(f) + " is not a function");
        }

        var res = kind is 1 or 2 ? new JsArray(Agent.Current.Realm.ArrayPrototype) : null;
        for (long i = 0; i < len; i++)
        {
            PropertyKey k = IdxKey(i);
            if (!o.HasProperty(k))
            {
                if (kind == 1)
                {
                    res!.Length++;
                }

                continue;
            }

            JsValue v = o.Get(k);
            JsValue rv = Ops.Call(f, A(a, 1), [v, Ops.Num(i), JsValue.FromObject(o)]);
            switch (kind)
            {
                case 1:
                    res!.CreateDataPropertyOrThrow(k, rv);
                    break;
                case 2:
                    if (Ops.ToBoolean(rv))
                    {
                        res!.Push(v);
                    }

                    break;
                case 3:
                    if (Ops.ToBoolean(rv))
                    {
                        return JsValue.True;
                    }

                    break;
                case 4:
                    if (!Ops.ToBoolean(rv))
                    {
                        return JsValue.False;
                    }

                    break;
            }
        }

        return kind switch { 1 or 2 => JsValue.FromObject(res!), 3 => JsValue.False, 4 => JsValue.True, _ => default };
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

                    Ops.IteratorCloseSilently(sync);
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
            JsObject vw = Promises.PromiseResolve(JsValue.FromObject(r.PromiseConstructor), value);
            Promises.PerformThen(
                (PromiseObject)vw,
                JsValue.FromObject(r.Fn(string.Empty, 1, (t, a, nt) => JsValue.FromObject(Ops.CreateIterResult(Ops.Arg(a, 0), done)))),
                done || method == "return"
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
