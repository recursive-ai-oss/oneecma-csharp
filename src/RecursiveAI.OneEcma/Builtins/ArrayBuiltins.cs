using System.Text;
using Alg = RecursiveAI.OneEcma.ArrayAlgo;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    // ---- Array (ECMA-262 23.1) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The default order of <c>values</c>, <c>keys</c> and <c>entries</c> kinds for <see cref="ArrayIteratorObject"/>.
    /// </summary>
    private const int IterKeys = 1;
    private const int IterValues = 2;
    private const int IterEntries = 3;

    private static void SetupArray(Realm r)
    {
        JsObject proto = r.ArrayPrototype;
        NativeFunction ctor = r.Ctor("Array", 1, ArrayProto.Construct, proto);
        r.Method(ctor, "isArray", 1, (t, a, nt) => Ops.Bool(Ops.IsArray(A(a, 0))));
        r.Method(ctor, "of", 0, ArrayProto.Of);
        r.Method(ctor, "from", 1, ArrayProto.From);
        r.Method(ctor, "fromAsync", 1, ArrayFromAsync.FromAsync);
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);

        r.Method(proto, "at", 1, ArrayProto.At);
        r.Method(proto, "concat", 1, ArrayProto.Concat);
        r.Method(proto, "copyWithin", 2, ArrayProto.CopyWithin);
        r.Method(proto, "fill", 1, ArrayProto.Fill);
        r.Method(proto, "find", 1, (t, a, nt) => ArrayProto.Find(t, a, "find", false, false));
        r.Method(proto, "findIndex", 1, (t, a, nt) => ArrayProto.Find(t, a, "findIndex", false, true));
        r.Method(proto, "findLast", 1, (t, a, nt) => ArrayProto.Find(t, a, "findLast", true, false));
        r.Method(proto, "findLastIndex", 1, (t, a, nt) => ArrayProto.Find(t, a, "findLastIndex", true, true));
        r.Method(proto, "lastIndexOf", 1, ArrayProto.LastIndexOf);
        r.Method(proto, "pop", 0, ArrayProto.Pop);
        r.Method(proto, "push", 1, ArrayProto.Push);
        r.Method(proto, "reverse", 0, ArrayProto.Reverse);
        r.Method(proto, "shift", 0, ArrayProto.Shift);
        r.Method(proto, "unshift", 1, ArrayProto.Unshift);
        r.Method(proto, "slice", 2, ArrayProto.Slice);
        r.Method(proto, "sort", 1, ArrayProto.Sort);
        r.Method(proto, "splice", 2, ArrayProto.Splice);
        r.Method(proto, "includes", 1, ArrayProto.Includes);
        r.Method(proto, "indexOf", 1, ArrayProto.IndexOf);
        r.Method(proto, "join", 1, ArrayProto.Join);
        r.Method(proto, "keys", 0, (t, a, nt) => MakeArrayIterator(r, t, IterKeys));
        r.Method(proto, "entries", 0, (t, a, nt) => MakeArrayIterator(r, t, IterEntries));
        NativeFunction values = r.Method(proto, "values", 0, (t, a, nt) => MakeArrayIterator(r, t, IterValues));
        proto.DefineDirect(new PropertyKey(WellKnown.Iterator), JsValue.FromObject(values), PropFlags.Hidden);
        r.ArrayProtoValues = JsValue.FromObject(values);
        r.Method(proto, "forEach", 1, (t, a, nt) => ArrayProto.Each(t, a, ArrayProto.EachKind.ForEach));
        r.Method(proto, "filter", 1, (t, a, nt) => ArrayProto.Each(t, a, ArrayProto.EachKind.Filter));
        r.Method(proto, "flat", 0, ArrayProto.Flat);
        r.Method(proto, "flatMap", 1, ArrayProto.FlatMap);
        r.Method(proto, "map", 1, (t, a, nt) => ArrayProto.Each(t, a, ArrayProto.EachKind.Map));
        r.Method(proto, "every", 1, (t, a, nt) => ArrayProto.Each(t, a, ArrayProto.EachKind.Every));
        r.Method(proto, "some", 1, (t, a, nt) => ArrayProto.Each(t, a, ArrayProto.EachKind.Some));
        r.Method(proto, "reduce", 1, (t, a, nt) => ArrayProto.Reduce(t, a, false));
        r.Method(proto, "reduceRight", 1, (t, a, nt) => ArrayProto.Reduce(t, a, true));
        r.Method(proto, "toReversed", 0, ArrayProto.ToReversed);
        r.Method(proto, "toSorted", 1, ArrayProto.ToSorted);
        r.Method(proto, "toSpliced", 2, ArrayProto.ToSpliced);
        r.Method(proto, "with", 2, ArrayProto.With);
        r.Method(proto, "toLocaleString", 0, ArrayProto.ToLocaleString);
        r.Method(proto, "toString", 0, ArrayProto.ToString);

        // 23.1.3.41 Array.prototype [ %Symbol.unscopables% ]
        var unscopables = new JsObject((JsObject?)null);
        foreach (string name in new[] { "at", "copyWithin", "entries", "fill", "find", "findIndex", "findLast", "findLastIndex", "flat", "flatMap", "includes", "keys", "toReversed", "toSorted", "toSpliced", "values" })
        {
            unscopables.CreateDataProperty(name, JsValue.True);
        }

        proto.DefineDirect(new PropertyKey(WellKnown.Unscopables), JsValue.FromObject(unscopables), PropFlags.Configurable);
    }

    /// <summary>The property key text of an array index; charges one step so loops over huge array-likes end in a limit.</summary>
    private static string IdxKey(long i) => Alg.Key(i).String!;

    /// <summary>Resolves a relative index argument for the string methods that share the array clamping rule.</summary>
    private static long RelIndex(JsValue v, long len, long dflt) => Alg.Relative(v, len, dflt);
}

