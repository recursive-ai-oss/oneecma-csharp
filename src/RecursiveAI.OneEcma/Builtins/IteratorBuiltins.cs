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
}
