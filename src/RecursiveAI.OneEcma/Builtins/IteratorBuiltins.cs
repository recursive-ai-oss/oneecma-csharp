namespace RecursiveAI.OneEcma;

/// <summary>An Array Iterator (23.1.5.1): the array-like, the next index and the kind (1 keys, 2 values, 3 entries).</summary>
internal sealed class ArrayIteratorObject : JsObject
{
    internal JsObject? Target;
    internal long Index;
    internal int Kind;

    internal ArrayIteratorObject(JsObject proto)
        : base(proto)
    {
    }
}

internal static partial class Builtins
{
    private static void SetupIterators(Realm r)
    {
        r.IteratorPrototype = r.NewObject();
        r.Method(r.IteratorPrototype, WellKnown.Iterator, "[Symbol.iterator]", 0, (t, a, nt) => t);
        r.AsyncIteratorPrototype = r.NewObject();
        r.Method(r.AsyncIteratorPrototype, WellKnown.AsyncIterator, "[Symbol.asyncIterator]", 0, (t, a, nt) => t);
        r.ArrayIteratorPrototype = new JsObject(r.IteratorPrototype);
        r.Method(r.ArrayIteratorPrototype, "next", 0, ArrayIteratorNext);
        r.ArrayIteratorPrototype.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Array Iterator"), PropFlags.Configurable);
        r.Intrinsics["%ArrayIteratorPrototype%"] = r.ArrayIteratorPrototype;
        IteratorHelpers.Setup(r);
    }

    /// <summary>%ArrayIteratorPrototype%.next (23.1.5.2.1), the body of the CreateArrayIterator closure (23.1.5.1).</summary>
    private static JsValue ArrayIteratorNext(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        if (t.Ref is not ArrayIteratorObject it)
        {
            throw Errors.Type("Method Array Iterator.prototype.next called on incompatible receiver " + Ops.DisplayValue(t));
        }

        if (it.Target is null)
        {
            return JsValue.FromObject(Ops.CreateIterResult(default, true));
        }

        long len = it.Target is JsTypedArray ita ? BinaryData.IteratedLength(ita) : Ops.LengthOfArrayLike(it.Target);
        long i = it.Index;
        if (i >= len)
        {
            it.Target = null;
            return JsValue.FromObject(Ops.CreateIterResult(default, true));
        }

        it.Index = i + 1;
        if (it.Kind == IterKeys)
        {
            return JsValue.FromObject(Ops.CreateIterResult(Ops.Num(i), false));
        }

        JsValue v = ArrayAlgo.Get(it.Target, i);
        return JsValue.FromObject(Ops.CreateIterResult(it.Kind == IterValues ? v : JsValue.FromObject(Ops.CreateArray([Ops.Num(i), v])), false));
    }

    private static JsValue MakeArrayIterator(Realm r, JsValue t, int kind)
    {
        JsObject o = Ops.ToObject(t);
        return JsValue.FromObject(new ArrayIteratorObject(r.ArrayIteratorPrototype) { Target = o, Kind = kind });
    }
}
