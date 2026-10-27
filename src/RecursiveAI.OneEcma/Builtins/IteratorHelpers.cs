namespace RecursiveAI.OneEcma;

/// <summary>An iterator helper object (27.1.2): a generator-like object produced by map, filter, take, drop, flatMap or Iterator.concat.</summary>
internal sealed class IteratorHelperObject : JsObject
{
    internal const int Start = 0;
    internal const int Suspended = 1;
    internal const int Running = 2;
    internal const int Completed = 3;

    internal HelperKind Kind;
    internal int State;
    internal IteratorRecord? Underlying;
    internal JsValue Fn;
    internal long Counter;
    internal double Remaining;
    internal bool Dropped;
    internal IteratorRecord? Inner;
    internal List<(JsValue Method, JsValue Iterable)>? Sources;
    internal int SourceIndex;

    internal IteratorHelperObject(JsObject proto)
        : base(proto)
    {
    }

    /// <summary>%IteratorHelperPrototype%.next: GeneratorResume with the closure of the helper as a state machine.</summary>
    internal JsValue Next()
    {
        if (State == Running)
        {
            throw Errors.Type("Generator is already running");
        }

        if (State == Completed)
        {
            return JsValue.FromObject(Ops.CreateIterResult(default, true));
        }

        State = Running;
        bool yielded = false;
        try
        {
            if (Step(out JsValue value))
            {
                yielded = true;
                return JsValue.FromObject(Ops.CreateIterResult(value, false));
            }

            return JsValue.FromObject(Ops.CreateIterResult(default, true));
        }
        finally
        {
            State = yielded ? Suspended : Completed;
        }
    }

    /// <summary>%IteratorHelperPrototype%.return (27.1.2.1.2).</summary>
    internal JsValue Return()
    {
        if (State == Running)
        {
            throw Errors.Type("Generator is already running");
        }

        int was = State;
        State = Running;
        try
        {
            if (was == Start)
            {
                if (Underlying is not null)
                {
                    Ops.IteratorClose(Underlying);
                }
            }
            else if (was == Suspended)
            {
                // The closure resumes with a return completion at its Yield: the inner iterator closes first, then the outer.
                if (Kind == HelperKind.FlatMap && Inner is not null)
                {
                    try
                    {
                        Ops.IteratorClose(Inner);
                    }
                    catch (JsException)
                    {
                        Ops.IteratorCloseSilently(Underlying!);
                        throw;
                    }

                    Ops.IteratorClose(Underlying!);
                }
                else if (Kind == HelperKind.Concat)
                {
                    if (Inner is not null)
                    {
                        Ops.IteratorClose(Inner);
                    }
                }
                else
                {
                    Ops.IteratorClose(Underlying!);
                }
            }
        }
        finally
        {
            State = Completed;
        }

        return JsValue.FromObject(Ops.CreateIterResult(default, true));
    }

    private void CloseUnderlyingAfterThrow() => Ops.IteratorCloseSilently(Underlying!);

