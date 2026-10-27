namespace RecursiveAI.OneEcma;

/// <summary>Entry points of the binary-data built-ins: ArrayBuffer, SharedArrayBuffer, TypedArrays, DataView and Atomics.</summary>
internal static class BinaryData
{
    internal static void Setup(Realm r)
    {
        ArrayBuffers.Setup(r);
        TypedArrays.Setup(r);
        DataViews.Setup(r);
        AtomicsBuiltin.Setup(r);

        // Math.f16round ( x ) (ES2025): the nearest Float16 value, as a Number.
        if (r.Global.Get("Math").Ref is JsObject math && !math.HasOwnProperty("f16round"))
        {
            r.Method(math, "f16round", 1, (t, a, nt) =>
            {
                double x = Ops.ToNumber(a.Length > 0 ? a[0] : default);
                return JsValue.FromNumber((double)(Half)x);
            });
        }
    }

    /// <summary>The host's <c>$262.detachArrayBuffer</c>: DetachArrayBuffer (25.1.3.5) with an undefined key.</summary>
    internal static JsValue HostDetach(JsValue v)
    {
        if (v.Ref is not JsArrayBuffer { Shared: false } b)
        {
            throw Errors.Type("detachArrayBuffer requires an ArrayBuffer");
        }

        b.Detach();
        return JsValue.Null;
    }

    /// <summary>The length an Array Iterator reads from a typed array; a detached or out-of-bounds one throws (23.1.5.1 step 1.b.i).</summary>
    internal static long IteratedLength(JsTypedArray ta)
    {
        if (ta.IsOutOfBounds)
        {
            throw ArrayBuffers.Detached("Array Iterator.prototype.next");
        }

        return ta.Length;
    }
}
