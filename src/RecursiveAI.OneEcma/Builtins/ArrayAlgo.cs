using System.Globalization;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma;

/// <summary>
/// The generic array-like algorithms shared by Array.prototype and (later) the typed array methods. Every element access
/// goes through the object's internal methods, with a fast path for dense <see cref="JsArray"/> storage that is only taken
/// where the result is indistinguishable from the spec algorithm. Each element visit charges one unit of fuel, so loops over
/// huge or hostile lengths end in a limit report instead of running unbounded.
/// </summary>
internal static class ArrayAlgo
{
    /// <summary>2^53 - 1, the largest length an array-like can have (7.1.20 ToLength).</summary>
    internal const long MaxSafeInteger = 9007199254740991L;

    /// <summary>2^32 - 1, the largest Array length (10.4.2.2 ArrayCreate).</summary>
    internal const long MaxArrayLength = 4294967295L;

    /// <summary>The key of integer index <paramref name="i"/>; charges one step.</summary>
    internal static PropertyKey Key(long i)
    {
        Agent.Current.Tick();
        return new PropertyKey(i.ToString(NumberFormatInfo.InvariantInfo));
    }

    /// <summary>The TypeError for calling a non-callable argument, in V8's wording (the type, then the value for primitives).</summary>
    internal static JsException NotFunction(JsValue v)
    {
        string text;
        if (v.IsUndefined)
        {
            text = "undefined";
        }
        else if (v.IsNull)
        {
            text = "object null";
        }
        else if (v.IsObject)
        {
            text = "object";
        }
        else if (v.IsNumber)
        {
            text = "number " + Ops.NumberToStr(v.Num);
        }
        else if (v.IsString)
        {
            text = "string \"" + Ops.ToStr(v) + "\"";
        }
        else if (v.IsBigInt)
        {
            text = "bigint " + Ops.ToStr(v);
        }
        else if (v.Ref is JsSymbol)
        {
            text = "symbol";
        }
        else
        {
            text = "boolean " + (v.IsTrue ? "true" : "false");
        }

        return Errors.Type(text + " is not a function");
    }

    /// <summary>ToObject(this value), with V8's message for the methods that name themselves when the receiver is null or undefined.</summary>
    internal static JsObject This(JsValue t, string? namedMethod)
    {
        if (namedMethod is not null && t.IsNullish)
        {
            throw Errors.Type("Array.prototype." + namedMethod + " called on null or undefined");
        }

        return Ops.ToObject(t);
    }

    // ---- element access --------------------------------------------------------------------------------------------------

    /// <summary>Get(O, ToString(i)).</summary>
    internal static JsValue Get(JsObject o, long i)
    {
        if (o is JsArray ja && i < ja.Count && ja.TryGetDense((uint)i, out JsValue v))
        {
            Agent.Current.Tick();
            return v;
        }

        return o.Get(Key(i));
    }

    /// <summary>HasProperty(O, ToString(i)).</summary>
    internal static bool Has(JsObject o, long i)
    {
        if (o is JsArray ja && i < ja.Count && ja.TryGetDense((uint)i, out _))
        {
            Agent.Current.Tick();
            return true;
        }

        return o.HasProperty(Key(i));
    }

    /// <summary>Set(O, P, V, true): throws V8's TypeError when the assignment fails.</summary>
    internal static void Set(JsObject o, PropertyKey k, JsValue v)
    {
        if (!o.Set(k, v, JsValue.FromObject(o)))
        {
            // V8 names the frozen length when an element cannot be added because of it.
            if (o is JsArray { LengthWritable: false } ja && k.TryGetIndex(out uint idx) && idx >= ja.Length)
            {
                throw Errors.Type("Cannot assign to read only property 'length' of object '[object Array]'");
            }

            throw Errors.Type(Ops.SetFailureMessage(o, k));
        }
    }

    /// <summary>Set(O, ToString(i), V, true).</summary>
    internal static void Set(JsObject o, long i, JsValue v)
    {
        // An existing element of a dense array is a writable data property, so a store is exactly what [[Set]] does.
        if (o is JsArray ja && i < ja.Count && ja.TryGetDense((uint)i, out _))
        {
            Agent.Current.Tick();
            ja.Dense![i] = v;
            return;
        }

        Set(o, Key(i), v);
    }

    /// <summary>Set(O, "length", len, true).</summary>
    internal static void SetLength(JsObject o, long len) => Set(o, "length", Ops.Num(len));

    /// <summary>DeletePropertyOrThrow(O, P).</summary>
    internal static void Delete(JsObject o, PropertyKey k)
    {
        if (!o.Delete(k))
        {
            throw Errors.Type("Cannot delete property '" + Ops.KeyText(k) + "' of " + Ops.ObjectLabel(o));
        }
    }

