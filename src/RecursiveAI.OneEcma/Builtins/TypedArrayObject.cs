using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneEcma;

/// <summary>The element types of Table 71 (ECMA-262 23.2) plus Float16Array (ES2025).</summary>
internal enum ElementKind : byte
{
    Int8,
    Uint8,
    Uint8Clamped,
    Int16,
    Uint16,
    Int32,
    Uint32,
    Float16,
    Float32,
    Float64,
    BigInt64,
    BigUint64,
}

/// <summary>Raw element access shared by typed arrays, DataView and Atomics (GetValueFromBuffer / SetValueInBuffer, 25.1.3.16 and 25.1.3.17).</summary>
internal static class ElementIo
{
    internal static readonly string[] Names =
    [
        "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array",
        "Float16Array", "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array",
    ];

    internal static int Size(ElementKind k) => k switch
    {
        ElementKind.Int8 or ElementKind.Uint8 or ElementKind.Uint8Clamped => 1,
        ElementKind.Int16 or ElementKind.Uint16 or ElementKind.Float16 => 2,
        ElementKind.Int32 or ElementKind.Uint32 or ElementKind.Float32 => 4,
        _ => 8,
    };

    internal static bool IsBig(ElementKind k) => k >= ElementKind.BigInt64;

    /// <summary>ToUint8Clamp (7.1.12).</summary>
    internal static byte Clamp(double d)
    {
        if (!(d > 0))
        {
            return 0;
        }

        if (d >= 255)
        {
            return 255;
        }

        return (byte)Math.Round(d, MidpointRounding.ToEven);
    }

    /// <summary>ToBigInt64 (7.1.15) and ToBigUint64 (7.1.16) share the modulo 2^64 step.</summary>
    internal static ulong ToUInt64(BigInteger b) => (ulong)(b & ulong.MaxValue);

    /// <summary>Reads one element. The span starts at the element and is at least as long as the element.</summary>
    internal static JsValue Read(ElementKind k, ReadOnlySpan<byte> s, bool little)
    {
        switch (k)
        {
            case ElementKind.Int8:
                return JsValue.FromNumber((sbyte)s[0]);
            case ElementKind.Uint8:
            case ElementKind.Uint8Clamped:
                return JsValue.FromNumber(s[0]);
            case ElementKind.Int16:
                return JsValue.FromNumber(little ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s));
            case ElementKind.Uint16:
                return JsValue.FromNumber(little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s));
            case ElementKind.Int32:
                return JsValue.FromNumber(little ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s));
            case ElementKind.Uint32:
                return JsValue.FromNumber(little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s));
            case ElementKind.Float16:
                return JsValue.FromNumber((double)(little ? BinaryPrimitives.ReadHalfLittleEndian(s) : BinaryPrimitives.ReadHalfBigEndian(s)));
            case ElementKind.Float32:
                return JsValue.FromNumber(little ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s));
            case ElementKind.Float64:
                return JsValue.FromNumber(little ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s));
            case ElementKind.BigInt64:
                return JsValue.FromBigInt(little ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s));
            default:
                return JsValue.FromBigInt(little ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s));
        }
    }

    /// <summary>Writes one element from an already converted value: a Number for number kinds, a BigInt for BigInt kinds.</summary>
    internal static void Write(ElementKind k, Span<byte> s, JsValue v, bool little)
    {
        switch (k)
        {
            case ElementKind.Int8:
            case ElementKind.Uint8:
                s[0] = unchecked((byte)Ops.ToInt32(v.Num));
                break;
            case ElementKind.Uint8Clamped:
                s[0] = Clamp(v.Num);
                break;
            case ElementKind.Int16:
            case ElementKind.Uint16:
                {
                    ushort x = unchecked((ushort)Ops.ToInt32(v.Num));
                    if (little)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(s, x);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(s, x);
                    }

                    break;
                }

            case ElementKind.Int32:
            case ElementKind.Uint32:
                {
                    uint x = unchecked((uint)Ops.ToInt32(v.Num));
                    if (little)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(s, x);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(s, x);
                    }

                    break;
                }

            case ElementKind.Float16:
                {
                    var h = (Half)v.Num;
                    if (little)
                    {
                        BinaryPrimitives.WriteHalfLittleEndian(s, h);
                    }
                    else
                    {
                        BinaryPrimitives.WriteHalfBigEndian(s, h);
                    }

                    break;
                }

            case ElementKind.Float32:
                if (little)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(s, (float)v.Num);
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(s, (float)v.Num);
                }

                break;
            case ElementKind.Float64:
                if (little)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(s, v.Num);
                }
                else
                {
                    BinaryPrimitives.WriteDoubleBigEndian(s, v.Num);
                }

                break;
            default:
                {
                    ulong x = ToUInt64((BigInteger)v.Ref!);
                    if (little)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(s, x);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(s, x);
                    }

                    break;
                }
        }
    }

    /// <summary>ToBigInt or ToNumber depending on the element type, the conversion every store performs first.</summary>
    internal static JsValue ToElementValue(ElementKind k, JsValue v) =>
        IsBig(k) ? JsValue.FromBigInt(Builtins.ToBigInt(v)) : (v.IsNumber ? v : JsValue.FromNumber(Ops.ToNumber(v)));
}