/// <summary>The Array constructor and the methods of Array.prototype.</summary>
internal static class ArrayProto
{
    // ---- constructor -----------------------------------------------------------------------------------------------------

    /// <summary>23.1.1.1 Array ( ...values )</summary>
    internal static JsValue Construct(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        Realm r = Agent.Current.Realm;
        JsObject p = nt is null ? r.ArrayPrototype : Ops.GetPrototypeFromConstructor(nt, x => x.ArrayPrototype);
        var arr = new JsArray(p);
        if (a.Length == 1)
        {
            JsValue len = a[0];
            if (len.IsNumber)
            {
                double n = len.Num;
                if (n != Ops.ToUint32(n))
                {
                    throw Errors.Range("Invalid array length");
                }

                arr.Length = (uint)n;
            }
            else
            {
                arr.Push(len);
            }
        }
        else
        {
            foreach (JsValue v in a)
            {
                arr.Push(v);
            }
        }

        return JsValue.FromObject(arr);
    }

    /// <summary>23.1.2.4 Array.of ( ...items )</summary>
    internal static JsValue Of(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        int len = a.Length;
        JsObject arr = Ops.IsConstructor(t) ? Ops.Construct((JsObject)t.Ref!, [Ops.Num(len)]) : Alg.ArrayCreate(len);
        for (int k = 0; k < len; k++)
        {
            Alg.Create(arr, k, a[k]);
        }

        Alg.SetLength(arr, len);
        return JsValue.FromObject(arr);
    }

    /// <summary>23.1.2.1 Array.from ( items [ , mapper [ , thisArg ] ] )</summary>
    internal static JsValue From(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsValue items = Ops.Arg(a, 0);
        JsValue mapper = Ops.Arg(a, 1);
        JsValue thisArg = Ops.Arg(a, 2);
        bool mapping = !mapper.IsUndefined;
        if (mapping && !Ops.IsCallable(mapper))
        {
            throw Alg.NotFunction(mapper);
        }

        if (items.IsNullish)
        {
            throw Errors.Type((items.IsNull ? "object null" : "undefined") + " is not iterable (cannot read property Symbol(Symbol.iterator))");
        }

        JsValue usingIterator = Ops.GetMethod(items, new PropertyKey(WellKnown.Iterator));
        if (!usingIterator.IsUndefined)
        {
            JsObject arr = Ops.IsConstructor(t) ? Ops.Construct((JsObject)t.Ref!, default) : Alg.ArrayCreate(0);
            IteratorRecord rec = Ops.GetIteratorFromMethod(items, usingIterator);
            long k = 0;
            while (true)
            {
                if (k >= Alg.MaxSafeInteger)
                {
                    Ops.IteratorCloseSilently(rec);
                    throw Errors.Type("Invalid array length");
                }

                Agent.Current.Tick();
                if (!Ops.IteratorStepValue(rec, out JsValue next))
                {
                    Alg.SetLength(arr, k);
                    return JsValue.FromObject(arr);
                }

                try
                {
                    JsValue mapped = mapping ? Ops.Call(mapper, thisArg, [next, Ops.Num(k)]) : next;
                    Alg.Create(arr, k, mapped);
                }
                catch (JsException)
                {
                    Ops.IteratorCloseSilently(rec);
                    throw;
                }

                k++;
            }
        }

        // The source is not iterable: treat it as an array-like.
        JsObject arrayLike = Ops.ToObject(items);
        long len = Ops.LengthOfArrayLike(arrayLike);
        JsObject res = Ops.IsConstructor(t) ? Ops.Construct((JsObject)t.Ref!, [Ops.Num(len)]) : Alg.ArrayCreate(len);
        for (long k = 0; k < len; k++)
        {
            JsValue kValue = Alg.Get(arrayLike, k);
            JsValue mapped = mapping ? Ops.Call(mapper, thisArg, [kValue, Ops.Num(k)]) : kValue;
            Alg.Create(res, k, mapped);
        }

        Alg.SetLength(res, len);
        return JsValue.FromObject(res);
    }