    /// <summary>Runs the closure up to its next Yield. False means it returned.</summary>
    private bool Step(out JsValue value)
    {
        value = default;
        switch (Kind)
        {
            case HelperKind.Map:
            {
                if (!Ops.IteratorStepValue(Underlying!, out JsValue v))
                {
                    return false;
                }

                value = CallClosing(v);
                Counter++;
                return true;
            }

            case HelperKind.Filter:
                while (true)
                {
                    if (!Ops.IteratorStepValue(Underlying!, out JsValue v))
                    {
                        return false;
                    }

                    bool selected = Ops.ToBoolean(CallClosing(v));
                    Counter++;
                    if (selected)
                    {
                        value = v;
                        return true;
                    }
                }

            case HelperKind.Take:
                if (Remaining == 0)
                {
                    Ops.IteratorClose(Underlying!);
                    return false;
                }

                if (!double.IsPositiveInfinity(Remaining))
                {
                    Remaining--;
                }

                return Ops.IteratorStepValue(Underlying!, out value);

            case HelperKind.Drop:
                while (Remaining > 0)
                {
                    if (!double.IsPositiveInfinity(Remaining))
                    {
                        Remaining--;
                    }

                    if (!IteratorHelpers.StepOnly(Underlying!))
                    {
                        return false;
                    }
                }

                return Ops.IteratorStepValue(Underlying!, out value);

            case HelperKind.FlatMap:
                while (true)
                {
                    if (Inner is not null)
                    {
                        bool has;
                        JsValue iv;
                        try
                        {
                            has = Ops.IteratorStepValue(Inner, out iv);
                        }
                        catch (JsException)
                        {
                            CloseUnderlyingAfterThrow();
                            throw;
                        }

                        if (has)
                        {
                            value = iv;
                            return true;
                        }

                        Inner = null;
                        Counter++;
                    }

                    if (!Ops.IteratorStepValue(Underlying!, out JsValue outer))
                    {
                        return false;
                    }

                    JsValue mapped = CallClosing(outer);
                    try
                    {
                        Inner = IteratorHelpers.GetIteratorFlattenable(mapped, false, "Iterator.prototype.flatMap");
                    }
                    catch (JsException)
                    {
                        CloseUnderlyingAfterThrow();
                        throw;
                    }
                }

            default: // Concat
                while (true)
                {
                    if (Inner is null)
                    {
                        if (SourceIndex >= Sources!.Count)
                        {
                            return false;
                        }

                        (JsValue method, JsValue iterable) = Sources[SourceIndex++];
                        JsValue iter = Ops.Call(method, iterable);
                        if (iter.Ref is not JsObject io)
                        {
                            throw Errors.Type(Ops.DisplayValue(iterable) + " is not an object");
                        }

                        Inner = IteratorHelpers.Direct(io);
                    }

                    if (Ops.IteratorStepValue(Inner, out value))
                    {
                        return true;
                    }

                    Inner = null;
                }
        }
    }

    /// <summary>Calls the helper's function with (value, counter); a throw closes the underlying iterator first (IfAbruptCloseIterator).</summary>
    private JsValue CallClosing(JsValue v)
    {
        try
        {
            return Ops.Call(Fn, default, [v, Ops.Num(Counter)]);
        }
        catch (JsException)
        {
            CloseUnderlyingAfterThrow();
            throw;
        }
    }
}

internal enum HelperKind
{
    Map,
    Filter,
    Take,
    Drop,
    FlatMap,
    Concat,
}

/// <summary>The object %WrapForValidIteratorPrototype% inherits for (27.1.3.2.2.1): an iterator record behind an ordinary object.</summary>
internal sealed class WrapIteratorObject : JsObject
{
    internal IteratorRecord Iterated;

    internal WrapIteratorObject(JsObject proto, IteratorRecord iterated)
        : base(proto)
    {
        Iterated = iterated;
    }
}

