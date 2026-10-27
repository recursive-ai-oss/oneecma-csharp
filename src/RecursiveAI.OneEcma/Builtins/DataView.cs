namespace RecursiveAI.OneEcma;

/// <summary>A DataView (ECMA-262 25.3): a byte-addressed window onto a buffer, optionally tracking a resizable buffer's length.</summary>
internal sealed class JsDataView : JsObject
{
    internal JsArrayBuffer Buffer = null!;
    internal int ByteOffset;

    /// <summary>[[ByteLength]], or -1 for "auto".</summary>
    internal int FixedLength;

    internal JsDataView(JsObject proto)
        : base(proto)
    {
    }

    internal override string ClassName => "DataView";

    /// <summary>IsViewOutOfBounds (25.3.1.3).</summary>
    internal bool IsOutOfBounds
    {
        get
        {
            if (Buffer.Detached)
            {
                return true;
            }

            long bufLen = Buffer.ByteLength;
            long end = FixedLength < 0 ? bufLen : ByteOffset + (long)FixedLength;
            return ByteOffset > bufLen || end > bufLen;
        }
    }

    /// <summary>GetViewByteLength (25.3.1.2) for a view that is in bounds.</summary>
    internal int ViewByteLength => FixedLength >= 0 ? FixedLength : Buffer.ByteLength - ByteOffset;
}

internal static class DataViews
{
    private static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    private static JsDataView This(JsValue t, string method) =>
        t.Ref as JsDataView ?? throw ArrayBuffers.Incompatible(method, t);

    private static long Index(JsValue v, string message)
    {
        if (v.IsUndefined)
        {
            return 0;
        }

        double n = Ops.ToIntegerOrInfinity(v);
        if (n < 0 || n > 9007199254740991d)
        {
            throw Errors.Range(message);
        }

        return (long)n;
    }

    internal static void Setup(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%DataView.prototype%"] = proto;
        r.Ctor("DataView", 1, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor DataView requires 'new'");
            }

            if (A(a, 0).Ref is not JsArrayBuffer buffer)
            {
                throw Errors.Type("First argument to DataView constructor must be an ArrayBuffer");
            }

            string startMsg = "Start offset " + (A(a, 1).IsUndefined ? "0" : Ops.ToStr(A(a, 1))) + " is outside the bounds of the buffer";
            long offset = Index(A(a, 1), startMsg);
            if (buffer.Detached)
            {
                throw ArrayBuffers.Detached("DataView");
            }

            long bufLen = buffer.ByteLength;
            if (offset > bufLen)
            {
                throw Errors.Range("Start offset " + Ops.NumberToStr(offset) + " is outside the bounds of the buffer");
            }

            long viewLen = -1;
            if (A(a, 2).IsUndefined)
            {
                if (!buffer.Resizable)
                {
                    viewLen = bufLen - offset;
                }
            }
            else
            {
                viewLen = Index(A(a, 2), "Invalid DataView length " + Ops.ToStr(A(a, 2)));
                if (offset + viewLen > bufLen)
                {
                    throw Errors.Range("Invalid DataView length " + Ops.NumberToStr(viewLen));
                }
            }

            JsObject p = Ops.GetPrototypeFromConstructor(nt, rr => rr.Intrinsics["%DataView.prototype%"]);
            if (buffer.Detached)
            {
                throw ArrayBuffers.Detached("DataView");
            }

            bufLen = buffer.ByteLength;
            if (offset > bufLen)
            {
                throw Errors.Range("Start offset " + Ops.NumberToStr(offset) + " is outside the bounds of the buffer");
            }

            if (!A(a, 2).IsUndefined && offset + viewLen > bufLen)
            {
                throw Errors.Range("Invalid DataView length " + Ops.NumberToStr(viewLen));
            }

            return JsValue.FromObject(new JsDataView(p) { Buffer = buffer, ByteOffset = (int)offset, FixedLength = (int)viewLen });
        }, proto);

        r.Getter(proto, "buffer", "buffer", (t, a, nt) => JsValue.FromObject(This(t, "get DataView.prototype.buffer").Buffer));
        r.Getter(proto, "byteLength", "byteLength", (t, a, nt) =>
        {
            JsDataView v = This(t, "get DataView.prototype.byteLength");
            if (v.IsOutOfBounds)
            {
                throw ArrayBuffers.Detached("get DataView.prototype.byteLength");
            }

            return JsValue.FromNumber(v.ViewByteLength);
        });
        r.Getter(proto, "byteOffset", "byteOffset", (t, a, nt) =>
        {
            JsDataView v = This(t, "get DataView.prototype.byteOffset");
            if (v.IsOutOfBounds)
            {
                throw ArrayBuffers.Detached("get DataView.prototype.byteOffset");
            }

            return JsValue.FromNumber(v.ByteOffset);
        });
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("DataView"), PropFlags.Configurable);

        // 25.3.4.x get/set accessors for each element type, via GetViewValue (25.3.1.5) and SetViewValue (25.3.1.6).
        ElementKind[] kinds =
        [
            ElementKind.Int8, ElementKind.Uint8, ElementKind.Int16, ElementKind.Uint16, ElementKind.Int32, ElementKind.Uint32,
            ElementKind.Float16, ElementKind.Float32, ElementKind.Float64, ElementKind.BigInt64, ElementKind.BigUint64,
        ];
        foreach (ElementKind k in kinds)
        {
            string typeName = ElementIo.Names[(int)k][..^5];
            int size = ElementIo.Size(k);
            string getName = "get" + typeName;
            string setName = "set" + typeName;
            r.Method(proto, getName, 1, (t, a, nt) =>
            {
                JsDataView view = This(t, "DataView.prototype." + getName);
                long idx = Index(A(a, 0), "Offset is outside the bounds of the DataView");
                bool little = Ops.ToBoolean(A(a, 1));
                if (view.IsOutOfBounds)
                {
                    throw ArrayBuffers.Detached("DataView.prototype." + getName);
                }

                if (idx + size > view.ViewByteLength)
                {
                    throw Errors.Range("Offset is outside the bounds of the DataView");
                }

                return ElementIo.Read(k, view.Buffer.Data.AsSpan(view.ByteOffset + (int)idx), little);
            });
            r.Method(proto, setName, 2, (t, a, nt) =>
            {
                JsDataView view = This(t, "DataView.prototype." + setName);
                long idx = Index(A(a, 0), "Offset is outside the bounds of the DataView");
                JsValue num = ElementIo.ToElementValue(k, A(a, 1));
                bool little = Ops.ToBoolean(A(a, 2));
                if (view.IsOutOfBounds)
                {
                    throw ArrayBuffers.Detached("DataView.prototype." + setName);
                }

                if (idx + size > view.ViewByteLength)
                {
                    throw Errors.Range("Offset is outside the bounds of the DataView");
                }

                ElementIo.Write(k, view.Buffer.Data.AsSpan(view.ByteOffset + (int)idx), num, little);
                return default;
            });
        }
    }
}
