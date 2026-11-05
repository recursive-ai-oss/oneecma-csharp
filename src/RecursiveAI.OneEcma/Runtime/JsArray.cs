namespace RecursiveAI.OneEcma;

/// <summary>
/// The Array exotic object (ECMA-262 10.4.2). Elements live in a dense store while they all have default
/// attributes and the array is not sparse; otherwise they move to ordinary string-keyed properties.
/// </summary>
internal sealed class JsArray : JsObject
{
    internal JsValue[]? Dense;
    internal int Count;
    internal uint Length;
    internal bool LengthWritable = true;

    internal JsArray(JsObject? proto, int capacity = 0)
        : base(proto)
    {
        if (capacity == 0)
        {
            Dense = [];
        }
        else
        {
            Agent.CurrentOrNull?.Alloc(capacity * (long)ElementBytes);
            Dense = new JsValue[capacity];
        }
    }

    /// <summary>Bytes one dense element occupies (a <see cref="JsValue"/>); the unit the allocation budget is charged in.</summary>
    internal const int ElementBytes = 16;

    /// <summary>Largest element count a .NET array can hold; a bigger request is an invalid array length.</summary>
    internal const int MaxDenseCapacity = 0x7FFFFFC7;

    /// <summary>
    /// Reallocates the dense store to hold <paramref name="capacity"/> elements. The new size is charged to the allocation budget
    /// first, so one huge request ends in a memory-limit report instead of allocating; a request .NET cannot satisfy is a
    /// RangeError (ECMA-262 10.4.2.2: invalid array length) or, if the allocator itself fails, a memory-limit report.
    /// </summary>
    internal void GrowDense(long capacity)
    {
        Agent ag = Agent.Current;
        ag.Alloc(capacity * ElementBytes);
        if (capacity > MaxDenseCapacity)
        {
            throw Errors.Range("Invalid array length");
        }

        try
        {
            Array.Resize(ref Dense, (int)capacity);
        }
        catch (OutOfMemoryException)
        {
            throw ag.Limit(LimitKind.Memory, "Execution ran out of memory growing an array to " + capacity.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " elements");
        }
    }

    internal override string ClassName => "Array";

    internal override bool IsPlainStorage => false;

    private static readonly PropertyKey LengthKey = "length";

    internal static JsArray FromList(JsObject? proto, IReadOnlyList<JsValue> items)
    {
        // Not billed here: callers either charged for the list they built or are host code copying values it already holds.
        var a = new JsArray(proto) { Dense = items.Count == 0 ? [] : new JsValue[items.Count] };
        for (int i = 0; i < items.Count; i++)
        {
            a.Dense![i] = items[i];
        }

        a.Count = items.Count;
        a.Length = (uint)items.Count;
        return a;
    }

    /// <summary>Appends at the end (dense fast path used by literals and spread).</summary>
    internal void Push(JsValue v)
    {
        if (Dense is not null && Count == Length && Extensible && LengthWritable)
        {
            if (Count == Dense.Length)
            {
                GrowDense(Math.Max(4L, Count * 2L));
            }

            Dense[Count++] = v;
            Length++;
            return;
        }

        CreateDataPropertyOrThrow(new PropertyKey(Length.ToString(System.Globalization.NumberFormatInfo.InvariantInfo)), v);
    }

    /// <summary>Reads element i if it is present in the dense store.</summary>
    internal bool TryGetDense(uint i, out JsValue v)
    {
        if (Dense is not null && i < (uint)Count)
        {
            v = Dense[i];
            return !v.IsHole;
        }

        v = default;
        return false;
    }

    private void ToSlow()
    {
        if (Dense is null)
        {
            return;
        }

        JsValue[] d = Dense;
        int n = Count;
        // Billed in one step before anything changes, so a limit cannot leave the array half converted.
        ChargeNewProperties(n);
        Dense = null;
        Count = 0;
        for (int i = 0; i < n; i++)
        {
            if (!d[i].IsHole)
            {
                AddProperty(new PropertyKey(((uint)i).ToString(System.Globalization.NumberFormatInfo.InvariantInfo)), d[i], PropFlags.Default);
            }
        }
    }

    internal override bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        if (key.Ref is string s)
        {
            if (s == "length")
            {
                desc = PropertyDescriptor.Data(JsValue.FromNumber(Length), LengthWritable ? PropFlags.Writable : PropFlags.None);
                return true;
            }

            if (Dense is not null && PropertyKey.TryParseIndex(s, out uint i))
            {
                if (i < (uint)Count && !Dense[i].IsHole)
                {
                    desc = PropertyDescriptor.Data(Dense[i], PropFlags.Default);
                    return true;
                }

                desc = default;
                return false;
            }
        }