/// <summary>
/// A TypedArray: the integer-indexed exotic object of ECMA-262 10.4.5. Integer-valued canonical numeric keys never reach
/// ordinary property storage and never consult the prototype chain.
/// </summary>
internal sealed class JsTypedArray : JsObject
{
    internal JsArrayBuffer Buffer = null!;
    internal ElementKind Kind;
    internal int ByteOffset;

    /// <summary>[[ArrayLength]], or -1 for "auto" (a length-tracking view of a resizable buffer).</summary>
    internal int FixedLength;

    internal JsTypedArray(JsObject proto, ElementKind kind)
        : base(proto)
    {
        Kind = kind;
        OverridesGet = true;
        OverridesSet = true;
    }

    internal override string ClassName => ElementIo.Names[(int)Kind];

    internal override bool IsPlainStorage => false;

    internal int ElementSize => ElementIo.Size(Kind);

    internal bool IsBigInt => ElementIo.IsBig(Kind);

    internal bool LengthTracking => FixedLength < 0;

    /// <summary>IsTypedArrayFixedLength (10.4.5.15).</summary>
    internal bool IsFixedLength => FixedLength >= 0 && (Buffer.Shared || !Buffer.Resizable);

    /// <summary>[[PreventExtensions]] (10.4.5.?): a view that can change size could gain integer-indexed properties, so it cannot be made non-extensible.</summary>
    internal override bool PreventExtensions() => IsFixedLength && base.PreventExtensions();

    /// <summary>IsTypedArrayOutOfBounds (10.4.5.14), which also covers a detached buffer.</summary>
    internal bool IsOutOfBounds
    {
        get
        {
            if (Buffer.Detached)
            {
                return true;
            }

            long bufLen = Buffer.ByteLength;
            long end = FixedLength < 0 ? bufLen : ByteOffset + ((long)FixedLength * ElementSize);
            return ByteOffset > bufLen || end > bufLen;
        }
    }

    /// <summary>TypedArrayLength (10.4.5.13); zero when out of bounds, as the observable accessors report.</summary>
    internal int Length
    {
        get
        {
            if (IsOutOfBounds)
            {
                return 0;
            }

            return FixedLength >= 0 ? FixedLength : (Buffer.ByteLength - ByteOffset) / ElementSize;
        }
    }

    internal int ByteLength => Length * ElementSize;

    internal Span<byte> Bytes => Buffer.Data.AsSpan(ByteOffset);

    /// <summary>Element i, which the caller has checked against <see cref="Length"/>.</summary>
    internal JsValue GetElement(int i) => ElementIo.Read(Kind, Buffer.Data.AsSpan(ByteOffset + (i * ElementSize)), true);

    /// <summary>Stores a converted value at element i, which the caller has checked.</summary>
    internal void SetElementRaw(int i, JsValue converted) => ElementIo.Write(Kind, Buffer.Data.AsSpan(ByteOffset + (i * ElementSize)), converted, true);

    /// <summary>TypedArrayGetElement (10.4.5.17) for a Number index.</summary>
    internal JsValue GetByNumber(double d)
    {
        int len = Length;
        if (d >= 0 && d < len)
        {
            int i = (int)d;
            if (i == d)
            {
                return GetElement(i);
            }
        }

        return default;
    }