    // ---- accessors and searching -----------------------------------------------------------------------------------------

    internal static JsValue At(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        double rel = Ops.ToIntegerOrInfinity(Ops.Arg(a, 0));
        double k = rel >= 0 ? rel : len + rel;
        return k < 0 || k >= len ? default : Alg.Get(o, (long)k);
    }

    internal static JsValue IndexOf(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, "indexOf");
        long len = Ops.LengthOfArrayLike(o);
        if (len == 0)
        {
            return Ops.Num(-1);
        }

        double n = Ops.ToIntegerOrInfinity(Ops.Arg(a, 1));
        if (double.IsPositiveInfinity(n))
        {
            return Ops.Num(-1);
        }

        long k = n >= 0 ? (long)Math.Min(n, len) : (long)Math.Max(len + n, 0);
        JsValue target = Ops.Arg(a, 0);
        if (len - k > Alg.SparseThreshold && Alg.TryExistingIndices(o, k, len, out List<long> idx))
        {
            foreach (long i in idx)
            {
                if (Alg.Has(o, i) && Ops.StrictEquals(Alg.Get(o, i), target))
                {
                    return Ops.Num(i);
                }
            }

            return Ops.Num(-1);
        }

        for (; k < len; k++)
        {
            if (Alg.Has(o, k) && Ops.StrictEquals(Alg.Get(o, k), target))
            {
                return Ops.Num(k);
            }
        }