    internal static void Delete(JsObject o, long i) => Delete(o, Key(i));

    /// <summary>CreateDataPropertyOrThrow(O, ToString(i), V).</summary>
    internal static void Create(JsObject o, long i, JsValue v)
    {
        if (o is JsArray ja && TryCreateDense(ja, i, v))
        {
            Agent.Current.Tick();
            return;
        }

        PropertyKey k = Key(i);
        if (!o.CreateDataProperty(k, v))
        {
            throw Errors.Type("Cannot add property " + Ops.KeyText(k) + ", object is not extensible");
        }
    }

    private static bool TryCreateDense(JsArray ja, long i, JsValue v)
    {
        if (ja.Dense is null || !ja.Extensible || !ja.LengthWritable)
        {
            return false;
        }

        if (i < ja.Count)
        {
            ja.Dense[i] = v;
            return true;
        }

        if (i == ja.Count && i < int.MaxValue / 2)
        {
            if (ja.Count == ja.Dense.Length)
            {
                Array.Resize(ref ja.Dense, Math.Max(4, ja.Count * 2));
            }

            ja.Dense[ja.Count++] = v;
            if (ja.Count > ja.Length)
            {
                ja.Length = (uint)ja.Count;
            }

            return true;
        }

        return false;
    }

    // ---- creation --------------------------------------------------------------------------------------------------------

    /// <summary>ArrayCreate (10.4.2.2) with the default prototype of the current realm.</summary>
    internal static JsArray ArrayCreate(long length)
    {
        if (length > MaxArrayLength)
        {
            throw Errors.Range("Invalid array length");
        }

        var a = new JsArray(Agent.Current.Realm.ArrayPrototype) { Length = (uint)length };
        return a;
    }

    /// <summary>ArraySpeciesCreate (10.4.2.3).</summary>
    internal static JsObject SpeciesCreate(JsObject original, long length)
    {
        if (!Ops.IsArray(JsValue.FromObject(original)))
        {
            return ArrayCreate(length);
        }

        JsValue c = original.Get("constructor");
        if (Ops.IsConstructor(c))
        {
            Realm thisRealm = Agent.Current.Realm;
            Realm realmC = Ops.GetFunctionRealm((JsObject)c.Ref!);
            if (!ReferenceEquals(thisRealm, realmC) && ReferenceEquals(c.Ref, realmC.Intrinsics["%Array%"]))
            {
                c = default;
            }
        }

        if (c.Ref is JsObject co)
        {
            c = co.Get(new PropertyKey(WellKnown.Species));
            if (c.IsNull)
            {
                c = default;
            }
        }

        if (c.IsUndefined)
        {
            return ArrayCreate(length);
        }

        if (!Ops.IsConstructor(c))
        {
            throw Errors.Type("object.constructor[Symbol.species] is not a constructor");
        }

        return Ops.Construct((JsObject)c.Ref!, [Ops.Num(length)]);
    }

    /// <summary>Accounts for a list of <paramref name="count"/> values before it is allocated.</summary>
    internal static void ReserveList(long count)
    {
        // The budget check comes first so that a huge request is reported as a memory limit when the host sets one.
        Agent.Current.Alloc(count * 16);
        if (count > int.MaxValue / 2)
        {
            throw Errors.Range("Invalid array length");
        }

    }

    // ---- relative indices ------------------------------------------------------------------------------------------------

    /// <summary>Resolves a relative index argument (negative counts from the end) and clamps it to [0, len].</summary>
    internal static long Relative(JsValue v, long len, long whenUndefined)
    {
        if (v.IsUndefined)
        {
            return whenUndefined;
        }

        double n = Ops.ToIntegerOrInfinity(v);
        return n < 0 ? (long)Math.Max(len + n, 0) : (long)Math.Min(n, len);
    }

    // ---- sorting ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A stable bottom-up merge sort. It never recurses and tolerates a comparator that is inconsistent or throws: every
    /// comparison goes through <paramref name="cmp"/> (negative means the first argument sorts before the second), the
    /// result is always a permutation of the input, and a throw leaves <paramref name="items"/> unspecified but valid.
    /// </summary>
    internal static void MergeSort<T>(T[] items, int n, Func<T, T, int> cmp)
    {
        if (n < 2)
        {
            return;
        }

        T[] src = items;
        T[] dst = new T[n];
        for (int width = 1; width < n; width *= 2)
        {
            for (int lo = 0; lo < n; lo += 2 * width)
            {
                int mid = Math.Min(lo + width, n);
                int hi = Math.Min(lo + (2 * width), n);
                int i = lo;
                int j = mid;
                int k = lo;
                while (i < mid && j < hi)
                {
                    // Take from the right run only when it sorts strictly before the left element: that keeps equal elements in order.
                    // The right element is passed first, so a degenerate comparator such as () => -1 reverses the input as it does in Node.
                    dst[k++] = cmp(src[j], src[i]) < 0 ? src[j++] : src[i++];
                }

                while (i < mid)
                {
                    dst[k++] = src[i++];
                }

                while (j < hi)
                {
                    dst[k++] = src[j++];
                }
            }

            (src, dst) = (dst, src);
        }

        if (!ReferenceEquals(src, items))
        {
            Array.Copy(src, items, n);
        }
    }