        return base.GetOwnProperty(key, out desc);
    }

    internal override bool TryGetOwn(PropertyKey key, JsValue receiver, out JsValue value)
    {
        if (key.Ref is string s)
        {
            if (s == "length")
            {
                value = JsValue.FromNumber(Length);
                return true;
            }

            if (Dense is not null && PropertyKey.TryParseIndex(s, out uint i))
            {
                if (i < (uint)Count && !Dense[i].IsHole)
                {
                    value = Dense[i];
                    return true;
                }

                value = default;
                return false;
            }
        }

        return base.TryGetOwn(key, receiver, out value);
    }

    internal override bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        if (key.Ref is string s)
        {
            if (s == "length")
            {
                return SetLength(desc);
            }

            if (PropertyKey.TryParseIndex(s, out uint idx))
            {
                if (idx >= Length && !LengthWritable)
                {
                    return false;
                }

                if (Dense is not null)
                {
                    bool plain = !desc.IsAccessor && (!desc.HasWritable || desc.Writable) && (!desc.HasEnumerable || desc.Enumerable) && (!desc.HasConfigurable || desc.Configurable);
                    // The dense store grows over a gap only while it stays at least about one slot in nine used, so a script writing x[2^k] cannot make it reserve gigabytes.
                    bool exists = idx < (uint)Count && !Dense[idx].IsHole;
                    bool full = desc.HasValue && desc.HasWritable && desc.HasEnumerable && desc.HasConfigurable;
                    if (plain && (exists || full) && (exists || Extensible) && (idx < (uint)Count || idx - (uint)Count <= 16u + ((uint)Count / 8u)))
                    {
                        if (exists)
                        {
                            if (desc.HasValue)
                            {
                                Dense[idx] = desc.Value;
                            }
                        }
                        else
                        {
                            if (idx >= (uint)Dense.Length)
                            {
                                GrowDense(Math.Max(idx + 1L, Math.Max(4L, Dense.Length * 2L)));
                            }

                            for (int j = Count; j < (int)idx; j++)
                            {
                                Dense[j] = JsValue.Hole;
                            }

                            Dense[idx] = desc.Value;
                            Count = Math.Max(Count, (int)idx + 1);
                        }

                        if (idx >= Length)
                        {
                            Length = idx + 1;
                        }

                        return true;
                    }

                    ToSlow();
                }

                if (!base.DefineOwnProperty(key, desc))
                {
                    return false;
                }

                if (idx >= Length)
                {
                    Length = idx + 1;
                }

                return true;
            }
        }

        return base.DefineOwnProperty(key, desc);
    }

    /// <summary>ArraySetLength (10.4.2.4).</summary>
    private bool SetLength(in PropertyDescriptor desc)
    {
        if (!desc.HasValue)
        {
            if ((desc.HasConfigurable && desc.Configurable) || (desc.HasEnumerable && desc.Enumerable) || desc.IsAccessor)
            {
                return false;
            }

            if (desc.HasWritable)
            {
                if (desc.Writable && !LengthWritable)
                {
                    return false;
                }

                if (!desc.Writable)
                {
                    LengthWritable = false;
                }
            }

            return true;
        }

        uint newLen = Ops.ToUint32(desc.Value);
        double numberLen = Ops.ToNumber(desc.Value);
        if (newLen != numberLen)
        {
            throw Errors.Range("Invalid array length");
        }

        bool newWritable = !desc.HasWritable || desc.Writable;
        if ((desc.HasConfigurable && desc.Configurable) || (desc.HasEnumerable && desc.Enumerable) || desc.IsAccessor)
        {
            return false;
        }

        if (newLen == Length)
        {
            if (desc.HasWritable && desc.Writable && !LengthWritable)
            {
                return false;
            }

            if (desc.HasWritable && !desc.Writable)
            {
                LengthWritable = false;
            }

            return true;
        }

        if (!LengthWritable)
        {
            return false;
        }

        if (newLen > Length)
        {
            Length = newLen;
            if (!newWritable)
            {
                LengthWritable = false;
            }

            return true;
        }

        bool ok = true;
        if (Dense is not null)
        {
            if (newLen < (uint)Count)
            {
                Array.Clear(Dense, (int)newLen, Count - (int)newLen);
                Count = (int)newLen;
            }

            Length = newLen;
        }
        else
        {
            var keys = new List<PropertyKey>();
            Shape.CollectKeys(keys);
            var idxs = new List<uint>();
            foreach (PropertyKey k in keys)
            {
                if (k.TryGetIndex(out uint i) && i >= newLen)
                {
                    idxs.Add(i);
                }
            }

            idxs.Sort();
            Length = Math.Max(Length, newLen);
            for (int j = idxs.Count - 1; j >= 0; j--)
            {
                if (!base.Delete(new PropertyKey(idxs[j].ToString(System.Globalization.NumberFormatInfo.InvariantInfo))))
                {
                    Length = idxs[j] + 1;
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                Length = newLen;
            }
        }

        if (!newWritable)
        {
            LengthWritable = false;
        }

        return ok;
    }

    internal override bool Delete(PropertyKey key)
    {
        if (key.Ref is string s)
        {
            if (s == "length")
            {
                return false;
            }

            if (Dense is not null && PropertyKey.TryParseIndex(s, out uint i))
            {
                if (i < (uint)Count)
                {
                    Dense[i] = JsValue.Hole;
                }

                return true;
            }
        }

        return base.Delete(key);
    }

    internal override void OwnKeys(List<PropertyKey> keys)
    {
        var all = new List<PropertyKey>(Shape.LiveCount);
        Shape.CollectKeys(all);
        if (Dense is not null)
        {
            for (int i = 0; i < Count; i++)
            {
                if (!Dense[i].IsHole)
                {
                    keys.Add(new PropertyKey(((uint)i).ToString(System.Globalization.NumberFormatInfo.InvariantInfo)));
                }
            }

            keys.Add(LengthKey);
            AppendOrdered(all, keys);
            return;
        }

        var ordered = new List<PropertyKey>(all.Count + 1);
        AppendOrdered(all, ordered);
        int n = 0;
        while (n < ordered.Count && ordered[n].TryGetIndex(out _))
        {
            n++;
        }

        keys.AddRange(ordered.GetRange(0, n));
        keys.Add(LengthKey);
        keys.AddRange(ordered.GetRange(n, ordered.Count - n));
    }

    internal override bool IsArrayExotic() => true;
}
