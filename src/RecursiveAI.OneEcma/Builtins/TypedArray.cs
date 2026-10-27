using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>%TypedArray%, the eleven concrete constructors and Float16Array (ECMA-262 23.2).</summary>
internal static partial class TypedArrays
{
    private static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    private static JsValue Num(double d) => JsValue.FromNumber(d);

    private static string KindText(JsValue v) => Ops.KindText(v);

    private static JsException NotFunction(JsValue v) => Errors.Type(Ops.DisplayValue(v) + " is not a function");

    private static JsException NotFunctionKind(JsValue v) => Errors.Type(KindText(v) + " is not a function");

    private static JsException DetachedOp(string method) => ArrayBuffers.Detached(method);

    /// <summary>RequireInternalSlot(O, [[TypedArrayName]]).</summary>
    private static JsTypedArray Require(JsValue t) => t.Ref as JsTypedArray ?? throw Errors.Type("this is not a typed array.");

    /// <summary>ValidateTypedArray (23.2.4.4): a typed array that is neither detached nor out of bounds.</summary>
    private static JsTypedArray Validate(JsValue t, string method)
    {
        JsTypedArray ta = Require(t);
        if (ta.IsOutOfBounds)
        {
            throw DetachedOp("%TypedArray%.prototype." + method);
        }

        return ta;
    }

    private static void Work(long n) => Agent.Current.Charge((n >> 3) + 1);

    private static JsObject ProtoFor(Realm r, ElementKind k) => r.Intrinsics["%" + ElementIo.Names[(int)k] + ".prototype%"];

    // ---- creation ---------------------------------------------------------------------------------------

    private static JsTypedArray Make(JsObject proto, ElementKind k, JsArrayBuffer buf, long byteOffset, int fixedLength) =>
        new(proto, k) { Buffer = buf, ByteOffset = (int)byteOffset, FixedLength = fixedLength };

    /// <summary>AllocateTypedArray with a length (23.2.5.1.1 and 23.2.5.1.6): a fresh fixed-length buffer.</summary>
    private static JsTypedArray AllocateLength(JsObject proto, ElementKind k, long length)
    {
        long bytes = length * ElementIo.Size(k);
        if (bytes > JsArrayBuffer.MaxBlockBytes)
        {
            throw Errors.Range("Invalid typed array length: " + Ops.NumberToStr(length));
        }

        JsArrayBuffer buf = JsArrayBuffer.NewDefault(bytes);
        return Make(proto, k, buf, 0, (int)length);
    }

    /// <summary>TypedArrayCreateSameType (23.2.4.2).</summary>
    private static JsTypedArray CreateSameType(JsTypedArray exemplar, long length) =>
        AllocateLength(ProtoFor(Ops.CurrentRealm, exemplar.Kind), exemplar.Kind, length);

    /// <summary>TypedArrayCreateFromConstructor (23.2.4.1).</summary>
    private static JsTypedArray CreateFromConstructor(JsObject ctor, ReadOnlySpan<JsValue> args, string? method = null)
    {
        JsObject o = Ops.Construct(ctor, args);
        JsTypedArray ta = o as JsTypedArray ?? (method is null ? Require(JsValue.FromObject(o)) : throw ArrayBuffers.Incompatible(method, JsValue.FromObject(o)));
        if (ta.IsOutOfBounds)
        {
            throw DetachedOp("Construct");
        }

        if (args.Length == 1 && args[0].IsNumber && ta.Length < args[0].Num)
        {
            throw Errors.Type("Derived TypedArray constructor created an array which was too small");
        }

        return ta;
    }

    /// <summary>TypedArraySpeciesCreate (23.2.4.3).</summary>
    private static JsTypedArray SpeciesCreate(JsTypedArray exemplar, ReadOnlySpan<JsValue> args)
    {
        Realm r = Ops.CurrentRealm;
        JsObject dflt = r.Intrinsics["%" + ElementIo.Names[(int)exemplar.Kind] + "%"];
        JsObject c = Ops.SpeciesConstructor(exemplar, dflt);
        JsTypedArray res = CreateFromConstructor(c, args);
        if (res.IsBigInt != exemplar.IsBigInt)
        {
            throw Errors.Type("Content type of the species-created typed array does not match");
        }

        return res;
    }

    private static JsException MixError() => Errors.Type("Cannot mix BigInt and other types, use explicit conversions");

    // ---- constructors -----------------------------------------------------------------------------------

    /// <summary>ToIndex for a length or offset with V8's message.</summary>
    private static long Index(JsValue v, Func<string> message)
    {
        if (v.IsUndefined)
        {
            return 0;
        }

        double n = Ops.ToIntegerOrInfinity(v);
        if (n < 0 || n > 9007199254740991d)
        {
            throw Errors.Range(message());
        }

        return (long)n;
    }