    private readonly struct Keyed
    {
        internal readonly JsValue Value;
        internal readonly string Text;

        internal Keyed(JsValue v, string s)
        {
            Value = v;
            Text = s;
        }
    }

    /// <summary>
    /// Sorts <paramref name="values"/> in place by SortCompare (23.1.3.30.2): undefined values go last without consulting the
    /// comparator, others are ordered by <paramref name="comparefn"/> or, when it is undefined, by the UTF-16 code units of their
    /// string forms.
    /// </summary>
    internal static void SortValues(List<JsValue> values, JsValue comparefn)
    {
        int count = values.Count;
        var defined = new JsValue[count];
        int n = 0;
        foreach (JsValue v in values)
        {
            if (!v.IsUndefined)
            {
                defined[n++] = v;
            }
        }

        int undefs = count - n;
        if (!comparefn.IsUndefined)
        {
            var fn = (JsObject)comparefn.Ref!;
            MergeSort(defined, n, (x, y) =>
            {
                Agent.Current.Tick();
                double d = Ops.ToNumber(fn.Call(default, [x, y]));
                return d > 0 ? 1 : d < 0 ? -1 : 0;
            });
        }
        else if (n >= 2)
        {
            bool pure = true;
            for (int i = 0; i < n && pure; i++)
            {
                pure = defined[i].Ref is not JsObject;
            }

            if (pure)
            {
                // Primitives convert without side effects, so the string forms are computed once. A symbol still throws here, as it would in the first comparison.
                var keyed = new Keyed[n];
                for (int i = 0; i < n; i++)
                {
                    keyed[i] = new Keyed(defined[i], Ops.ToStr(defined[i]));
                }

                MergeSort(keyed, n, (x, y) =>
                {
                    Agent.Current.Tick();
                    return string.CompareOrdinal(x.Text, y.Text);
                });
                for (int i = 0; i < n; i++)
                {
                    defined[i] = keyed[i].Value;
                }
            }
            else
            {
                MergeSort(defined, n, (x, y) =>
                {
                    Agent.Current.Tick();
                    return string.CompareOrdinal(Ops.ToStr(x), Ops.ToStr(y));
                });
            }
        }

        values.Clear();
        for (int i = 0; i < n; i++)
        {
            values.Add(defined[i]);
        }

        for (int i = 0; i < undefs; i++)
        {
            values.Add(default);
        }
    }

    /// <summary>The comparator check shared by sort and toSorted.</summary>
    internal static void RequireComparator(JsValue comparefn)
    {
        if (!comparefn.IsUndefined && !Ops.IsCallable(comparefn))
        {
            throw Errors.Type("The comparison function must be either a function or undefined: " + Ops.DisplayValue(comparefn));
        }
    }

    // ---- flattening ------------------------------------------------------------------------------------------------------

    private struct FlatFrame
    {
        internal JsObject Source;
        internal long Length;
        internal long Index;
        internal double Depth;
    }

    /// <summary>
    /// FlattenIntoArray (23.1.3.13.1). The recursion of the spec algorithm is an explicit stack of frames, so nesting as deep as
    /// the call-depth limit (or a cyclic structure with an infinite depth) ends in a RangeError instead of a host stack overflow.
    /// </summary>
    internal static long FlattenIntoArray(JsObject target, JsObject source, long sourceLen, long start, double depth, JsValue mapper, JsValue thisArg)
    {
        Agent ag = Agent.Current;
        var stack = new List<FlatFrame>();
        JsObject cur = source;
        long curLen = sourceLen;
        long index = 0;
        double d = depth;
        long targetIndex = start;
        while (true)
        {
            if (index >= curLen)
            {
                if (stack.Count == 0)
                {
                    return targetIndex;
                }

                FlatFrame f = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                cur = f.Source;
                curLen = f.Length;
                index = f.Index;
                d = f.Depth;
                continue;
            }

            long i = index++;
            if (!Has(cur, i))
            {
                continue;
            }

            JsValue element = Get(cur, i);
            if (stack.Count == 0 && !mapper.IsUndefined)
            {
                element = Ops.Call(mapper, thisArg, [element, Ops.Num(i), JsValue.FromObject(cur)]);
            }

            if (d > 0 && Ops.IsArray(element))
            {
                if (stack.Count >= ag.Limits.MaxCallDepth)
                {
                    throw Errors.Range("Maximum call stack size exceeded");
                }

                ag.Alloc(48);
                var inner = (JsObject)element.Ref!;
                long innerLen = Ops.LengthOfArrayLike(inner);
                stack.Add(new FlatFrame { Source = cur, Length = curLen, Index = index, Depth = d });
                cur = inner;
                curLen = innerLen;
                index = 0;
                d = double.IsPositiveInfinity(d) ? d : d - 1;
            }
            else
            {
                if (targetIndex >= MaxSafeInteger)
                {
                    throw Errors.Type("Invalid array length");
                }

                Create(target, targetIndex, element);
                targetIndex++;
            }
        }
    }