        return Ops.Num(-1);
    }

    internal static JsValue LastIndexOf(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        if (len == 0)
        {
            return Ops.Num(-1);
        }

        double n = a.Length > 1 ? Ops.ToIntegerOrInfinity(a[1]) : len - 1;
        if (double.IsNegativeInfinity(n))
        {
            return Ops.Num(-1);
        }

        long k = n >= 0 ? (long)Math.Min(n, len - 1) : (long)(len + n);
        JsValue target = Ops.Arg(a, 0);
        if (k >= Alg.SparseThreshold && Alg.TryExistingIndices(o, 0, k + 1, out List<long> idx))
        {
            for (int j = idx.Count - 1; j >= 0; j--)
            {
                if (Alg.Has(o, idx[j]) && Ops.StrictEquals(Alg.Get(o, idx[j]), target))
                {
                    return Ops.Num(idx[j]);
                }
            }

            return Ops.Num(-1);
        }

        for (; k >= 0; k--)
        {
            if (Alg.Has(o, k) && Ops.StrictEquals(Alg.Get(o, k), target))
            {
                return Ops.Num(k);
            }
        }

        return Ops.Num(-1);
    }

    internal static JsValue Includes(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        if (len == 0)
        {
            return JsValue.False;
        }

        double n = Ops.ToIntegerOrInfinity(Ops.Arg(a, 1));
        if (double.IsPositiveInfinity(n))
        {
            return JsValue.False;
        }

        long k = n >= 0 ? (long)Math.Min(n, len) : (long)Math.Max(len + n, 0);
        JsValue target = Ops.Arg(a, 0);
        if (!target.IsUndefined && len - k > Alg.SparseThreshold && Alg.TryExistingIndices(o, k, len, out List<long> idx))
        {
            // A hole reads as undefined, which can never equal a defined search value, so only existing keys matter.
            foreach (long i in idx)
            {
                if (Ops.SameValueZero(Alg.Get(o, i), target))
                {
                    return JsValue.True;
                }
            }

            return JsValue.False;
        }

        for (; k < len; k++)
        {
            if (Ops.SameValueZero(Alg.Get(o, k), target))
            {
                return JsValue.True;
            }
        }

        return JsValue.False;
    }

    /// <summary>find, findIndex, findLast and findLastIndex (23.1.3.9 to 23.1.3.12).</summary>
    internal static JsValue Find(JsValue t, ReadOnlySpan<JsValue> a, string name, bool fromEnd, bool wantIndex)
    {
        JsObject o = Alg.This(t, name);
        long len = Ops.LengthOfArrayLike(o);
        JsValue pred = Ops.Arg(a, 0);
        if (!Ops.IsCallable(pred))
        {
            throw Alg.NotFunction(pred);
        }

        JsValue thisArg = Ops.Arg(a, 1);
        for (long n = 0; n < len; n++)
        {
            long k = fromEnd ? len - 1 - n : n;
            JsValue v = Alg.Get(o, k);
            if (Ops.ToBoolean(Ops.Call(pred, thisArg, [v, Ops.Num(k), JsValue.FromObject(o)])))
            {
                return wantIndex ? Ops.Num(k) : v;
            }
        }

        return wantIndex ? Ops.Num(-1) : default;
    }

    // ---- callbacks over elements -----------------------------------------------------------------------------------------

    internal enum EachKind
    {
        ForEach,
        Map,
        Filter,
        Some,
        Every,
    }

    /// <summary>forEach, map, filter, some and every (23.1.3.15, 23.1.3.21, 23.1.3.8, 23.1.3.29, 23.1.3.7).</summary>
    internal static JsValue Each(JsValue t, ReadOnlySpan<JsValue> a, EachKind kind)
    {
        string name = kind switch { EachKind.ForEach => "forEach", EachKind.Map => "map", EachKind.Filter => "filter", EachKind.Some => "some", _ => "every" };
        JsObject o = Alg.This(t, name);
        long len = Ops.LengthOfArrayLike(o);
        JsValue f = Ops.Arg(a, 0);
        if (!Ops.IsCallable(f))
        {
            throw Alg.NotFunction(f);
        }

        JsValue thisArg = Ops.Arg(a, 1);
        JsObject? res = kind switch
        {
            EachKind.Map => Alg.SpeciesCreate(o, len),
            EachKind.Filter => Alg.SpeciesCreate(o, 0),
            _ => null,
        };
        long to = 0;
        JsValue self = JsValue.FromObject(o);
        for (long k = 0; k < len; k++)
        {
            if (!Alg.Has(o, k))
            {
                continue;
            }

            JsValue v = Alg.Get(o, k);
            JsValue rv = Ops.Call(f, thisArg, [v, Ops.Num(k), self]);
            switch (kind)
            {
                case EachKind.Map:
                    Alg.Create(res!, k, rv);
                    break;
                case EachKind.Filter:
                    if (Ops.ToBoolean(rv))
                    {
                        Alg.Create(res!, to++, v);
                    }

                    break;
                case EachKind.Some:
                    if (Ops.ToBoolean(rv))
                    {
                        return JsValue.True;
                    }

                    break;
                case EachKind.Every:
                    if (!Ops.ToBoolean(rv))
                    {
                        return JsValue.False;
                    }

                    break;
            }
        }

        return kind switch
        {
            EachKind.Map or EachKind.Filter => JsValue.FromObject(res!),
            EachKind.Some => JsValue.False,
            EachKind.Every => JsValue.True,
            _ => default,
        };
    }

    /// <summary>reduce and reduceRight (23.1.3.24, 23.1.3.25).</summary>
    internal static JsValue Reduce(JsValue t, ReadOnlySpan<JsValue> a, bool right)
    {
        JsObject o = Alg.This(t, right ? "reduceRight" : "reduce");
        long len = Ops.LengthOfArrayLike(o);
        JsValue f = Ops.Arg(a, 0);
        if (!Ops.IsCallable(f))
        {
            throw Alg.NotFunction(f);
        }

        if (len == 0 && a.Length < 2)
        {
            throw Errors.Type("Reduce of empty array with no initial value");
        }

        long n = 0;
        JsValue acc = default;
        if (a.Length >= 2)
        {
            acc = a[1];
        }
        else
        {
            bool found = false;
            for (; n < len && !found; n++)
            {
                long k = right ? len - 1 - n : n;
                if (Alg.Has(o, k))
                {
                    acc = Alg.Get(o, k);
                    found = true;
                }
            }

            if (!found)
            {
                throw Errors.Type("Reduce of empty array with no initial value");
            }
        }

        JsValue self = JsValue.FromObject(o);
        for (; n < len; n++)
        {
            long k = right ? len - 1 - n : n;
            if (Alg.Has(o, k))
            {
                acc = Ops.Call(f, default, [acc, Alg.Get(o, k), Ops.Num(k), self]);
            }
        }

        return acc;
    }

    // ---- mutators --------------------------------------------------------------------------------------------------------

    /// <summary>True for a dense array whose slots 0..length-1 are all present, so element moves are plain copies.</summary>
    private static bool Packed(JsObject o, out JsArray ja)
    {
        if (o is JsArray arr && arr.Dense is not null && arr.Count == arr.Length)
        {
            Agent.Current.Charge(arr.Count);
            JsValue[] d = arr.Dense;
            for (int i = 0; i < arr.Count; i++)
            {
                if (d[i].IsHole)
                {
                    ja = arr;
                    return false;
                }
            }

            ja = arr;
            return true;
        }

        ja = null!;
        return false;
    }

    internal static JsValue Push(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        if (o is JsArray ja && ja.Dense is not null && ja.Count == ja.Length && ja.Extensible && ja.LengthWritable && Alg.ChainIndexFree(ja))
        {
            foreach (JsValue v in a)
            {
                ja.Push(v);
            }

            return Ops.Num(ja.Length);
        }

        long len = Ops.LengthOfArrayLike(o);
        if (len + a.Length > Alg.MaxSafeInteger)
        {
            throw Errors.Type("Pushing " + a.Length.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " elements on an array-like of length " + Ops.NumberToStr(len) + " is disallowed, as the total surpasses 2**53-1");
        }

        foreach (JsValue v in a)
        {
            Alg.Set(o, len++, v);
        }

        Alg.SetLength(o, len);
        return Ops.Num(len);
    }

    internal static JsValue Pop(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        if (len == 0)
        {
            Alg.SetLength(o, 0);
            return default;
        }

        PropertyKey k = Alg.Key(len - 1);
        JsValue v = o.Get(k);
        Alg.Delete(o, k);
        Alg.SetLength(o, len - 1);
        return v;
    }

    internal static JsValue Shift(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        if (len == 0)
        {
            Alg.SetLength(o, 0);
            return default;
        }

        if (Packed(o, out JsArray ja) && ja.LengthWritable)
        {
            JsValue first = ja.Dense![0];
            Array.Copy(ja.Dense, 1, ja.Dense, 0, ja.Count - 1);
            ja.Dense[ja.Count - 1] = default;
            ja.Count--;
            ja.Length--;
            return first;
        }

        JsValue result = Alg.Get(o, 0);
        for (long k = 1; k < len; k++)
        {
            if (Alg.Has(o, k))
            {
                Alg.Set(o, k - 1, Alg.Get(o, k));
            }
            else
            {
                Alg.Delete(o, k - 1);
            }
        }

        Alg.Delete(o, len - 1);
        Alg.SetLength(o, len - 1);
        return result;
    }

    internal static JsValue Unshift(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        int argCount = a.Length;
        if (argCount > 0)
        {
            if (len + argCount > Alg.MaxSafeInteger)
            {
                throw Errors.Type("Invalid array length");
            }

            if (Packed(o, out JsArray ja) && ja.Extensible && ja.LengthWritable && ja.Count + (long)argCount < int.MaxValue / 2 && Alg.ChainIndexFree(ja))
            {
                Agent.Current.Alloc((ja.Count + argCount) * 16L);
                int count = ja.Count;
                if (count + argCount > ja.Dense!.Length)
                {
                    Array.Resize(ref ja.Dense, Math.Max(count + argCount, count * 2));
                }

                Array.Copy(ja.Dense, 0, ja.Dense, argCount, count);
                a.CopyTo(ja.Dense);
                ja.Count = count + argCount;
                ja.Length = (uint)ja.Count;
                return Ops.Num(ja.Length);
            }

            for (long k = len; k > 0; k--)
            {
                long from = k - 1;
                long to = k + argCount - 1;
                if (Alg.Has(o, from))
                {
                    Alg.Set(o, to, Alg.Get(o, from));
                }
                else
                {
                    Alg.Delete(o, to);
                }
            }

            for (int j = 0; j < argCount; j++)
            {
                Alg.Set(o, j, a[j]);
            }
        }

        Alg.SetLength(o, len + argCount);
        return Ops.Num(len + argCount);
    }

    internal static JsValue Reverse(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        if (Packed(o, out JsArray ja))
        {
            Array.Reverse(ja.Dense!, 0, ja.Count);
            return JsValue.FromObject(o);
        }

        long middle = len / 2;
        for (long lower = 0; lower != middle; lower++)
        {
            long upper = len - lower - 1;
            bool lowerExists = Alg.Has(o, lower);
            JsValue lowerValue = lowerExists ? Alg.Get(o, lower) : default;
            bool upperExists = Alg.Has(o, upper);
            JsValue upperValue = upperExists ? Alg.Get(o, upper) : default;
            if (lowerExists && upperExists)
            {
                Alg.Set(o, lower, upperValue);
                Alg.Set(o, upper, lowerValue);
            }
            else if (upperExists)
            {
                Alg.Set(o, lower, upperValue);
                Alg.Delete(o, upper);
            }
            else if (lowerExists)
            {
                Alg.Delete(o, lower);
                Alg.Set(o, upper, lowerValue);
            }
        }

        return JsValue.FromObject(o);
    }

    internal static JsValue Fill(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        long k = Alg.Relative(Ops.Arg(a, 1), len, 0);
        long fin = Alg.Relative(Ops.Arg(a, 2), len, len);
        JsValue value = Ops.Arg(a, 0);
        for (; k < fin; k++)
        {
            Alg.Set(o, k, value);
        }

        return JsValue.FromObject(o);
    }

    internal static JsValue CopyWithin(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        long to = Alg.Relative(Ops.Arg(a, 0), len, 0);
        long from = Alg.Relative(Ops.Arg(a, 1), len, 0);
        long fin = Alg.Relative(Ops.Arg(a, 2), len, len);
        long count = Math.Min(fin - from, len - to);
        int dir = 1;
        if (from < to && to < from + count)
        {
            dir = -1;
            from = from + count - 1;
            to = to + count - 1;
        }

        for (; count > 0; count--, from += dir, to += dir)
        {
            if (Alg.Has(o, from))
            {
                Alg.Set(o, to, Alg.Get(o, from));
            }
            else
            {
                Alg.Delete(o, to);
            }
        }

        return JsValue.FromObject(o);
    }

    internal static JsValue Slice(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        long k = Alg.Relative(Ops.Arg(a, 0), len, 0);
        long fin = Alg.Relative(Ops.Arg(a, 1), len, len);
        long count = Math.Max(fin - k, 0);
        JsObject res = Alg.SpeciesCreate(o, count);
        long n = 0;
        if (count > 0 && o is JsArray src && res is JsArray dst && src.Dense is not null && dst.Dense is not null && dst.Count == 0
            && dst.Extensible && dst.LengthWritable && fin <= src.Count && count < int.MaxValue / 2 && NoHoles(src, (int)k, (int)fin))
        {
            Agent.Current.Alloc(count * 16);
            dst.Dense = new JsValue[count];
            Array.Copy(src.Dense, k, dst.Dense, 0, count);
            dst.Count = (int)count;
            if (dst.Length < count)
            {
                dst.Length = (uint)count;
            }

            n = count;
        }
        else
        {
            for (; k < fin; k++, n++)
            {
                if (Alg.Has(o, k))
                {
                    Alg.Create(res, n, Alg.Get(o, k));
                }
            }
        }

        Alg.SetLength(res, n);
        return JsValue.FromObject(res);
    }

    private static bool NoHoles(JsArray ja, int from, int to)
    {
        Agent.Current.Charge(to - from);
        for (int i = from; i < to; i++)
        {
            if (ja.Dense![i].IsHole)
            {
                return false;
            }
        }

        return true;
    }

    internal static JsValue Splice(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        long start = Alg.Relative(Ops.Arg(a, 0), len, 0);
        int itemCount = Math.Max(a.Length - 2, 0);
        long deleteCount;
        if (a.Length == 0)
        {
            deleteCount = 0;
        }
        else if (a.Length == 1)
        {
            deleteCount = len - start;
        }
        else
        {
            deleteCount = (long)Math.Min(Math.Max(Ops.ToIntegerOrInfinity(a[1]), 0), len - start);
        }

        if (len + itemCount - deleteCount > Alg.MaxSafeInteger)
        {
            throw Errors.Type("Invalid array length");
        }

        JsObject res = Alg.SpeciesCreate(o, deleteCount);
        for (long k = 0; k < deleteCount; k++)
        {
            long from = start + k;
            if (Alg.Has(o, from))
            {
                Alg.Create(res, k, Alg.Get(o, from));
            }
        }

        Alg.SetLength(res, deleteCount);
        if (itemCount < deleteCount)
        {
            if (Packed(o, out JsArray ja) && ja.LengthWritable)
            {
                int tail = ja.Count - (int)(start + deleteCount);
                Array.Copy(ja.Dense!, start + deleteCount, ja.Dense!, start + itemCount, tail);
                int newCount = ja.Count - (int)(deleteCount - itemCount);
                Array.Clear(ja.Dense!, newCount, ja.Count - newCount);
                ja.Count = newCount;
                ja.Length = (uint)newCount;
            }
            else
            {
                for (long k = start; k < len - deleteCount; k++)
                {
                    long from = k + deleteCount;
                    long to = k + itemCount;
                    if (Alg.Has(o, from))
                    {
                        Alg.Set(o, to, Alg.Get(o, from));
                    }
                    else
                    {
                        Alg.Delete(o, to);
                    }
                }

                for (long k = len; k > len - deleteCount + itemCount; k--)
                {
                    Alg.Delete(o, k - 1);
                }
            }
        }
        else if (itemCount > deleteCount)
        {
            if (Packed(o, out JsArray ja) && ja.Extensible && ja.LengthWritable && len + itemCount - deleteCount < int.MaxValue / 2 && Alg.ChainIndexFree(ja))
            {
                int grow = (int)(itemCount - deleteCount);
                Agent.Current.Alloc((len + grow) * 16);
                if (ja.Count + grow > ja.Dense!.Length)
                {
                    Array.Resize(ref ja.Dense, Math.Max(ja.Count + grow, ja.Count * 2));
                }

                Array.Copy(ja.Dense, start + deleteCount, ja.Dense, start + itemCount, ja.Count - (start + deleteCount));
                ja.Count += grow;
                ja.Length = (uint)ja.Count;
            }
            else
            {
                for (long k = len - deleteCount; k > start; k--)
                {
                    long from = k + deleteCount - 1;
                    long to = k + itemCount - 1;
                    if (Alg.Has(o, from))
                    {
                        Alg.Set(o, to, Alg.Get(o, from));
                    }
                    else
                    {
                        Alg.Delete(o, to);
                    }
                }
            }
        }

        for (int i = 0; i < itemCount; i++)
        {
            Alg.Set(o, start + i, a[i + 2]);
        }

        Alg.SetLength(o, len - deleteCount + itemCount);
        return JsValue.FromObject(res);
    }

    // ---- sorting ---------------------------------------------------------------------------------------------------------

    /// <summary>SortIndexedProperties (23.1.3.30.1).</summary>
    private static List<JsValue> SortIndexedProperties(JsObject o, long len, bool skipHoles)
    {
        var items = new List<JsValue>();
        if (skipHoles && len - 0 > Alg.SparseThreshold && Alg.TryExistingIndices(o, 0, len, out List<long> idx))
        {
            foreach (long i in idx)
            {
                if (Alg.Has(o, i))
                {
                    Agent.Current.Alloc(16);
                    items.Add(Alg.Get(o, i));
                }
            }

            return items;
        }

        if (!skipHoles)
        {
            Alg.ReserveList(len);
        }

        for (long k = 0; k < len; k++)
        {
            if (!skipHoles || Alg.Has(o, k))
            {
                Agent.Current.Alloc(16);
                items.Add(Alg.Get(o, k));
            }
        }

        return items;
    }

    internal static JsValue Sort(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsValue comparefn = Ops.Arg(a, 0);
        Alg.RequireComparator(comparefn);
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        List<JsValue> sorted = SortIndexedProperties(o, len, true);
        Alg.SortValues(sorted, comparefn);
        int itemCount = sorted.Count;
        int j = 0;
        for (; j < itemCount; j++)
        {
            Alg.Set(o, j, sorted[j]);
        }

        // The slots past the sorted items stay holes: delete what is still there.
        if (len - j > Alg.SparseThreshold && Alg.TryExistingIndices(o, j, len, out List<long> tail))
        {
            foreach (long i in tail)
            {
                Alg.Delete(o, i);
            }
        }
        else
        {
            for (long k = j; k < len; k++)
            {
                Alg.Delete(o, k);
            }
        }

        return JsValue.FromObject(o);
    }

    internal static JsValue ToSorted(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsValue comparefn = Ops.Arg(a, 0);
        Alg.RequireComparator(comparefn);
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        JsArray res = Alg.ArrayCreate(len);
        List<JsValue> sorted = SortIndexedProperties(o, len, false);
        Alg.SortValues(sorted, comparefn);
        for (int j = 0; j < sorted.Count; j++)
        {
            Alg.Create(res, j, sorted[j]);
        }

        return JsValue.FromObject(res);
    }

    internal static JsValue ToReversed(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        JsArray res = Alg.ArrayCreate(len);
        Alg.ReserveList(len);
        for (long k = 0; k < len; k++)
        {
            Alg.Create(res, k, Alg.Get(o, len - k - 1));
        }

        return JsValue.FromObject(res);
    }

    internal static JsValue ToSpliced(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        long start = Alg.Relative(Ops.Arg(a, 0), len, 0);
        int insertCount = Math.Max(a.Length - 2, 0);
        long skipCount;
        if (a.Length == 0)
        {
            skipCount = 0;
        }
        else if (a.Length == 1)
        {
            skipCount = len - start;
        }
        else
        {
            skipCount = (long)Math.Min(Math.Max(Ops.ToIntegerOrInfinity(a[1]), 0), len - start);
        }

        long newLen = len + insertCount - skipCount;
        if (newLen > Alg.MaxSafeInteger)
        {
            throw Errors.Type("Invalid array length");
        }

        JsArray res = Alg.ArrayCreate(newLen);
        Alg.ReserveList(newLen);
        long i = 0;
        long r = start + skipCount;
        for (; i < start; i++)
        {
            Alg.Create(res, i, Alg.Get(o, i));
        }

        for (int j = 0; j < insertCount; j++, i++)
        {
            Alg.Create(res, i, a[j + 2]);
        }

        for (; i < newLen; i++, r++)
        {
            Alg.Create(res, i, Alg.Get(o, r));
        }

        return JsValue.FromObject(res);
    }

    internal static JsValue With(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        double rel = Ops.ToIntegerOrInfinity(Ops.Arg(a, 0));
        double actual = rel >= 0 ? rel : len + rel;
        if (actual >= len || actual < 0)
        {
            throw Errors.Range("Invalid index : " + Ops.NumberToStr(rel));
        }

        JsArray res = Alg.ArrayCreate(len);
        Alg.ReserveList(len);
        JsValue value = Ops.Arg(a, 1);
        for (long k = 0; k < len; k++)
        {
            Alg.Create(res, k, k == (long)actual ? value : Alg.Get(o, k));
        }

        return JsValue.FromObject(res);
    }

    // ---- concat, flat -----------------------------------------------------------------------------------------------------

    internal static JsValue Concat(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, "concat");
        JsObject res = Alg.SpeciesCreate(o, 0);
        long n = 0;
        for (int i = -1; i < a.Length; i++)
        {
            JsValue e = i < 0 ? JsValue.FromObject(o) : a[i];
            if (IsConcatSpreadable(e))
            {
                var eo = (JsObject)e.Ref!;
                long len = Ops.LengthOfArrayLike(eo);
                if (n + len > Alg.MaxSafeInteger)
                {
                    throw Errors.Type("Invalid array length");
                }

                for (long k = 0; k < len; k++, n++)
                {
                    if (Alg.Has(eo, k))
                    {
                        Alg.Create(res, n, Alg.Get(eo, k));
                    }
                }
            }
            else
            {
                if (n >= Alg.MaxSafeInteger)
                {
                    throw Errors.Type("Invalid array length");
                }

                Alg.Create(res, n++, e);
            }
        }

        Alg.SetLength(res, n);
        return JsValue.FromObject(res);
    }

    /// <summary>IsConcatSpreadable (23.1.3.2.1).</summary>
    private static bool IsConcatSpreadable(JsValue e)
    {
        if (e.Ref is not JsObject eo)
        {
            return false;
        }

        JsValue spreadable = eo.Get(new PropertyKey(WellKnown.IsConcatSpreadable));
        return !spreadable.IsUndefined ? Ops.ToBoolean(spreadable) : Ops.IsArray(e);
    }

    internal static JsValue Flat(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        double depth = 1;
        if (!Ops.Arg(a, 0).IsUndefined)
        {
            depth = Ops.ToIntegerOrInfinity(a[0]);
            if (depth < 0)
            {
                depth = 0;
            }
        }

        JsObject res = Alg.SpeciesCreate(o, 0);
        Alg.FlattenIntoArray(res, o, len, 0, depth, default, default);
        return JsValue.FromObject(res);
    }

    internal static JsValue FlatMap(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        JsValue mapper = Ops.Arg(a, 0);
        if (!Ops.IsCallable(mapper))
        {
            throw Alg.NotFunction(mapper);
        }

        JsObject res = Alg.SpeciesCreate(o, 0);
        Alg.FlattenIntoArray(res, o, len, 0, 1, mapper, Ops.Arg(a, 1));
        return JsValue.FromObject(res);
    }

    // ---- strings ---------------------------------------------------------------------------------------------------------

    /// <summary>Array objects currently being joined; a cycle contributes the empty string.</summary>
    [ThreadStatic]
    private static List<JsObject>? t_joining;

    private static string JoinCore(JsObject o, long len, string sep, bool locale, ReadOnlySpan<JsValue> localeArgs)
    {
        List<JsObject> stack = t_joining ??= [];
        foreach (JsObject s in stack)
        {
            if (ReferenceEquals(s, o))
            {
                return string.Empty;
            }
        }

        if (stack.Count > 1000 || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw Errors.Range("Maximum call stack size exceeded");
        }

        stack.Add(o);
        try
        {
            var sb = new StringBuilder();
            JsValue[] fwd = [Ops.Arg(localeArgs, 0), Ops.Arg(localeArgs, 1)];
            for (long k = 0; k < len; k++)
            {
                if (k > 0)
                {
                    sb.Append(sep);
                }

                JsValue v = Alg.Get(o, k);
                if (!v.IsNullish)
                {
                    sb.Append(locale ? Ops.ToStr(Ops.Invoke(v, "toLocaleString", fwd)) : Ops.ToStr(v));
                }

                Alg.CheckStringLength(sb.Length);
            }

            return sb.ToString();
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    internal static JsValue Join(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        JsValue sepArg = Ops.Arg(a, 0);
        string sep = sepArg.IsUndefined ? "," : Ops.ToStr(sepArg);
        return Ops.Str(JoinCore(o, len, sep, false, default));
    }

    internal static JsValue ToLocaleString(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        long len = Ops.LengthOfArrayLike(o);
        return Ops.Str(JoinCore(o, len, ",", true, a));
    }

    internal static JsValue ToString(JsValue t, ReadOnlySpan<JsValue> a, JsObject? nt)
    {
        JsObject o = Alg.This(t, null);
        JsValue j = o.Get("join");
        return Ops.IsCallable(j) ? Ops.Call(j, JsValue.FromObject(o)) : Ops.Str(Builtins.ObjectToString(JsValue.FromObject(o)));
    }
}
