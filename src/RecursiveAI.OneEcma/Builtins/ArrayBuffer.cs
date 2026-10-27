namespace RecursiveAI.OneEcma;

/// <summary>
/// An ArrayBuffer or SharedArrayBuffer (ECMA-262 25.1, 25.2): a block of bytes, optionally resizable. <see cref="Data"/>
/// is always exactly the current byte length long; a resize replaces the array, which is unobservable because views refer
/// to the buffer object, never to the array.
/// </summary>
internal sealed class JsArrayBuffer : JsObject
{
    /// <summary>The largest block the host can hold (the .NET array limit).</summary>
    internal const long MaxBlockBytes = 0x7FFFFFC7;

    internal byte[] Data;

    /// <summary>[[ArrayBufferMaxByteLength]], or -1 for a fixed-length buffer.</summary>
    internal long MaxByteLength = -1;

    internal bool Shared;

    internal bool Detached;

    private JsArrayBuffer(JsObject proto, byte[] data)
        : base(proto)
    {
        Data = data;
    }

    internal override string ClassName => Shared ? "SharedArrayBuffer" : "ArrayBuffer";

    internal int ByteLength => Data.Length;

    internal bool Resizable => MaxByteLength >= 0;

    /// <summary>CreateByteDataBlock (6.2.9.1) and CreateSharedByteDataBlock: charges the memory limit before allocating.</summary>
    internal static byte[] CreateBlock(long size)
    {
        if (size > MaxBlockBytes)
        {
            throw Errors.Range("Array buffer allocation failed");
        }

        if (size == 0)
        {
            return [];
        }

        Agent.Current.Alloc(size);
        try
        {
            return new byte[size];
        }
        catch (OutOfMemoryException)
        {
            throw Errors.Range("Array buffer allocation failed");
        }
    }

    /// <summary>AllocateArrayBuffer (25.1.3.1) / AllocateSharedArrayBuffer (25.2.2.1) after the prototype is known.</summary>
    internal static JsArrayBuffer Allocate(JsObject proto, long byteLength, long maxByteLength = -1, bool shared = false)
    {
        // Step 3: byteLength > maxByteLength is checked before anything is created.
        if (maxByteLength >= 0 && byteLength > maxByteLength)
        {
            throw Errors.Range("Invalid array buffer max length");
        }

        return AllocateAfterProto(proto, byteLength, maxByteLength, shared);
    }

    private static JsArrayBuffer AllocateAfterProto(JsObject proto, long byteLength, long maxByteLength, bool shared)
    {
        byte[] block = CreateBlock(byteLength);
        if (maxByteLength > MaxBlockBytes)
        {
            throw Errors.Range("Array buffer allocation failed");
        }

        return new JsArrayBuffer(proto, block) { MaxByteLength = maxByteLength, Shared = shared };
    }

    /// <summary>A fresh fixed-length %ArrayBuffer% of the given size (used by typed array allocation and cloning).</summary>
    internal static JsArrayBuffer NewDefault(long byteLength) =>
        AllocateAfterProto(Ops.CurrentRealm.Intrinsics["%ArrayBuffer.prototype%"], byteLength, -1, false);

    /// <summary>DetachArrayBuffer (25.1.3.5) with an undefined detach key.</summary>
    internal void Detach()
    {
        Data = [];
        Detached = true;
    }

    /// <summary>Resizes in place (ArrayBuffer.prototype.resize, SharedArrayBuffer.prototype.grow); new bytes are zero.</summary>
    internal void ResizeTo(long newLength)
    {
        if (newLength == Data.Length)
        {
            return;
        }

        byte[] nb = CreateBlock(newLength);
        Array.Copy(Data, nb, Math.Min(newLength, Data.Length));
        Data = nb;
    }
}

internal static class ArrayBuffers
{
    private static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    internal static JsException Incompatible(string method, JsValue t) =>
        Errors.Type("Method " + method + " called on incompatible receiver " + (t.IsString ? t.AsString : Ops.DisplayValue(t)));

    internal static JsException Detached(string op) => Errors.Type("Cannot perform " + op + " on a detached ArrayBuffer");

    private static JsArrayBuffer This(JsValue t, bool shared, string method) =>
        t.Ref is JsArrayBuffer b && b.Shared == shared ? b : throw Incompatible(method, t);

    /// <summary>ToIndex with the message V8 uses for buffer sizes.</summary>
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

    /// <summary>GetArrayBufferMaxByteLengthOption (25.1.3.7); -1 stands for "empty".</summary>
    private static long MaxOption(JsValue options)
    {
        if (options.Ref is not JsObject o)
        {
            return -1;
        }

        JsValue m = o.Get("maxByteLength");
        return m.IsUndefined ? -1 : Index(m, "Invalid array buffer max length");
    }

