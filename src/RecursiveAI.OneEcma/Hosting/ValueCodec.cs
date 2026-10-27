using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Copies a script value to and from the record log: undefined, null, booleans, numbers, strings, BigInts, arrays and plain
/// objects (cycles and shared objects included). Anything else — functions, host objects, dates, maps, promises — cannot be
/// recorded as data; the caller marks the result opaque instead.
/// </summary>
internal static class ValueCodec
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 20_000;

    private enum VT : byte
    {
        Undefined, Null, False, True, Number, String, BigInt, Array, Object, Ref,
    }

    internal static bool CanEncode(JsValue v)
    {
        var seen = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        int nodes = 0;
        return Check(v, seen, 0, ref nodes);
    }

    private static bool Check(JsValue v, HashSet<JsObject> seen, int depth, ref int nodes)
    {
        if (!v.IsObject)
        {
            return !v.IsSymbol;
        }

        var o = (JsObject)v.Ref!;
        if (seen.Contains(o))
        {
            return true;
        }

        if (depth > MaxDepth || ++nodes > MaxNodes || !IsPlain(o))
        {
            return false;
        }

        seen.Add(o);
        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (k.IsSymbol)
            {
                continue;
            }

            if (!o.GetOwnProperty(k, out PropertyDescriptor d) || d.IsAccessor)
            {
                return false;
            }

            if (o is JsArray && k.String == "length")
            {
                continue;
            }

            if (!Check(d.Value, seen, depth + 1, ref nodes))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlain(JsObject o) =>
        (o.GetType() == typeof(JsObject) && o.Proto is { } p && ReferenceEquals(p, Agent.Current.Realm.ObjectPrototype))
        || (o is JsArray && o.GetType() == typeof(JsArray) && ReferenceEquals(o.Proto, Agent.Current.Realm.ArrayPrototype));

    internal static void Encode(RecordLog log, JsValue v)
    {
        var ids = new Dictionary<JsObject, int>(ReferenceEqualityComparer.Instance);
        Write(log, v, ids);
    }

    private static void Write(RecordLog log, JsValue v, Dictionary<JsObject, int> ids)
    {
        if (v.IsUndefined)
        {
            log.Byte((byte)VT.Undefined);
        }
        else if (v.IsNull)
        {
            log.Byte((byte)VT.Null);
        }
        else if (v.IsBoolean)
        {
            log.Byte((byte)(Ops.ToBoolean(v) ? VT.True : VT.False));
        }
        else if (v.IsNumber)
        {
            log.Byte((byte)VT.Number);
            log.Double(v.AsNumber);
        }
        else if (v.IsString)
        {
            log.Byte((byte)VT.String);
            log.Str(v.AsString);
        }
        else if (v.IsBigInt)
        {
            log.Byte((byte)VT.BigInt);
            log.Str(((System.Numerics.BigInteger)v.Ref!).ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
        }
        else
        {
            var o = (JsObject)v.Ref!;
            if (ids.TryGetValue(o, out int id))
            {
                log.Byte((byte)VT.Ref);
                log.Varint((ulong)id);
                return;
            }

            ids[o] = ids.Count;
            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            var entries = new List<(string, JsValue)>();
            foreach (PropertyKey k in keys)
            {
                if (k.IsSymbol || (o is JsArray && k.String == "length") || !o.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable)
                {
                    continue;
                }

                entries.Add((k.String!, d.Value));
            }

            if (o is JsArray)
            {
                // Arrays are written by index; a hole reads back as a hole.
                uint len = (uint)Ops.LengthOfArrayLike(o);
                log.Byte((byte)VT.Array);
                log.Varint(len);
                log.Varint((ulong)entries.Count);
                foreach ((string key, JsValue val) in entries)
                {
                    log.Varint(uint.Parse(key, System.Globalization.NumberFormatInfo.InvariantInfo));
                    Write(log, val, ids);
                }
            }
            else
            {
                log.Byte((byte)VT.Object);
                log.Varint((ulong)entries.Count);
                foreach ((string key, JsValue val) in entries)
                {
                    log.Str(key);
                    Write(log, val, ids);
                }
            }
        }
    }

    internal static JsValue Decode(LogReader r, Realm realm)
    {
        var made = new List<JsObject>();
        return Read(r, realm, made);
    }

    private static JsValue Read(LogReader r, Realm realm, List<JsObject> made)
    {
        switch ((VT)r.Next())
        {
            case VT.Undefined: return default;
            case VT.Null: return JsValue.Null;
            case VT.False: return JsValue.False;
            case VT.True: return JsValue.True;
            case VT.Number: return JsValue.FromNumber(r.Double());
            case VT.String: return JsValue.FromString(r.Str());
            case VT.BigInt: return JsValue.FromBigInt(System.Numerics.BigInteger.Parse(r.Str(), System.Globalization.NumberFormatInfo.InvariantInfo));
            case VT.Ref: return JsValue.FromObject(made[checked((int)r.Varint())]);
            case VT.Array:
                {
                    uint len = (uint)r.Varint();
                    JsObject arr = Ops.CreateArray([]);
                    made.Add(arr);
                    arr.Set("length", JsValue.FromNumber(len), JsValue.FromObject(arr));
                    for (ulong n = r.Varint(); n > 0; n--)
                    {
                        string key = r.Varint().ToString(System.Globalization.NumberFormatInfo.InvariantInfo);
                        arr.CreateDataPropertyOrThrow(key, Read(r, realm, made));
                    }

                    return JsValue.FromObject(arr);
                }

            case VT.Object:
                {
                    JsObject o = realm.NewObject();
                    made.Add(o);
                    for (ulong n = r.Varint(); n > 0; n--)
                    {
                        string key = r.Str();
                        o.CreateDataPropertyOrThrow(key, Read(r, realm, made));
                    }

                    return JsValue.FromObject(o);
                }

            default:
                throw new FormatException("Bad value in the record log.");
        }
    }

    internal static void Skip(LogReader r, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("The record log nests values deeper than " + MaxDepth + " levels.");
        }

        switch ((VT)r.Next())
        {
            case VT.Number: r.Double(); break;
            case VT.String: case VT.BigInt: r.Str(); break;
            case VT.Ref: r.Varint(); break;
            case VT.Array:
                r.Varint();
                for (ulong n = r.Varint(); n > 0; n--)
                {
                    r.Varint();
                    Skip(r, depth + 1);
                }

                break;
            case VT.Object:
                for (ulong n = r.Varint(); n > 0; n--)
                {
                    r.Str();
                    Skip(r, depth + 1);
                }

                break;
        }
    }
}