    private static JsValue Construct(Realm r, ElementKind k, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        string name = ElementIo.Names[(int)k];
        if (nt is null)
        {
            throw Errors.Type("Constructor " + name + " requires 'new'");
        }

        string protoName = "%" + name + ".prototype%";
        JsObject Proto() => Ops.GetPrototypeFromConstructor(nt, rr => rr.Intrinsics[protoName]);
        if (a.Length == 0)
        {
            return JsValue.FromObject(AllocateLength(Proto(), k, 0));
        }

        JsValue first = a[0];
        if (first.Ref is not JsObject fo)
        {
            long n = Index(first, () => "Invalid typed array length: " + Ops.ToStr(first));
            return JsValue.FromObject(AllocateLength(Proto(), k, n));
        }

        JsObject proto = Proto();
        switch (fo)
        {
            case JsTypedArray src:
                return JsValue.FromObject(FromTypedArray(proto, k, src));
            case JsArrayBuffer buf:
                return JsValue.FromObject(FromBuffer(proto, k, buf, A(a, 1), A(a, 2), name));
            default:
                {
                    JsValue usingIterator = Ops.GetMethod(first, new PropertyKey(WellKnown.Iterator));
                    if (!usingIterator.IsUndefined)
                    {
                        List<JsValue> values = Ops.IterableToList(first, usingIterator);
                        JsTypedArray o = AllocateLength(proto, k, values.Count);
                        for (int i = 0; i < values.Count; i++)
                        {
                            Agent.Current.Tick();
                            o.SetByNumber(i, values[i]);
                        }

                        return JsValue.FromObject(o);
                    }

                    long len = Ops.LengthOfArrayLike(fo);
                    JsTypedArray ta = AllocateLength(proto, k, len);
                    for (long i = 0; i < len; i++)
                    {
                        Agent.Current.Tick();
                        ta.SetByNumber(i, fo.Get(new PropertyKey(Ops.NumberToStr(i))));
                    }

                    return JsValue.FromObject(ta);
                }
        }
    }

    /// <summary>InitializeTypedArrayFromTypedArray (23.2.5.1.2).</summary>
    private static JsTypedArray FromTypedArray(JsObject proto, ElementKind k, JsTypedArray src)
    {
        if (src.IsOutOfBounds)
        {
            throw DetachedOp("Construct");
        }

        int len = src.Length;
        JsTypedArray o = AllocateLength(proto, k, len);
        Work(len);
        if (k == src.Kind)
        {
            Array.Copy(src.Buffer.Data, src.ByteOffset, o.Buffer.Data, 0, (long)len * o.ElementSize);
        }
        else
        {
            if (src.IsBigInt != o.IsBigInt)
            {
                throw MixError();
            }

            for (int i = 0; i < len; i++)
            {
                o.SetElementRaw(i, src.GetElement(i));
            }
        }

        return o;
    }