    /// <summary>Relative index clamp used by slice (steps 6-13).</summary>
    internal static long Clamp(JsValue v, long len, long dflt)
    {
        if (v.IsUndefined)
        {
            return dflt;
        }

        double rel = Ops.ToIntegerOrInfinity(v);
        if (rel < 0)
        {
            return rel == double.NegativeInfinity ? 0 : (long)Math.Max(len + rel, 0);
        }

        return (long)Math.Min(rel, len);
    }

    internal static void Setup(Realm r)
    {
        SetupArrayBuffer(r);
        SetupShared(r);
    }

    private static JsArrayBuffer Create(JsObject? nt, string protoName, long byteLength, long max, bool shared)
    {
        // AllocateArrayBuffer step 3 precedes step 4's prototype lookup.
        if (max >= 0 && byteLength > max)
        {
            throw Errors.Range("Invalid array buffer max length");
        }

        JsObject proto = nt is null
            ? Ops.CurrentRealm.Intrinsics[protoName]
            : Ops.GetPrototypeFromConstructor(nt, rr => rr.Intrinsics[protoName]);
        return JsArrayBuffer.Allocate(proto, byteLength, max, shared);
    }

    private static void SetupArrayBuffer(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%ArrayBuffer.prototype%"] = proto;
        NativeFunction ctor = r.Ctor("ArrayBuffer", 1, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor ArrayBuffer requires 'new'");
            }

            long len = Index(A(a, 0), "Invalid array buffer length");
            long max = MaxOption(A(a, 1));
            return JsValue.FromObject(Create(nt, "%ArrayBuffer.prototype%", len, max, false));
        }, proto);
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        r.Method(ctor, "isView", 1, (t, a, nt) => Ops.Bool(A(a, 0).Ref is JsTypedArray or JsDataView));

        r.Getter(proto, "byteLength", "byteLength", (t, a, nt) =>
            Ops.Num(This(t, false, "get ArrayBuffer.prototype.byteLength").ByteLength));
        r.Getter(proto, "maxByteLength", "maxByteLength", (t, a, nt) =>
        {
            JsArrayBuffer b = This(t, false, "get ArrayBuffer.prototype.maxByteLength");
            return Ops.Num(b.Detached ? 0 : b.Resizable ? b.MaxByteLength : b.ByteLength);
        });
        r.Getter(proto, "resizable", "resizable", (t, a, nt) =>
            Ops.Bool(This(t, false, "get ArrayBuffer.prototype.resizable").Resizable));
        r.Getter(proto, "detached", "detached", (t, a, nt) =>
            Ops.Bool(This(t, false, "get ArrayBuffer.prototype.detached").Detached));

        // 25.1.6.6 ArrayBuffer.prototype.resize ( newLength )
        r.Method(proto, "resize", 1, (t, a, nt) =>
        {
            JsArrayBuffer b = t.Ref is JsArrayBuffer { Shared: false, Resizable: true } x ? x : throw Incompatible("ArrayBuffer.prototype.resize", t);
            long n = Index(A(a, 0), "ArrayBuffer.prototype.resize: Invalid length parameter");
            if (b.Detached)
            {
                throw Detached("ArrayBuffer.prototype.resize");
            }

            if (n > b.MaxByteLength)
            {
                throw Errors.Range("ArrayBuffer.prototype.resize: Invalid length parameter");
            }

            b.ResizeTo(n);
            return default;
        });

        // 25.1.6.7 ArrayBuffer.prototype.slice ( start, end )
        r.Method(proto, "slice", 2, (t, a, nt) =>
        {
            JsArrayBuffer o = This(t, false, "ArrayBuffer.prototype.slice");
            if (o.Detached)
            {
                throw Detached("ArrayBuffer.prototype.slice");
            }

            long len = o.ByteLength;
            long first = Clamp(A(a, 0), len, 0);
            long fin = Clamp(A(a, 1), len, len);
            long newLen = Math.Max(fin - first, 0);
            JsObject c = Ops.SpeciesConstructor(o, r.Intrinsics["%ArrayBuffer%"]);
            JsValue nv = Ops.Construct(c, [Ops.Num(newLen)]) is { } no ? JsValue.FromObject(no) : default;
            if (nv.Ref is not JsArrayBuffer nb || nb.Shared)
            {
                throw Incompatible("ArrayBuffer.prototype.slice", nv);
            }

            if (nb.Detached)
            {
                throw Detached("ArrayBuffer.prototype.slice");
            }

            if (ReferenceEquals(nb, o))
            {
                throw Errors.Type("ArrayBuffer subclass returned this from species constructor");
            }

            if (nb.ByteLength < newLen)
            {
                throw Errors.Type("Species constructor returned a too small ArrayBuffer");
            }

            if (o.Detached)
            {
                throw Detached("ArrayBuffer.prototype.slice");
            }

            long cur = o.ByteLength;
            if (first < cur)
            {
                long count = Math.Min(newLen, cur - first);
                Agent.Current.Charge(count >> 4);
                Array.Copy(o.Data, first, nb.Data, 0, count);
            }

            return nv;
        });

        r.Method(proto, "transfer", 0, (t, a, nt) => CopyAndDetach(r, t, A(a, 0), true, "ArrayBuffer.prototype.transfer"));
        r.Method(proto, "transferToFixedLength", 0, (t, a, nt) => CopyAndDetach(r, t, A(a, 0), false, "ArrayBuffer.prototype.transferToFixedLength"));
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("ArrayBuffer"), PropFlags.Configurable);
    }

    /// <summary>ArrayBufferCopyAndDetach (25.1.3.3).</summary>
    private static JsValue CopyAndDetach(Realm r, JsValue t, JsValue newLength, bool preserve, string method)
    {
        JsArrayBuffer b = This(t, false, method);
        long n = newLength.IsUndefined ? b.ByteLength : Index(newLength, "Invalid array buffer length");
        if (b.Detached)
        {
            throw Detached(method);
        }

        long max = preserve && b.Resizable ? b.MaxByteLength : -1;
        JsArrayBuffer nb = Create(null, "%ArrayBuffer.prototype%", n, max, false);
        long copy = Math.Min(n, b.ByteLength);
        Agent.Current.Charge(copy >> 4);
        Array.Copy(b.Data, nb.Data, copy);
        b.Detach();
        return JsValue.FromObject(nb);
    }

    private static void SetupShared(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%SharedArrayBuffer.prototype%"] = proto;
        NativeFunction ctor = r.Ctor("SharedArrayBuffer", 1, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Constructor SharedArrayBuffer requires 'new'");
            }

            long len = Index(A(a, 0), "Invalid array buffer length");
            long max = MaxOption(A(a, 1));
            return JsValue.FromObject(Create(nt, "%SharedArrayBuffer.prototype%", len, max, true));
        }, proto);
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        r.Getter(proto, "byteLength", "byteLength", (t, a, nt) =>
            Ops.Num(This(t, true, "get SharedArrayBuffer.prototype.byteLength").ByteLength));
        r.Getter(proto, "growable", "growable", (t, a, nt) =>
            Ops.Bool(This(t, true, "get SharedArrayBuffer.prototype.growable").Resizable));
        r.Getter(proto, "maxByteLength", "maxByteLength", (t, a, nt) =>
        {
            JsArrayBuffer b = This(t, true, "get SharedArrayBuffer.prototype.maxByteLength");
            return Ops.Num(b.Resizable ? b.MaxByteLength : b.ByteLength);
        });

        // 25.2.5.3 SharedArrayBuffer.prototype.grow ( newLength )
        r.Method(proto, "grow", 1, (t, a, nt) =>
        {
            JsArrayBuffer b = t.Ref is JsArrayBuffer { Shared: true, Resizable: true } x ? x : throw Incompatible("SharedArrayBuffer.prototype.grow", t);
            long n = Index(A(a, 0), "SharedArrayBuffer.prototype.grow: Invalid length parameter");
            if (n > b.MaxByteLength || n < b.ByteLength)
            {
                throw Errors.Range("SharedArrayBuffer.prototype.grow: Invalid length parameter");
            }

            b.ResizeTo(n);
            return default;
        });

        // 25.2.5.6 SharedArrayBuffer.prototype.slice ( start, end )
        r.Method(proto, "slice", 2, (t, a, nt) =>
        {
            JsArrayBuffer o = This(t, true, "SharedArrayBuffer.prototype.slice");
            long len = o.ByteLength;
            long first = Clamp(A(a, 0), len, 0);
            long fin = Clamp(A(a, 1), len, len);
            long newLen = Math.Max(fin - first, 0);
            JsObject c = Ops.SpeciesConstructor(o, r.Intrinsics["%SharedArrayBuffer%"]);
            JsObject no = Ops.Construct(c, [Ops.Num(newLen)]);
            if (no is not JsArrayBuffer { Shared: true } nb)
            {
                throw Incompatible("SharedArrayBuffer.prototype.slice", JsValue.FromObject(no));
            }

            if (ReferenceEquals(nb, o))
            {
                throw Errors.Type("SharedArrayBuffer subclass returned this from species constructor");
            }

            if (nb.ByteLength < newLen)
            {
                throw Errors.Type("Species constructor returned a too small SharedArrayBuffer");
            }

            Agent.Current.Charge(newLen >> 4);
            Array.Copy(o.Data, first, nb.Data, 0, newLen);
            return JsValue.FromObject(nb);
        });
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("SharedArrayBuffer"), PropFlags.Configurable);
    }
}