    [ThreadStatic]
    private static List<PropertyKey>? t_keys;

    /// <summary>
    /// True when no object on the prototype chain of <paramref name="o"/> can hold or intercept an integer key, so adding an element
    /// to <paramref name="o"/> with [[Set]] is the same as defining it directly. Prototypes that are not plain objects or arrays
    /// (Proxy, exotic objects) answer false.
    /// </summary>
    internal static bool ChainIndexFree(JsObject o)
    {
        for (JsObject? p = o.Proto; p is not null; p = p.Proto)
        {
            if (p is JsArray pa)
            {
                if (pa.Dense is null || pa.Count > 0)
                {
                    return false;
                }
            }
            else if (p.GetType() != typeof(JsObject) && p is not ObjectPrototypeObject)
            {
                return false;
            }

            Shape sh = p.Shape;
            if (sh.IsDictionary)
            {
                for (int slot = 0; slot < sh.Count; slot++)
                {
                    if (!sh.IsDeleted(slot) && sh.KeyAt(slot).Ref is string s && s.Length > 0 && s[0] is >= '0' and <= '9')
                    {
                        return false;
                    }
                }
            }
            else
            {
                List<PropertyKey> keys = t_keys ??= [];
                keys.Clear();
                sh.CollectKeys(keys);
                foreach (PropertyKey k in keys)
                {
                    if (k.Ref is string s && s.Length > 0 && s[0] is >= '0' and <= '9')
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    // ---- sparse objects --------------------------------------------------------------------------------------------------

    /// <summary>Parses a canonical integer key ("0", "17", never "01") up to 2^53 - 1.</summary>
    private static bool TryParseIndex(string s, out long index)
    {
        index = 0;
        if (s.Length == 0 || s.Length > 16 || (s[0] == '0' && s.Length > 1))
        {
            return false;
        }

        long v = 0;
        foreach (char c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            v = (v * 10) + (c - '0');
        }

        index = v;
        return v <= MaxSafeInteger;
    }

    /// <summary>
    /// For a huge length, lists the integer keys in [from, to) that exist, in ascending order, so a hole-skipping loop visits only
    /// them. Returns false (callers then run the plain loop) unless the receiver and its whole prototype chain are ordinary objects
    /// or arrays and the prototypes hold no integer keys, which makes "absent on the receiver" mean "absent".
    /// </summary>
    internal static bool TryExistingIndices(JsObject o, long from, long to, out List<long> indices)
    {
        indices = [];
        if (!IsOrdinaryLike(o))
        {
            return false;
        }

        var keys = new List<PropertyKey>();
        for (JsObject? p = o.Proto; p is not null; p = p.Proto)
        {
            if (!IsOrdinaryLike(p))
            {
                return false;
            }

            keys.Clear();
            p.OwnKeys(keys);
            foreach (PropertyKey k in keys)
            {
                if (k.Ref is string s && TryParseIndex(s, out _))
                {
                    return false;
                }
            }
        }

        keys.Clear();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            Agent.Current.Tick();
            if (k.Ref is string s && TryParseIndex(s, out long idx) && idx >= from && idx < to)
            {
                indices.Add(idx);
            }
        }

        indices.Sort();
        return true;
    }

    private static bool IsOrdinaryLike(JsObject o) =>
        o is JsArray || o is ObjectPrototypeObject || o.GetType() == typeof(JsObject);

    /// <summary>Lengths above this use the sparse shortcuts where they apply.</summary>
    internal const long SparseThreshold = 1 << 16;

    // ---- strings ---------------------------------------------------------------------------------------------------------

    /// <summary>Throws the RangeError for a string longer than the configured limit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckStringLength(int length)
    {
        if (length > Agent.Current.Limits.MaxStringLength)
        {
            throw Errors.Range("Invalid string length");
        }
    }
}