/// <summary>The Iterator constructor, Iterator.from, Iterator.concat and Iterator.prototype helpers (ECMA-262 27.1).</summary>
internal static class IteratorHelpers
{
    private static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    internal static void Setup(Realm r)
    {
        JsObject proto = r.IteratorPrototype;
        NativeFunction? ctor = null;
        ctor = r.Ctor("Iterator", 0, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor Iterator requires 'new'");
            }

            if (ReferenceEquals(nt, ctor))
            {
                throw Errors.Type("Abstract class Iterator not directly constructable");
            }

            return JsValue.FromObject(Ops.OrdinaryCreateFromConstructor(nt, x => x.IteratorPrototype));
        }, proto);
        r.Intrinsics["%Iterator.prototype%"] = proto;

        // 27.1.3.3.1 and 27.1.3.3.14: "constructor" and @@toStringTag are accessors, for compatibility with code that assigns to them.
        r.Getter(proto, "constructor", "constructor", (t, a, nt) => JsValue.FromObject(ctor), (t, a, nt) =>
        {
            SetterThatIgnoresPrototypeProperties(t, proto, "constructor", A(a, 0), "Iterator.prototype.constructor");
            return default;
        });
        var tag = new PropertyKey(WellKnown.ToStringTag);
        r.Getter(proto, tag, "[Symbol.toStringTag]", (t, a, nt) => Ops.Str("Iterator"), (t, a, nt) =>
        {
            SetterThatIgnoresPrototypeProperties(t, proto, tag, A(a, 0), "Iterator.prototype[Symbol.toStringTag]");
            return default;
        });

        r.Method(ctor, "from", 1, (t, a, nt) => From(r, A(a, 0)));
        r.Method(ctor, "concat", 0, (t, a, nt) => Concat(r, a));

        // %WrapForValidIteratorPrototype% (27.1.3.2.2.1)
        var wrap = new JsObject(proto);
        r.Intrinsics["%WrapForValidIteratorPrototype%"] = wrap;
        r.Method(wrap, "next", 0, (t, a, nt) =>
        {
            if (t.Ref is not WrapIteratorObject w)
            {
                throw Errors.Type("Method %WrapForValidIteratorPrototype%.next called on incompatible receiver " + Ops.DisplayValue(t));
            }

            return Ops.Call(w.Iterated.Next, JsValue.FromObject(w.Iterated.Iterator));
        });
        r.Method(wrap, "return", 0, (t, a, nt) =>
        {
            if (t.Ref is not WrapIteratorObject w)
            {
                throw Errors.Type("Method %WrapForValidIteratorPrototype%.return called on incompatible receiver " + Ops.DisplayValue(t));
            }

            JsObject iterator = w.Iterated.Iterator;
            JsValue ret = Ops.GetMethod(JsValue.FromObject(iterator), "return");
            if (ret.IsUndefined)
            {
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }

            return Ops.Call(ret, JsValue.FromObject(iterator));
        });

        // %IteratorHelperPrototype% (27.1.2.1)
        var helperProto = new JsObject(proto);
        r.Intrinsics["%IteratorHelperPrototype%"] = helperProto;
        r.Method(helperProto, "next", 0, (t, a, nt) => t.Ref is IteratorHelperObject h
            ? h.Next()
            : throw Errors.Type("Method Iterator Helper.prototype.next called on incompatible receiver " + Ops.DisplayValue(t)));
        r.Method(helperProto, "return", 0, (t, a, nt) => t.Ref is IteratorHelperObject h
            ? h.Return()
            : throw Errors.Type("Method Iterator Helper.prototype.return called on incompatible receiver " + Ops.DisplayValue(t)));
        helperProto.DefineDirect(tag, Ops.Str("Iterator Helper"), PropFlags.Configurable);

        r.Method(proto, "drop", 1, (t, a, nt) => TakeDrop(r, t, A(a, 0), HelperKind.Drop));
        r.Method(proto, "every", 1, (t, a, nt) => Eager(t, a, "every"));
        r.Method(proto, "filter", 1, (t, a, nt) => Lazy(r, t, A(a, 0), HelperKind.Filter, "filter"));
        r.Method(proto, "find", 1, (t, a, nt) => Eager(t, a, "find"));
        r.Method(proto, "flatMap", 1, (t, a, nt) => Lazy(r, t, A(a, 0), HelperKind.FlatMap, "flatMap"));
        r.Method(proto, "forEach", 1, (t, a, nt) => Eager(t, a, "forEach"));
        r.Method(proto, "map", 1, (t, a, nt) => Lazy(r, t, A(a, 0), HelperKind.Map, "map"));
        r.Method(proto, "reduce", 1, (t, a, nt) => Reduce(t, a));
        r.Method(proto, "some", 1, (t, a, nt) => Eager(t, a, "some"));
        r.Method(proto, "take", 1, (t, a, nt) => TakeDrop(r, t, A(a, 0), HelperKind.Take));
        r.Method(proto, "toArray", 0, (t, a, nt) => ToArray(t));
    }

    /// <summary>SetterThatIgnoresPrototypeProperties (27.1.3.3.1.2 note).</summary>
    private static void SetterThatIgnoresPrototypeProperties(JsValue thisValue, JsObject home, PropertyKey p, JsValue v, string label)
    {
        if (thisValue.Ref is not JsObject o)
        {
            throw Errors.Type("set " + label + " called on non-object");
        }

        if (ReferenceEquals(o, home))
        {
            throw Errors.Type(Ops.ReadOnlyMessage(o, p));
        }

        if (!o.GetOwnProperty(p, out _))
        {
            o.CreateDataPropertyOrThrow(p, v);
        }
        else
        {
            ArrayAlgo.Set(o, p, v);
        }
    }

    /// <summary>GetIteratorDirect (7.4.4).</summary>
    internal static IteratorRecord Direct(JsObject o) => new(o, o.Get("next"));

    /// <summary>IteratorStep (7.4.9) without reading "value": true when the iterator produced another result.</summary>
    internal static bool StepOnly(IteratorRecord rec)
    {
        try
        {
            JsValue r = Ops.Call(rec.Next, JsValue.FromObject(rec.Iterator));
            if (r.Ref is not JsObject ro)
            {
                throw Errors.Type("Iterator result " + Ops.DisplayValue(r) + " is not an object");
            }

            if (Ops.ToBoolean(ro.Get("done")))
            {
                rec.Done = true;
                return false;
            }

            return true;
        }
        catch (JsException)
        {
            rec.Done = true;
            throw;
        }
    }

    /// <summary>GetIteratorFlattenable (7.4.5). With <paramref name="iterateStrings"/> a string primitive is accepted, otherwise any primitive is rejected.</summary>
    internal static IteratorRecord GetIteratorFlattenable(JsValue obj, bool iterateStrings, string caller)
    {
        if (obj.Ref is not JsObject && !(iterateStrings && obj.IsString))
        {
            throw Errors.Type(caller + " called on non-object");
        }

        JsValue method = Ops.GetMethod(obj, new PropertyKey(WellKnown.Iterator));
        JsValue iterator = method.IsUndefined ? obj : Ops.Call(method, obj);
        if (iterator.Ref is not JsObject io)
        {
            throw Errors.Type(Ops.DisplayValue(obj) + " is not iterable");
        }

        return Direct(io);
    }

    private static JsValue From(Realm r, JsValue o)
    {
        IteratorRecord rec = GetIteratorFlattenable(o, true, "Iterator.from");
        if (Ops.OrdinaryHasInstance(JsValue.FromObject(r.Intrinsics["%Iterator%"]), JsValue.FromObject(rec.Iterator)))
        {
            return JsValue.FromObject(rec.Iterator);
        }

        return JsValue.FromObject(new WrapIteratorObject(r.Intrinsics["%WrapForValidIteratorPrototype%"], rec));
    }

    /// <summary>Iterator.concat ( ...items ) (27.1.3.2.1).</summary>
    private static JsValue Concat(Realm r, ReadOnlySpan<JsValue> items)
    {
        var sources = new List<(JsValue, JsValue)>();
        foreach (JsValue item in items)
        {
            if (item.Ref is not JsObject)
            {
                throw Errors.Type(Ops.DisplayValue(item) + " is not an object");
            }

            JsValue method = Ops.GetMethod(item, new PropertyKey(WellKnown.Iterator));
            if (method.IsUndefined)
            {
                throw Errors.Type(Ops.DisplayValue(item) + " is not iterable");
            }

            sources.Add((method, item));
        }

        return JsValue.FromObject(new IteratorHelperObject(r.Intrinsics["%IteratorHelperPrototype%"]) { Kind = HelperKind.Concat, Sources = sources });
    }

    private static JsObject RequireObject(JsValue t, string method) =>
        t.Ref as JsObject ?? throw Errors.Type("Iterator.prototype." + method + " called on non-object");

    /// <summary>Step 4 of the helper methods: a non-callable argument closes the receiver and throws.</summary>
    private static void RequireCallable(JsObject o, JsValue f)
    {
        if (!Ops.IsCallable(f))
        {
            Ops.IteratorCloseSilently(new IteratorRecord(o, default));
            throw ArrayAlgo.NotFunction(f);
        }
    }

    private static JsValue Lazy(Realm r, JsValue t, JsValue fn, HelperKind kind, string name)
    {
        JsObject o = RequireObject(t, name);
        RequireCallable(o, fn);
        IteratorRecord rec = Direct(o);
        return JsValue.FromObject(new IteratorHelperObject(r.Intrinsics["%IteratorHelperPrototype%"]) { Kind = kind, Underlying = rec, Fn = fn });
    }

    private static JsValue TakeDrop(Realm r, JsValue t, JsValue limit, HelperKind kind)
    {
        string name = kind == HelperKind.Take ? "take" : "drop";
        JsObject o = RequireObject(t, name);
        var closing = new IteratorRecord(o, default);
        double numLimit;
        try
        {
            numLimit = Ops.ToNumber(limit);
        }
        catch (JsException)
        {
            Ops.IteratorCloseSilently(closing);
            throw;
        }

        // A finite limit beyond 2^53 - 1 is a RangeError as well (the conformance suite is newer than the edition text here).
        double integerLimit = double.IsNaN(numLimit) ? double.NaN : Ops.ToIntegerOrInfinity(Ops.Num(numLimit));
        if (double.IsNaN(integerLimit) || integerLimit < 0 || (double.IsFinite(numLimit) && numLimit > ArrayAlgo.MaxSafeInteger))
        {
            Ops.IteratorCloseSilently(closing);
            throw Errors.Range(Ops.ToStr(limit) + " must be positive");
        }

        IteratorRecord rec = Direct(o);
        return JsValue.FromObject(new IteratorHelperObject(r.Intrinsics["%IteratorHelperPrototype%"]) { Kind = kind, Underlying = rec, Remaining = integerLimit });
    }

    /// <summary>every, find, forEach and some (27.1.3.3.3, .5, .7, .10): they consume the iterator eagerly.</summary>
    private static JsValue Eager(JsValue t, ReadOnlySpan<JsValue> a, string name)
    {
        JsObject o = RequireObject(t, name);
        JsValue fn = A(a, 0);
        RequireCallable(o, fn);
        IteratorRecord rec = Direct(o);
        long counter = 0;
        while (true)
        {
            if (!Ops.IteratorStepValue(rec, out JsValue value))
            {
                return name switch { "every" => JsValue.True, "some" => JsValue.False, _ => default };
            }

            JsValue result;
            try
            {
                result = Ops.Call(fn, default, [value, Ops.Num(counter)]);
            }
            catch (JsException)
            {
                Ops.IteratorCloseSilently(rec);
                throw;
            }

            switch (name)
            {
                case "every":
                    if (!Ops.ToBoolean(result))
                    {
                        Ops.IteratorClose(rec);
                        return JsValue.False;
                    }

                    break;
                case "some":
                    if (Ops.ToBoolean(result))
                    {
                        Ops.IteratorClose(rec);
                        return JsValue.True;
                    }

                    break;
                case "find":
                    if (Ops.ToBoolean(result))
                    {
                        Ops.IteratorClose(rec);
                        return value;
                    }

                    break;
            }

            counter++;
        }
    }

    private static JsValue Reduce(JsValue t, ReadOnlySpan<JsValue> a)
    {
        JsObject o = RequireObject(t, "reduce");
        JsValue reducer = A(a, 0);
        RequireCallable(o, reducer);
        IteratorRecord rec = Direct(o);
        JsValue acc;
        long counter;
        if (a.Length < 2)
        {
            if (!Ops.IteratorStepValue(rec, out acc))
            {
                throw Errors.Type("Reduce of a done iterator with no initial value");
            }

            counter = 1;
        }
        else
        {
            acc = a[1];
            counter = 0;
        }

        while (Ops.IteratorStepValue(rec, out JsValue value))
        {
            try
            {
                acc = Ops.Call(reducer, default, [acc, value, Ops.Num(counter)]);
            }
            catch (JsException)
            {
                Ops.IteratorCloseSilently(rec);
                throw;
            }

            counter++;
        }

        return acc;
    }

    private static JsValue ToArray(JsValue t)
    {
        JsObject o = RequireObject(t, "toArray");
        IteratorRecord rec = Direct(o);
        var items = new List<JsValue>();
        while (Ops.IteratorStepValue(rec, out JsValue value))
        {
            Agent.Current.Alloc(16);
            items.Add(value);
        }

        return JsValue.FromObject(Ops.CreateArray(items));
    }
}