    /// <summary>InitializeTypedArrayFromArrayBuffer (23.2.5.1.3).</summary>
    private static JsTypedArray FromBuffer(JsObject proto, ElementKind k, JsArrayBuffer buf, JsValue byteOffset, JsValue length, string name)
    {
        int size = ElementIo.Size(k);
        long offset = Index(byteOffset, () => "Start offset " + Ops.ToStr(byteOffset) + " is outside the bounds of the buffer");
        if (offset % size != 0)
        {
            throw Errors.Range("start offset of " + name + " should be a multiple of " + size.ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
        }

        long newLength = 0;
        if (!length.IsUndefined)
        {
            newLength = Index(length, () => "Invalid typed array length: " + Ops.ToStr(length));
        }

        if (buf.Detached)
        {
            throw DetachedOp("Construct");
        }

        long bufLen = buf.ByteLength;
        if (length.IsUndefined && buf.Resizable)
        {
            if (offset > bufLen)
            {
                throw Errors.Range("Start offset " + offset.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " is outside the bounds of the buffer");
            }

            return Make(proto, k, buf, offset, -1);
        }

        long newByteLength;
        if (length.IsUndefined)
        {
            if (bufLen % size != 0)
            {
                throw Errors.Range("byte length of " + name + " should be a multiple of " + size.ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
            }

            newByteLength = bufLen - offset;
            if (newByteLength < 0)
            {
                throw Errors.Range("Start offset " + offset.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " is outside the bounds of the buffer");
            }
        }
        else
        {
            newByteLength = newLength * size;
            if (offset + newByteLength > bufLen)
            {
                throw Errors.Range("Invalid typed array length: " + newLength.ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
            }
        }

        return Make(proto, k, buf, offset, (int)(newByteLength / size));
    }

    // ---- setup --------------------------------------------------------------------------------------------

    internal static void Setup(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%TypedArray.prototype%"] = proto;
        var ctor = new NativeFunction(r, "TypedArray", 0, (t, a, nt) => throw Errors.Type("Abstract class TypedArray not directly constructable"), true);
        ctor.DefineDirect("prototype", JsValue.FromObject(proto), PropFlags.None);
        proto.DefineDirect("constructor", JsValue.FromObject(ctor), PropFlags.Hidden);
        r.Intrinsics["%TypedArray%"] = ctor;
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        SetupStatics(r, ctor);
        SetupAccessors(r, proto);
        SetupMethods(r, proto);
        SetupSearch(r, proto);
        SetupMutators(r, proto);
        SetupOrdering(r, proto);

        for (int i = 0; i < ElementIo.Names.Length; i++)
        {
            var k = (ElementKind)i;
            string name = ElementIo.Names[i];
            var p = new JsObject(proto);
            r.Intrinsics["%" + name + ".prototype%"] = p;
            NativeFunction c = r.Ctor(name, 3, (t, a, nt) => Construct(r, k, a, nt), p, ctor);
            var bpe = Num(ElementIo.Size(k));
            c.DefineDirect("BYTES_PER_ELEMENT", bpe, PropFlags.None);
            p.DefineDirect("BYTES_PER_ELEMENT", bpe, PropFlags.None);
        }

        SetupUint8Extras(r);
    }

    private static void SetupStatics(Realm r, NativeFunction ctor)
    {
        // 23.2.2.1 %TypedArray%.from ( source [ , mapfn [ , thisArg ] ] )
        r.Method(ctor, "from", 1, (t, a, nt) =>
        {
            if (!Ops.IsConstructor(t))
            {
                throw Errors.Type("Method %TypedArray%.from called on incompatible receiver " + (t.IsString ? t.AsString : Ops.DisplayValue(t)));
            }

            var c = (JsObject)t.Ref!;
            JsValue mapfn = A(a, 1);
            JsValue thisArg = A(a, 2);
            bool mapping = !mapfn.IsUndefined;
            if (mapping && !Ops.IsCallable(mapfn))
            {
                throw NotFunctionKind(mapfn);
            }

            JsValue source = A(a, 0);
            JsValue usingIterator = Ops.GetMethod(source, new PropertyKey(WellKnown.Iterator));
            if (!usingIterator.IsUndefined)
            {
                List<JsValue> values = Ops.IterableToList(source, usingIterator);
                JsTypedArray target = CreateFromConstructor(c, [Num(values.Count)], "%TypedArray%.from");
                for (int k = 0; k < values.Count; k++)
                {
                    Agent.Current.Tick();
                    JsValue mapped = mapping ? Ops.Call(mapfn, thisArg, [values[k], Num(k)]) : values[k];
                    target.SetByNumber(k, mapped);
                }

                return JsValue.FromObject(target);
            }

            JsObject arrayLike = Ops.ToObject(source);
            long len = Ops.LengthOfArrayLike(arrayLike);
            JsTypedArray tgt = CreateFromConstructor(c, [Num(len)], "%TypedArray%.from");
            for (long k = 0; k < len; k++)
            {
                Agent.Current.Tick();
                JsValue kValue = arrayLike.Get(new PropertyKey(Ops.NumberToStr(k)));
                JsValue mapped = mapping ? Ops.Call(mapfn, thisArg, [kValue, Num(k)]) : kValue;
                tgt.SetByNumber(k, mapped);
            }

            return JsValue.FromObject(tgt);
        });

        // 23.2.2.2 %TypedArray%.of ( ...items )
        r.Method(ctor, "of", 0, (t, a, nt) =>
        {
            if (!Ops.IsConstructor(t))
            {
                throw Errors.Type("Method %TypedArray%.of called on incompatible receiver " + (t.IsString ? t.AsString : Ops.DisplayValue(t)));
            }

            JsTypedArray target = CreateFromConstructor((JsObject)t.Ref!, [Num(a.Length)], "%TypedArray%.of");
            for (int k = 0; k < a.Length; k++)
            {
                target.SetByNumber(k, a[k]);
            }

            return JsValue.FromObject(target);
        });
    }

    private static void SetupAccessors(Realm r, JsObject proto)
    {
        JsTypedArray Get(JsValue t, string n) => t.Ref as JsTypedArray ?? throw Errors.Type("Method get TypedArray.prototype." + n + " called on incompatible receiver " + (t.Ref is JsObject ro ? "[object " + ro.ClassName + "]" : Ops.DisplayValue(t)));
        r.Getter(proto, "buffer", "buffer", (t, a, nt) => JsValue.FromObject(Get(t, "buffer").Buffer));
        r.Getter(proto, "byteLength", "byteLength", (t, a, nt) => Num(Get(t, "byteLength").ByteLength));
        r.Getter(proto, "byteOffset", "byteOffset", (t, a, nt) =>
        {
            JsTypedArray ta = Get(t, "byteOffset");
            return Num(ta.IsOutOfBounds ? 0 : ta.ByteOffset);
        });
        r.Getter(proto, "length", "length", (t, a, nt) => Num(Get(t, "length").Length));

        // 23.2.3.38 get %TypedArray%.prototype [ %Symbol.toStringTag% ]
        r.Getter(proto, new PropertyKey(WellKnown.ToStringTag), "[Symbol.toStringTag]", (t, a, nt) =>
            t.Ref is JsTypedArray ta ? Ops.Str(ElementIo.Names[(int)ta.Kind]) : default);
    }

    // ---- iteration-style methods ------------------------------------------------------------------------

    private static void SetupMethods(Realm r, JsObject proto)
    {
        // 23.2.3.1 at ( index )
        r.Method(proto, "at", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "at");
            long len = o.Length;
            double rel = Ops.ToIntegerOrInfinity(A(a, 0));
            double k = rel >= 0 ? rel : len + rel;
            return k < 0 || k >= len ? default : o.GetByNumber(k);
        });

        void Visit(string name, int length, int mode)
        {
            // mode: 0 forEach, 1 every, 2 some, 3 find, 4 findIndex, 5 findLast, 6 findLastIndex
            r.Method(proto, name, length, (t, a, nt) =>
            {
                JsTypedArray o = Validate(t, name);
                long len = o.Length;
                JsValue cb = A(a, 0);
                if (!Ops.IsCallable(cb))
                {
                    throw mode >= 3 ? NotFunctionKind(cb) : NotFunction(cb);
                }

                bool reverse = mode >= 5;
                for (long i = 0; i < len; i++)
                {
                    Agent.Current.Tick();
                    long k = reverse ? len - 1 - i : i;
                    JsValue kv = o.GetByNumber(k);
                    JsValue res = Ops.Call(cb, A(a, 1), [kv, Num(k), t]);
                    switch (mode)
                    {
                        case 1 when !Ops.ToBoolean(res):
                            return JsValue.False;
                        case 2 when Ops.ToBoolean(res):
                            return JsValue.True;
                        case 3 or 5 when Ops.ToBoolean(res):
                            return kv;
                        case 4 or 6 when Ops.ToBoolean(res):
                            return Num(k);
                    }
                }

                return mode switch
                {
                    1 => JsValue.True,
                    2 => JsValue.False,
                    4 or 6 => Num(-1),
                    _ => default,
                };
            });
        }

        Visit("every", 1, 1);
        Visit("some", 1, 2);
        Visit("forEach", 1, 0);
        Visit("find", 1, 3);
        Visit("findIndex", 1, 4);
        Visit("findLast", 1, 5);
        Visit("findLastIndex", 1, 6);

        // 23.2.3.8 filter ( callback [ , thisArg ] )
        r.Method(proto, "filter", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "filter");
            long len = o.Length;
            JsValue cb = A(a, 0);
            if (!Ops.IsCallable(cb))
            {
                throw NotFunction(cb);
            }

            var kept = new List<JsValue>();
            for (long k = 0; k < len; k++)
            {
                Agent.Current.Tick();
                JsValue kv = o.GetByNumber(k);
                if (Ops.ToBoolean(Ops.Call(cb, A(a, 1), [kv, Num(k), t])))
                {
                    kept.Add(kv);
                }
            }

            JsTypedArray res = SpeciesCreate(o, [Num(kept.Count)]);
            for (int n = 0; n < kept.Count; n++)
            {
                res.SetByNumber(n, kept[n]);
            }

            return JsValue.FromObject(res);
        });

