using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
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
}