    /// <summary>TypedArraySetElement (10.4.5.18): converts first, then stores only if the index is still valid.</summary>
    internal void SetByNumber(double d, JsValue value)
    {
        JsValue num = ElementIo.ToElementValue(Kind, value);
        int len = Length;
        if (d >= 0 && d < len)
        {
            int i = (int)d;
            if (i == d)
            {
                SetElementRaw(i, num);
            }
        }
    }

    /// <summary>IsValidIntegerIndex (10.4.5.16) for a canonical numeric index.</summary>
    internal bool IsValidIndex(double d, out int index)
    {
        index = 0;
        if (double.IsNegative(d) && d == 0)
        {
            return false;
        }

        int len = Length;
        if (d >= 0 && d < len && Math.Floor(d) == d)
        {
            index = (int)d;
            return true;
        }

        return false;
    }

    /// <summary>CanonicalNumericIndexString (7.1.21): true when the string is the canonical text of a Number (or is "-0").</summary>
    internal static bool CanonicalNumeric(string s, out double n)
    {
        n = 0;
        if (s.Length == 0)
        {
            return false;
        }

        char c = s[0];
        if (!(c is >= '0' and <= '9' || c == '-' || c == 'I' || c == 'N'))
        {
            return false;
        }

        if (s == "-0")
        {
            n = -0.0;
            return true;
        }

        if (PropertyKey.TryParseIndex(s, out uint u))
        {
            n = u;
            return true;
        }

        n = Ops.ToNumber(JsValue.FromString(s));
        return Ops.NumberToStr(n) == s;
    }

    private static bool Numeric(PropertyKey key, out double n)
    {
        if (key.Ref is string s)
        {
            return CanonicalNumeric(s, out n);
        }

        n = 0;
        return false;
    }

    internal override bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        if (Numeric(key, out double n))
        {
            if (IsValidIndex(n, out int i))
            {
                desc = PropertyDescriptor.Data(GetElement(i), PropFlags.Default);
                return true;
            }

            desc = default;
            return false;
        }

        return base.GetOwnProperty(key, out desc);
    }

    internal override bool TryGetOwn(PropertyKey key, JsValue receiver, out JsValue value)
    {
        if (Numeric(key, out double n))
        {
            value = IsValidIndex(n, out int i) ? GetElement(i) : default;
            return IsValidIndex(n, out _);
        }

        return base.TryGetOwn(key, receiver, out value);
    }

    internal override bool HasProperty(PropertyKey key)
    {
        if (Numeric(key, out double n))
        {
            return IsValidIndex(n, out _);
        }

        return base.HasProperty(key);
    }

    internal override bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        if (Numeric(key, out double n))
        {
            if (!IsValidIndex(n, out _))
            {
                return false;
            }

            if ((desc.HasConfigurable && !desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) || desc.IsAccessor || (desc.HasWritable && !desc.Writable))
            {
                return false;
            }

            if (desc.HasValue)
            {
                SetByNumber(n, desc.Value);
            }

            return true;
        }

        return base.DefineOwnProperty(key, desc);
    }

    internal override JsValue Get(PropertyKey key, JsValue receiver)
    {
        if (Numeric(key, out double n))
        {
            return IsValidIndex(n, out int i) ? GetElement(i) : default;
        }

        return base.Get(key, receiver);
    }

    internal override bool Set(PropertyKey key, JsValue value, JsValue receiver)
    {
        if (Numeric(key, out double n))
        {
            if (ReferenceEquals(receiver.Ref, this))
            {
                SetByNumber(n, value);
                return true;
            }

            if (!IsValidIndex(n, out _))
            {
                return true;
            }
        }

        return base.Set(key, value, receiver);
    }

    internal override bool Delete(PropertyKey key)
    {
        if (Numeric(key, out double n))
        {
            return !IsValidIndex(n, out _);
        }

        return base.Delete(key);
    }

    internal override void OwnKeys(List<PropertyKey> keys)
    {
        int len = Length;
        Agent.Current.Charge(len >> 3);
        for (int i = 0; i < len; i++)
        {
            keys.Add(new PropertyKey(Ops.NumberToStr(i)));
        }

        base.OwnKeys(keys);
    }
}