        // 23.2.3.22 map ( callback [ , thisArg ] )
        r.Method(proto, "map", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "map");
            long len = o.Length;
            JsValue cb = A(a, 0);
            if (!Ops.IsCallable(cb))
            {
                throw NotFunction(cb);
            }

            JsTypedArray res = SpeciesCreate(o, [Num(len)]);
            for (long k = 0; k < len; k++)
            {
                Agent.Current.Tick();
                JsValue mapped = Ops.Call(cb, A(a, 1), [o.GetByNumber(k), Num(k), t]);
                res.SetByNumber(k, mapped);
            }

            return JsValue.FromObject(res);
        });

        // 23.2.3.23 reduce / 23.2.3.24 reduceRight
        void Reduce(string name, bool right)
        {
            r.Method(proto, name, 1, (t, a, nt) =>
            {
                JsTypedArray o = Validate(t, name);
                long len = o.Length;
                JsValue cb = A(a, 0);
                if (!Ops.IsCallable(cb))
                {
                    throw NotFunction(cb);
                }

                if (len == 0 && a.Length < 2)
                {
                    throw Errors.Type("Reduce of empty array with no initial value");
                }

                long i = 0;
                JsValue acc;
                if (a.Length >= 2)
                {
                    acc = a[1];
                }
                else
                {
                    acc = o.GetByNumber(right ? len - 1 : 0);
                    i = 1;
                }

                for (; i < len; i++)
                {
                    Agent.Current.Tick();
                    long k = right ? len - 1 - i : i;
                    acc = Ops.Call(cb, default, [acc, o.GetByNumber(k), Num(k), t]);
                }

                return acc;
            });
        }

        Reduce("reduce", false);
        Reduce("reduceRight", true);

        // 23.2.3.18 join ( separator )
        r.Method(proto, "join", 1, (t, a, nt) =>
        {
            JsTypedArray o = t.Ref as JsTypedArray ?? throw ArrayBuffers.Incompatible("%TypedArray%.prototype.join", t);
            if (o.IsOutOfBounds)
            {
                throw DetachedOp("%TypedArray%.prototype.join");
            }

            long len = o.Length;
            string sep = A(a, 0).IsUndefined ? "," : Ops.ToStr(A(a, 0));
            return Ops.Str(Join(o, len, sep));
        });

        // 23.2.3.31 toLocaleString ( [ reserved1 [ , reserved2 ] ] ): Array.prototype.toLocaleString over TypedArrayLength.
        r.Method(proto, "toLocaleString", 0, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "toLocaleString");
            long len = o.Length;
            var sb = new StringBuilder();
            int max = Agent.Current.Limits.MaxStringLength;
            for (long k = 0; k < len; k++)
            {
                Agent.Current.Tick();
                if (k > 0)
                {
                    sb.Append(',');
                }

                JsValue el = o.GetByNumber(k);
                if (!el.IsNullish)
                {
                    sb.Append(Ops.ToStr(Ops.Invoke(el, "toLocaleString", [A(a, 0), A(a, 1)])));
                }

                if (sb.Length > max)
                {
                    throw Errors.Range("Invalid string length");
                }
            }

            return Ops.Str(sb.ToString());
        });

        // 23.2.3.34 toString is the same function object as Array.prototype.toString.
        proto.DefineDirect("toString", r.ArrayPrototype.Get("toString"), PropFlags.Hidden);

        // 23.2.3.37 / 23.2.3.19 / 23.2.3.7: the iterators reuse the Array iterator machinery after ValidateTypedArray.
        JsValue arrValues = r.ArrayPrototype.Get("values");
        JsValue arrKeys = r.ArrayPrototype.Get("keys");
        JsValue arrEntries = r.ArrayPrototype.Get("entries");
        NativeFunction values = r.Method(proto, "values", 0, (t, a, nt) =>
        {
            Validate(t, "values");
            return Ops.Call(arrValues, t);
        });
        proto.DefineDirect(new PropertyKey(WellKnown.Iterator), JsValue.FromObject(values), PropFlags.Hidden);
        r.Method(proto, "keys", 0, (t, a, nt) =>
        {
            Validate(t, "keys");
            return Ops.Call(arrKeys, t);
        });
        r.Method(proto, "entries", 0, (t, a, nt) =>
        {
            Validate(t, "entries");
            return Ops.Call(arrEntries, t);
        });
    }

    private static string Join(JsTypedArray o, long len, string sep)
    {
        var sb = new StringBuilder();
        int max = Agent.Current.Limits.MaxStringLength;
        long charged = 0;
        for (long k = 0; k < len; k++)
        {
            if ((k & 63) == 0)
            {
                Agent.Current.Tick();
                Agent.Current.Alloc((sb.Length - charged) * 2);
                charged = sb.Length;
            }

            if (k > 0)
            {
                sb.Append(sep);
            }

            JsValue el = o.GetByNumber(k);
            if (!el.IsUndefined)
            {
                sb.Append(el.IsNumber ? Ops.NumberToStr(el.Num) : Ops.ToStr(el));
            }

            if (sb.Length > max)
            {
                throw Errors.Range("Invalid string length");
            }
        }

        return sb.ToString();
    }

    // ---- searching ----------------------------------------------------------------------------------------

    private static void SetupSearch(Realm r, JsObject proto)
    {
        // 23.2.3.16 includes ( searchElement [ , fromIndex ] )
        r.Method(proto, "includes", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "includes");
            long len = o.Length;
            if (len == 0)
            {
                return JsValue.False;
            }

            double n = Ops.ToIntegerOrInfinity(A(a, 1));
            if (n == double.PositiveInfinity)
            {
                return JsValue.False;
            }

            double k = n >= 0 ? (n == double.NegativeInfinity ? 0 : n) : Math.Max(len + n, 0);
            Work(len);
            for (; k < len; k++)
            {
                if (Ops.SameValueZero(A(a, 0), o.GetByNumber(k)))
                {
                    return JsValue.True;
                }
            }

            return JsValue.False;
        });

        // 23.2.3.14 indexOf ( searchElement [ , fromIndex ] )
        r.Method(proto, "indexOf", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "indexOf");
            long len = o.Length;
            if (len == 0)
            {
                return Num(-1);
            }

            double n = Ops.ToIntegerOrInfinity(A(a, 1));
            if (n == double.PositiveInfinity)
            {
                return Num(-1);
            }

            double k = n >= 0 ? (n == double.NegativeInfinity ? 0 : n) : Math.Max(len + n, 0);
            Work(len);
            for (; k < len; k++)
            {
                if (k < o.Length && Ops.StrictEquals(A(a, 0), o.GetElement((int)k)))
                {
                    return Num(k);
                }
            }

            return Num(-1);
        });

        // 23.2.3.20 lastIndexOf ( searchElement [ , fromIndex ] )
        r.Method(proto, "lastIndexOf", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "lastIndexOf");
            long len = o.Length;
            if (len == 0)
            {
                return Num(-1);
            }

            double n = a.Length > 1 ? Ops.ToIntegerOrInfinity(a[1]) : len - 1;
            if (n == double.NegativeInfinity)
            {
                return Num(-1);
            }

            double k = n >= 0 ? Math.Min(n, len - 1) : len + n;
            Work(len);
            for (; k >= 0; k--)
            {
                if (k < o.Length && Ops.StrictEquals(A(a, 0), o.GetElement((int)k)))
                {
                    return Num(k);
                }
            }

            return Num(-1);
        });
    }

    // ---- mutators -----------------------------------------------------------------------------------------

    private static void SetupMutators(Realm r, JsObject proto)
    {
        // 23.2.3.9 fill ( value [ , start [ , end ] ] )
        r.Method(proto, "fill", 1, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "fill");
            long len = o.Length;
            JsValue value = ElementIo.ToElementValue(o.Kind, A(a, 0));
            long start = ArrayBuffers.Clamp(A(a, 1), len, 0);
            long end = ArrayBuffers.Clamp(A(a, 2), len, len);
            if (o.IsOutOfBounds)
            {
                throw DetachedOp("%TypedArray%.prototype.fill");
            }

            end = Math.Min(end, o.Length);
            Work(end - start);
            for (long k = start; k < end; k++)
            {
                o.SetElementRaw((int)k, value);
            }

            return t;
        });

        // 23.2.3.6 copyWithin ( target, start [ , end ] )
        r.Method(proto, "copyWithin", 2, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "copyWithin");
            long len = o.Length;
            long to = ArrayBuffers.Clamp(A(a, 0), len, 0);
            long from = ArrayBuffers.Clamp(A(a, 1), len, 0);
            long fin = ArrayBuffers.Clamp(A(a, 2), len, len);
            long count = Math.Min(fin - from, len - to);
            if (count > 0)
            {
                if (o.IsOutOfBounds)
                {
                    throw DetachedOp("%TypedArray%.prototype.copyWithin");
                }

                len = o.Length;
                count = Math.Min(count, Math.Min(len - from, len - to));
                if (count > 0)
                {
                    int size = o.ElementSize;
                    Work(count * size);
                    Array.Copy(o.Buffer.Data, o.ByteOffset + (from * size), o.Buffer.Data, o.ByteOffset + (to * size), count * size);
                }
            }

            return t;
        });

        // 23.2.3.25 reverse ( )
        r.Method(proto, "reverse", 0, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "reverse");
            int len = o.Length;
            Work(len);
            for (int lower = 0, upper = len - 1; lower < upper; lower++, upper--)
            {
                JsValue lv = o.GetElement(lower);
                o.SetElementRaw(lower, o.GetElement(upper));
                o.SetElementRaw(upper, lv);
            }

            return t;
        });

        // 23.2.3.35 toReversed ( )
        r.Method(proto, "toReversed", 0, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "toReversed");
            int len = o.Length;
            JsTypedArray res = CreateSameType(o, len);
            Work(len);
            for (int k = 0; k < len; k++)
            {
                res.SetElementRaw(k, o.GetElement(len - 1 - k));
            }

            return JsValue.FromObject(res);
        });

        // 23.2.3.36 with ( index, value )
        r.Method(proto, "with", 2, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "with");
            long len = o.Length;
            double rel = Ops.ToIntegerOrInfinity(A(a, 0));
            double actual = rel >= 0 ? rel : len + rel;
            JsValue numeric = ElementIo.ToElementValue(o.Kind, A(a, 1));
            if (!o.IsValidIndex(actual, out _))
            {
                throw Errors.Range("Invalid typed array index");
            }

            JsTypedArray res = CreateSameType(o, len);
            Work(len);
            for (long k = 0; k < len; k++)
            {
                res.SetByNumber(k, k == actual ? numeric : o.GetByNumber(k));
            }

            return JsValue.FromObject(res);
        });

        // 23.2.3.26 set ( source [ , offset ] )
        r.Method(proto, "set", 1, (t, a, nt) =>
        {
            JsTypedArray target = Require(t);
            double targetOffset = Ops.ToIntegerOrInfinity(A(a, 1));
            if (targetOffset < 0)
            {
                throw Errors.Range("offset is out of bounds");
            }

            if (A(a, 0).Ref is JsTypedArray source)
            {
                SetFromTypedArray(target, targetOffset, source);
            }
            else
            {
                if (target.IsOutOfBounds)
                {
                    throw DetachedOp("%TypedArray%.prototype.set");
                }

                long targetLength = target.Length;
                JsObject src = Ops.ToObject(A(a, 0));
                long srcLength = Ops.LengthOfArrayLike(src);
                if (targetOffset == double.PositiveInfinity || srcLength + targetOffset > targetLength)
                {
                    throw Errors.Range("offset is out of bounds");
                }

                for (long k = 0; k < srcLength; k++)
                {
                    Agent.Current.Tick();
                    target.SetByNumber(targetOffset + k, src.Get(new PropertyKey(Ops.NumberToStr(k))));
                }
            }

            return default;
        });

        // 23.2.3.27 slice ( start, end )
        r.Method(proto, "slice", 2, (t, a, nt) =>
        {
            JsTypedArray o = Validate(t, "slice");
            long srcLen = o.Length;
            long start = ArrayBuffers.Clamp(A(a, 0), srcLen, 0);
            long end = ArrayBuffers.Clamp(A(a, 1), srcLen, srcLen);
            long count = Math.Max(end - start, 0);
            JsTypedArray res = SpeciesCreate(o, [Num(count)]);
            if (count > 0)
            {
                if (o.IsOutOfBounds)
                {
                    throw DetachedOp("%TypedArray%.prototype.slice");
                }

                end = Math.Min(end, o.Length);
                count = Math.Max(end - start, 0);
                Work(count);
                if (o.Kind == res.Kind)
                {
                    int size = o.ElementSize;
                    long srcByte = o.ByteOffset + (start * size);
                    long dstByte = res.ByteOffset;
                    long bytes = Math.Min(count * size, res.Buffer.ByteLength - dstByte);
                    byte[] sd = o.Buffer.Data;
                    byte[] dd = res.Buffer.Data;
                    if (bytes > 0 && ReferenceEquals(sd, dd))
                    {
                        // Byte-by-byte ascending copy, as the spec words it: overlapping ranges repeat bytes.
                        for (long i = 0; i < bytes; i++)
                        {
                            dd[dstByte + i] = sd[srcByte + i];
                        }
                    }
                    else if (bytes > 0)
                    {
                        Array.Copy(sd, srcByte, dd, dstByte, bytes);
                    }
                }
                else
                {
                    long n = 0;
                    for (long k = start; k < end; k++, n++)
                    {
                        res.SetByNumber(n, o.GetByNumber(k));
                    }
                }
            }

            return JsValue.FromObject(res);
        });

        // 23.2.3.30 subarray ( start, end )
        r.Method(proto, "subarray", 2, (t, a, nt) =>
        {
            JsTypedArray o = Require(t);
            long srcLength = o.Length;
            long start = ArrayBuffers.Clamp(A(a, 0), srcLength, 0);
            long beginByteOffset = o.ByteOffset + (start * o.ElementSize);
            if (o.LengthTracking && A(a, 1).IsUndefined)
            {
                return JsValue.FromObject(SpeciesCreate(o, [JsValue.FromObject(o.Buffer), Num(beginByteOffset)]));
            }

            long end = ArrayBuffers.Clamp(A(a, 1), srcLength, srcLength);
            long newLength = Math.Max(end - start, 0);
            return JsValue.FromObject(SpeciesCreate(o, [JsValue.FromObject(o.Buffer), Num(beginByteOffset), Num(newLength)]));
        });
    }

    /// <summary>SetTypedArrayFromTypedArray (23.2.3.26.2).</summary>
    private static void SetFromTypedArray(JsTypedArray target, double targetOffset, JsTypedArray source)
    {
        if (target.IsOutOfBounds)
        {
            throw DetachedOp("%TypedArray%.prototype.set");
        }

        long targetLength = target.Length;
        if (source.IsOutOfBounds)
        {
            throw DetachedOp("%TypedArray%.prototype.set");
        }

        long srcLength = source.Length;
        if (targetOffset == double.PositiveInfinity || srcLength + targetOffset > targetLength)
        {
            throw Errors.Range("offset is out of bounds");
        }

        if (target.IsBigInt != source.IsBigInt)
        {
            throw MixError();
        }

        Work(srcLength);
        int toff = (int)targetOffset;
        if (source.Kind == target.Kind)
        {
            Array.Copy(source.Buffer.Data, source.ByteOffset, target.Buffer.Data, target.ByteOffset + (toff * target.ElementSize), srcLength * target.ElementSize);
            return;
        }

        byte[] srcData = source.Buffer.Data;
        int srcBase = source.ByteOffset;
        if (ReferenceEquals(srcData, target.Buffer.Data))
        {
            // Overlapping views of one buffer: read from a snapshot of the source bytes.
            srcData = new byte[srcLength * source.ElementSize];
            Agent.Current.Alloc(srcData.Length);
            Array.Copy(source.Buffer.Data, source.ByteOffset, srcData, 0, srcData.Length);
            srcBase = 0;
        }

        int ss = source.ElementSize;
        for (int i = 0; i < srcLength; i++)
        {
            JsValue v = ElementIo.Read(source.Kind, srcData.AsSpan(srcBase + (i * ss)), true);
            target.SetElementRaw(toff + i, v);
        }
    }

    // ---- ordering -----------------------------------------------------------------------------------------

    private static int CompareNumbers(double x, double y)
    {
        if (x < y)
        {
            return -1;
        }

        if (x > y)
        {
            return 1;
        }

        if (x == y)
        {
            if (x == 0)
            {
                bool nx = double.IsNegative(x);
                bool ny = double.IsNegative(y);
                return nx == ny ? 0 : nx ? -1 : 1;
            }

            return 0;
        }

        bool xn = double.IsNaN(x);
        bool yn = double.IsNaN(y);
        return xn && yn ? 0 : xn ? 1 : -1;
    }

    /// <summary>CompareTypedArrayElements (23.2.4.7) with an optional comparator.</summary>
    private static double Compare(JsValue x, JsValue y, JsValue comparator)
    {
        if (!comparator.IsUndefined)
        {
            double v = Ops.ToNumber(Ops.Call(comparator, default, [x, y]));
            return double.IsNaN(v) ? 0 : v;
        }

        if (x.Ref is BigInteger bx)
        {
            return bx.CompareTo((BigInteger)y.Ref!);
        }

        return CompareNumbers(x.Num, y.Num);
    }

    /// <summary>SortIndexedProperties over the typed array's current elements; stable.</summary>
    private static JsValue[] SortedElements(JsTypedArray o, long len, JsValue comparator)
    {
        var items = new JsValue[len];
        for (int i = 0; i < len; i++)
        {
            items[i] = o.GetByNumber(i);
        }

        Work(len);
        if (comparator.IsUndefined)
        {
            if (o.IsBigInt)
            {
                var keys = new BigInteger[len];
                for (int i = 0; i < len; i++)
                {
                    keys[i] = (BigInteger)items[i].Ref!;
                }

                Array.Sort(keys);
                for (int i = 0; i < len; i++)
                {
                    items[i] = JsValue.FromBigInt(keys[i]);
                }
            }
            else
            {
                var keys = new double[len];
                for (int i = 0; i < len; i++)
                {
                    keys[i] = items[i].Num;
                }

                Array.Sort(keys, CompareNumbers);
                for (int i = 0; i < len; i++)
                {
                    items[i] = Num(keys[i]);
                }
            }

            return items;
        }

        // Bottom-up merge sort: stable, no recursion, and the comparator may throw at any point.
        var tmp = new JsValue[len];
        for (long width = 1; width < len; width *= 2)
        {
            for (long lo = 0; lo < len; lo += 2 * width)
            {
                long mid = Math.Min(lo + width, len);
                long hi = Math.Min(lo + (2 * width), len);
                long i = lo, j = mid, k = lo;
                while (i < mid && j < hi)
                {
                    Agent.Current.Tick();
                    tmp[k++] = Compare(items[j], items[i], comparator) < 0 ? items[j++] : items[i++];
                }

                while (i < mid)
                {
                    tmp[k++] = items[i++];
                }

                while (j < hi)
                {
                    tmp[k++] = items[j++];
                }
            }

            (items, tmp) = (tmp, items);
        }

        return items;
    }

    private static void SetupOrdering(Realm r, JsObject proto)
    {
        // 23.2.3.29 sort ( comparator )
        r.Method(proto, "sort", 1, (t, a, nt) =>
        {
            JsValue cmp = A(a, 0);
            if (!cmp.IsUndefined && !Ops.IsCallable(cmp))
            {
                throw Errors.Type("The comparison function must be either a function or undefined: " + Ops.DisplayValue(cmp));
            }

            JsTypedArray o = Validate(t, "sort");
            long len = o.Length;
            JsValue[] sorted = SortedElements(o, len, cmp);
            int cur = o.Length;
            for (int j = 0; j < len && j < cur; j++)
            {
                o.SetElementRaw(j, sorted[j]);
            }

            return t;
        });

        // 23.2.3.33 toSorted ( comparator )
        r.Method(proto, "toSorted", 1, (t, a, nt) =>
        {
            JsValue cmp = A(a, 0);
            if (!cmp.IsUndefined && !Ops.IsCallable(cmp))
            {
                throw Errors.Type("The comparison function must be either a function or undefined: " + Ops.DisplayValue(cmp));
            }

            JsTypedArray o = Validate(t, "toSorted");
            long len = o.Length;
            JsTypedArray res = CreateSameType(o, len);
            JsValue[] sorted = SortedElements(o, len, cmp);
            for (int j = 0; j < len; j++)
            {
                res.SetByNumber(j, sorted[j]);
            }

            return JsValue.FromObject(res);
        });
    }
}
