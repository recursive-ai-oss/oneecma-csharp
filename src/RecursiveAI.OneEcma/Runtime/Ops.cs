using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>The abstract operations of ECMA-262 clauses 7 (type conversion, testing, comparison, objects, iteration) and the operators of clause 13.</summary>
internal static class Ops
{
    private static readonly NumberFormatInfo Inv = NumberFormatInfo.InvariantInfo;
    private static readonly string[] SmallInts = BuildSmallInts();

    private static string[] BuildSmallInts()
    {
        var a = new string[1024];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = i.ToString(Inv);
        }

        return a;
    }

    internal static Realm CurrentRealm => Agent.Current.Realm;

    internal static JsValue Arg(ReadOnlySpan<JsValue> args, int i) => i < args.Length ? args[i] : default;

    internal static JsValue Str(string s) => JsValue.FromString(s);

    internal static JsValue Num(double d) => JsValue.FromNumber(d);

    internal static JsValue Bool(bool b) => JsValue.FromBoolean(b);

    internal static JsValue Obj(JsObject o) => JsValue.FromObject(o);

    // ---- type conversion ------------------------------------------------------------------

    internal static bool ToBoolean(JsValue v)
    {
        object? r = v.Ref;
        if (ReferenceEquals(r, JsValue.TrueTag))
        {
            return true;
        }

        if (r is null || ReferenceEquals(r, JsValue.FalseTag) || ReferenceEquals(r, JsValue.NullTag))
        {
            return false;
        }

        if (ReferenceEquals(r, JsValue.NumberTag))
        {
            return !(v.Num == 0 || double.IsNaN(v.Num));
        }

        return r switch
        {
            string s => s.Length != 0,
            Rope rp => rp.Length != 0,
            BigInteger b => !b.IsZero,
            JsObject o => !o.IsHtmlDda,
            _ => true,
        };
    }

    internal static JsValue ToPrimitive(JsValue input, string hint = "default")
    {
        if (input.Ref is not JsObject o)
        {
            return input;
        }

        JsValue exotic = GetMethod(input, new PropertyKey(WellKnown.ToPrimitive));
        if (!exotic.IsUndefined)
        {
            JsValue r = exotic.AsObject!.Call(input, [Str(hint)]);
            if (r.IsObject)
            {
                throw Errors.Type("Cannot convert object to primitive value");
            }

            return r;
        }

        return OrdinaryToPrimitive(o, hint == "string" ? "string" : "number");
    }

    internal static JsValue OrdinaryToPrimitive(JsObject o, string hint)
    {
        ReadOnlySpan<string> names = hint == "string" ? ["toString", "valueOf"] : ["valueOf", "toString"];
        foreach (string n in names)
        {
            JsValue m = o.Get(n);
            if (m.Ref is JsObject f && f.IsCallable)
            {
                JsValue r = f.Call(Obj(o), default);
                if (!r.IsObject)
                {
                    return r;
                }
            }
        }

        throw Errors.Type("Cannot convert object to primitive value");
    }

    internal static double ToNumber(JsValue v)
    {
        if (v.IsNumber)
        {
            return v.Num;
        }

        switch (v.Ref)
        {
            case null:
                return double.NaN;
            case string s:
                return NumberConv.StringToNumber(s);
            case Rope rp:
                return NumberConv.StringToNumber(rp.Flatten());
            case JsSymbol:
                throw Errors.Type("Cannot convert a Symbol value to a number");
            case BigInteger:
                throw Errors.Type("Cannot convert a BigInt value to a number");
            case JsObject:
                return ToNumber(ToPrimitive(v, "number"));
            default:
                return ReferenceEquals(v.Ref, JsValue.TrueTag) ? 1 : 0;
        }
    }

    /// <summary>ToNumeric (7.1.3): a number or a bigint value.</summary>
    internal static JsValue ToNumeric(JsValue v)
    {
        if (v.IsNumber)
        {
            return v;
        }

        JsValue p = ToPrimitive(v, "number");
        return p.IsBigInt ? p : Num(ToNumber(p));
    }

    internal static double ToIntegerOrInfinity(JsValue v)
    {
        double n = ToNumber(v);
        if (double.IsNaN(n) || n == 0)
        {
            return 0;
        }

        // Truncate(-0.5) is -0; the mathematical integer has no negative zero (7.1.5 step 4).
        return double.IsInfinity(n) ? n : Math.Truncate(n) + 0.0;
    }

    internal static int ToInt32(JsValue v) => v.IsNumber ? ToInt32(v.Num) : ToInt32(ToNumber(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ToInt32(double d)
    {
        int i = (int)d;
        if (i == d)
        {
            return i;
        }

        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return 0;
        }

        return unchecked((int)(long)(Math.Truncate(d) % 4294967296.0));
    }

    internal static uint ToUint32(JsValue v) => unchecked((uint)ToInt32(v));

    internal static uint ToUint32(double d) => unchecked((uint)ToInt32(d));

    internal static long ToLength(JsValue v)
    {
        double n = ToIntegerOrInfinity(v);
        return n <= 0 ? 0 : (long)Math.Min(n, 9007199254740991d);
    }

    /// <summary>ToIndex (7.1.22).</summary>
    internal static long ToIndex(JsValue v)
    {
        if (v.IsUndefined)
        {
            return 0;
        }

        double n = ToIntegerOrInfinity(v);
        if (n < 0 || n > 9007199254740991d)
        {
            throw Errors.Range("Invalid index");
        }

        return (long)n;
    }

    internal static string ToStr(JsValue v)
    {
        switch (v.Ref)
        {
            case string s:
                return s;
            case Rope rp:
                return rp.Flatten();
            case null:
                return "undefined";
            case JsSymbol:
                throw Errors.Type("Cannot convert a Symbol value to a string");
            case BigInteger b:
                return b.ToString(Inv);
            case JsObject:
                return ToStr(ToPrimitive(v, "string"));
            default:
                if (v.IsNumber)
                {
                    return NumberToStr(v.Num);
                }

                return ReferenceEquals(v.Ref, JsValue.NullTag) ? "null" : ReferenceEquals(v.Ref, JsValue.TrueTag) ? "true" : "false";
        }
    }

    /// <summary>ToString that keeps a rope as a rope.</summary>
    internal static JsValue ToStringValue(JsValue v) => v.IsString ? v : Str(ToStr(v));

    internal static string NumberToStr(double d)
    {
        if (d >= 0 && d < SmallInts.Length && d == (int)d)
        {
            return SmallInts[(int)d];
        }

        return NumberConv.ToString(d);
    }

    internal static JsObject ToObject(JsValue v)
    {
        Realm r = CurrentRealm;
        switch (v.Ref)
        {
            case JsObject o:
                return o;
            case null:
                throw Errors.Type("Cannot convert undefined or null to object");
            case string s:
                return new JsStringObject(r.StringPrototype, s);
            case Rope rp:
                return new JsStringObject(r.StringPrototype, rp.Flatten());
            case JsSymbol:
                return new JsPrimitiveObject(r.SymbolPrototype, v, "Symbol");
            case BigInteger:
                return new JsPrimitiveObject(r.BigIntPrototype, v, "BigInt");
            default:
                if (v.IsNumber)
                {
                    return new JsPrimitiveObject(r.NumberPrototype, v, "Number");
                }

                if (v.IsNull)
                {
                    throw Errors.Type("Cannot convert undefined or null to object");
                }

                return new JsPrimitiveObject(r.BooleanPrototype, v, "Boolean");
        }
    }

    internal static PropertyKey ToPropertyKey(JsValue v)
    {
        if (v.Ref is string s)
        {
            return new PropertyKey(s);
        }

        if (v.IsNumber)
        {
            return new PropertyKey(NumberToStr(v.Num));
        }

        if (v.Ref is JsSymbol sym)
        {
            return new PropertyKey(sym);
        }

        JsValue p = ToPrimitive(v, "string");
        return p.Ref is JsSymbol s2 ? new PropertyKey(s2) : new PropertyKey(ToStr(p));
    }

    internal static JsValue RequireObjectCoercible(JsValue v)
    {
        if (v.IsNullish)
        {
            throw Errors.Type("Cannot convert undefined or null to object");
        }

        return v;
    }

    // ---- testing and comparison -------------------------------------------------------------

    internal static bool IsCallable(JsValue v) => v.Ref is JsObject o && o.IsCallable;

    internal static bool IsConstructor(JsValue v) => v.Ref is JsObject o && o.IsConstructor;

    internal static bool SameValue(JsValue a, JsValue b)
    {
        if (a.IsNumber)
        {
            return b.IsNumber && (a.Num.Equals(b.Num) && BitConverter.DoubleToInt64Bits(a.Num) == BitConverter.DoubleToInt64Bits(b.Num) || (double.IsNaN(a.Num) && double.IsNaN(b.Num)));
        }

        return SameValueNonNumber(a, b);
    }

    internal static bool SameValueZero(JsValue a, JsValue b)
    {
        if (a.IsNumber)
        {
            return b.IsNumber && (a.Num == b.Num || (double.IsNaN(a.Num) && double.IsNaN(b.Num)));
        }

        return SameValueNonNumber(a, b);
    }

    private static bool SameValueNonNumber(JsValue a, JsValue b)
    {
        if (a.IsString)
        {
            return b.IsString && a.AsString == b.AsString;
        }

        if (a.Ref is BigInteger x)
        {
            return b.Ref is BigInteger y && x == y;
        }

        return ReferenceEquals(a.Ref, b.Ref);
    }

    internal static bool StrictEquals(JsValue a, JsValue b)
    {
        if (a.IsNumber)
        {
            return b.IsNumber && a.Num == b.Num;
        }

        return SameValueNonNumber(a, b);
    }

    /// <summary>IsLooselyEqual (7.2.14).</summary>
    internal static bool LooseEquals(JsValue a, JsValue b)
    {
        while (true)
        {
            if (a.IsNumber && b.IsNumber)
            {
                return a.Num == b.Num;
            }

            if (a.IsString && b.IsString)
            {
                return a.AsString == b.AsString;
            }

            if (a.IsNullish)
            {
                return b.IsNullish || (b.Ref is JsObject bo && bo.IsHtmlDda);
            }

            if (b.IsNullish)
            {
                return a.Ref is JsObject ao && ao.IsHtmlDda;
            }

            if (a.IsObject && b.IsObject)
            {
                return ReferenceEquals(a.Ref, b.Ref);
            }

            if (a.IsBoolean && b.IsBoolean)
            {
                return ReferenceEquals(a.Ref, b.Ref);
            }

            if (a.IsSymbol && b.IsSymbol)
            {
                return ReferenceEquals(a.Ref, b.Ref);
            }

            if (a.IsBigInt && b.IsBigInt)
            {
                return (BigInteger)a.Ref! == (BigInteger)b.Ref!;
            }

            if (a.IsNumber && b.IsString)
            {
                return a.Num == NumberConv.StringToNumber(b.AsString);
            }

            if (a.IsString && b.IsNumber)
            {
                return NumberConv.StringToNumber(a.AsString) == b.Num;
            }

            if (a.IsBigInt && b.IsString)
            {
                return NumberConv.TryStringToBigInt(b.AsString, out BigInteger n) && n == (BigInteger)a.Ref!;
            }

            if (a.IsString && b.IsBigInt)
            {
                (a, b) = (b, a);
                continue;
            }

            if (a.IsBoolean)
            {
                a = Num(a.IsTrue ? 1 : 0);
                continue;
            }

            if (b.IsBoolean)
            {
                b = Num(b.IsTrue ? 1 : 0);
                continue;
            }

            if (a.IsObject && !b.IsObject)
            {
                a = ToPrimitive(a);
                continue;
            }

            if (b.IsObject && !a.IsObject)
            {
                b = ToPrimitive(b);
                continue;
            }

            if (a.IsBigInt && b.IsNumber)
            {
                return BigIntEqualsNumber((BigInteger)a.Ref!, b.Num);
            }

            if (a.IsNumber && b.IsBigInt)
            {
                return BigIntEqualsNumber((BigInteger)b.Ref!, a.Num);
            }

            return false;
        }
    }

    private static bool BigIntEqualsNumber(BigInteger b, double d) => !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && new BigInteger(d) == b;

    /// <summary>IsLessThan (7.2.13). Returns 1 true, 0 false, -1 undefined.</summary>
    internal static int LessThan(JsValue x, JsValue y, bool leftFirst)
    {
        JsValue px;
        JsValue py;
        if (leftFirst)
        {
            px = ToPrimitive(x, "number");
            py = ToPrimitive(y, "number");
        }
        else
        {
            py = ToPrimitive(y, "number");
            px = ToPrimitive(x, "number");
        }

        if (px.IsString && py.IsString)
        {
            return string.CompareOrdinal(px.AsString, py.AsString) < 0 ? 1 : 0;
        }

        if (px.IsBigInt && py.IsString)
        {
            return NumberConv.TryStringToBigInt(py.AsString, out BigInteger ny) ? ((BigInteger)px.Ref! < ny ? 1 : 0) : -1;
        }

        if (px.IsString && py.IsBigInt)
        {
            return NumberConv.TryStringToBigInt(px.AsString, out BigInteger nx) ? (nx < (BigInteger)py.Ref! ? 1 : 0) : -1;
        }

        JsValue nxv = ToNumeric(px);
        JsValue nyv = ToNumeric(py);
        if (nxv.IsNumber && nyv.IsNumber)
        {
            if (double.IsNaN(nxv.Num) || double.IsNaN(nyv.Num))
            {
                return -1;
            }

            return nxv.Num < nyv.Num ? 1 : 0;
        }

        if (nxv.IsBigInt && nyv.IsBigInt)
        {
            return (BigInteger)nxv.Ref! < (BigInteger)nyv.Ref! ? 1 : 0;
        }

        if (nxv.IsBigInt)
        {
            double d = nyv.Num;
            if (double.IsNaN(d))
            {
                return -1;
            }

            if (double.IsInfinity(d))
            {
                return d > 0 ? 1 : 0;
            }

            return CompareBigNum((BigInteger)nxv.Ref!, d) < 0 ? 1 : 0;
        }
        else
        {
            double d = nxv.Num;
            if (double.IsNaN(d))
            {
                return -1;
            }

            if (double.IsInfinity(d))
            {
                return d < 0 ? 1 : 0;
            }

            return CompareBigNum((BigInteger)nyv.Ref!, d) > 0 ? 1 : 0;
        }
    }

    private static int CompareBigNum(BigInteger b, double d)
    {
        double fl = Math.Floor(d);
        int c = b.CompareTo(new BigInteger(fl));
        if (c != 0)
        {
            return c;
        }

        return fl < d ? -1 : 0;
    }

    // ---- operators ---------------------------------------------------------------------------

    internal static JsValue Concat(JsValue a, JsValue b)
    {
        int la = a.Ref is string sa ? sa.Length : ((Rope)a.Ref!).Length;
        int lb = b.Ref is string sb ? sb.Length : ((Rope)b.Ref!).Length;
        if (la == 0)
        {
            return b;
        }

        if (lb == 0)
        {
            return a;
        }

        long total = (long)la + lb;
        Agent ag = Agent.Current;
        if (total > ag.Limits.MaxStringLength)
        {
            throw Errors.Range("Invalid string length");
        }

        if (total < 24 && a.Ref is string x && b.Ref is string y)
        {
            return Str(string.Concat(x, y));
        }

        // A rope node is small; the characters are charged when the rope is flattened.
        ag.Alloc(48);
        return JsValue.FromRope(new Rope(a.Ref!, b.Ref!, (int)total));
    }

    internal static JsValue Add(JsValue a, JsValue b)
    {
        if (a.IsNumber && b.IsNumber)
        {
            return Num(a.Num + b.Num);
        }

        if (a.IsString && b.IsString)
        {
            return Concat(a, b);
        }

        JsValue pa = ToPrimitive(a);
        JsValue pb = ToPrimitive(b);
        if (pa.IsString || pb.IsString)
        {
            return Concat(ToStringValue(pa), ToStringValue(pb));
        }

        return Arith(Syntax.BinaryOp.Add, pa, pb);
    }

    /// <summary>ApplyStringOrNumericBinaryOperator (13.15.3) for the arithmetic, shift and bitwise operators.</summary>
    internal static JsValue Arith(Syntax.BinaryOp op, JsValue a, JsValue b)
    {
        JsValue na = ToNumeric(a);
        JsValue nb = ToNumeric(b);
        if (na.IsNumber && nb.IsNumber)
        {
            double x = na.Num;
            double y = nb.Num;
            switch (op)
            {
                case Syntax.BinaryOp.Add: return Num(x + y);
                case Syntax.BinaryOp.Subtract: return Num(x - y);
                case Syntax.BinaryOp.Multiply: return Num(x * y);
                case Syntax.BinaryOp.Divide: return Num(x / y);
                case Syntax.BinaryOp.Remainder: return Num(Remainder(x, y));
                case Syntax.BinaryOp.Exponent: return Num(Pow(x, y));
                case Syntax.BinaryOp.ShiftLeft: return Num(ToInt32(x) << (ToInt32(y) & 31));
                case Syntax.BinaryOp.ShiftRight: return Num(ToInt32(x) >> (ToInt32(y) & 31));
                case Syntax.BinaryOp.UnsignedShiftRight: return Num(ToUint32(x) >> (ToInt32(y) & 31));
                case Syntax.BinaryOp.BitwiseAnd: return Num(ToInt32(x) & ToInt32(y));
                case Syntax.BinaryOp.BitwiseOr: return Num(ToInt32(x) | ToInt32(y));
                default: return Num(ToInt32(x) ^ ToInt32(y));
            }
        }

        if (na.IsBigInt && nb.IsBigInt)
        {
            return BigIntOp(op, (BigInteger)na.Ref!, (BigInteger)nb.Ref!);
        }

        throw Errors.Type("Cannot mix BigInt and other types, use explicit conversions");
    }

    internal static double Remainder(double x, double y)
    {
        if (double.IsInfinity(y) && !double.IsInfinity(x))
        {
            return x;
        }

        return x % y;
    }

    internal static double Pow(double x, double y)
    {
        if (double.IsNaN(y))
        {
            return double.NaN;
        }

        if (y == 0)
        {
            return 1;
        }

        if (Math.Abs(x) == 1 && double.IsInfinity(y))
        {
            return double.NaN;
        }

        return Math.Pow(x, y);
    }

    private static JsValue BigIntOp(Syntax.BinaryOp op, BigInteger x, BigInteger y)
    {
        switch (op)
        {
            case Syntax.BinaryOp.Add: return JsValue.FromBigInt(x + y);
            case Syntax.BinaryOp.Subtract: return JsValue.FromBigInt(x - y);
            case Syntax.BinaryOp.Multiply: return JsValue.FromBigInt(x * y);
            case Syntax.BinaryOp.Divide:
                if (y.IsZero)
                {
                    throw Errors.Range("Division by zero");
                }

                return JsValue.FromBigInt(BigInteger.Divide(x, y));
            case Syntax.BinaryOp.Remainder:
                if (y.IsZero)
                {
                    throw Errors.Range("Division by zero");
                }

                return JsValue.FromBigInt(BigInteger.Remainder(x, y));
            case Syntax.BinaryOp.Exponent:
                if (y.Sign < 0)
                {
                    throw Errors.Range("undefined must be positive"); // V8 wording as printed by the reference engine
                }

                if (y > 10_000_000)
                {
                    throw Errors.Range("Maximum BigInt size exceeded");
                }

                Agent.Current.Alloc((long)(BigInteger.Log(BigInteger.Abs(x) + 1, 2) * (double)y / 8));
                return JsValue.FromBigInt(BigInteger.Pow(x, (int)y));
            case Syntax.BinaryOp.ShiftLeft: return JsValue.FromBigInt(Shift(x, y));
            case Syntax.BinaryOp.ShiftRight: return JsValue.FromBigInt(Shift(x, -y));
            case Syntax.BinaryOp.UnsignedShiftRight:
                throw Errors.Type("BigInts have no unsigned right shift, use >> instead");
            case Syntax.BinaryOp.BitwiseAnd: return JsValue.FromBigInt(x & y);
            case Syntax.BinaryOp.BitwiseOr: return JsValue.FromBigInt(x | y);
            default: return JsValue.FromBigInt(x ^ y);
        }
    }

    private static BigInteger Shift(BigInteger x, BigInteger by)
    {
        if (by > 1_000_000_000)
        {
            if (x.IsZero)
            {
                return x;
            }

            throw Errors.Range("Maximum BigInt size exceeded");
        }

        if (by < -1_000_000_000)
        {
            return x.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero;
        }

        int n = (int)by;
        if (n > 0)
        {
            Agent.Current.Alloc(n / 8);
        }

        return n >= 0 ? x << n : x >> -n;
    }

    // ---- property access on any value ---------------------------------------------------------

    internal static JsObject ProtoForPrimitive(JsValue v, out string? str)
    {
        Realm r = CurrentRealm;
        str = null;
        if (v.IsString)
        {
            str = v.AsString;
            return r.StringPrototype;
        }

        if (v.IsNumber)
        {
            return r.NumberPrototype;
        }

        if (v.IsBoolean)
        {
            return r.BooleanPrototype;
        }

        if (v.IsSymbol)
        {
            return r.SymbolPrototype;
        }

        return r.BigIntPrototype;
    }

    /// <summary>GetV (7.3.3): the property of any value, boxing primitives lazily.</summary>
    internal static JsValue GetV(JsValue v, PropertyKey key)
    {
        if (v.Ref is JsObject o)
        {
            return o.Get(key, v);
        }

        if (v.IsNullish)
        {
            throw Errors.Type("Cannot read properties of " + (v.IsNull ? "null" : "undefined") + " (reading '" + KeyText(key) + "')");
        }

        JsObject proto = ProtoForPrimitive(v, out string? s);
        if (s is not null)
        {
            if (key.Ref is string ks)
            {
                if (ks == "length")
                {
                    return Num(s.Length);
                }

                if (PropertyKey.TryParseIndex(ks, out uint i) && i < (uint)s.Length)
                {
                    return Str(s[(int)i].ToString());
                }
            }
        }

        return proto.Get(key, v);
    }

    internal static JsValue GetV(JsValue v, string name) => GetV(v, new PropertyKey(name));

    /// <summary>PutValue on a property reference whose base is any value (6.2.5.6 step 3).</summary>
    internal static void SetV(JsValue baseValue, PropertyKey key, JsValue value, bool strict)
    {
        if (baseValue.Ref is JsObject o)
        {
            if (!o.Set(key, value, baseValue) && strict)
            {
                throw Errors.Type(SetFailureMessage(o, key));
            }

            return;
        }

        if (baseValue.IsNullish)
        {
            throw Errors.Type("Cannot set properties of " + (baseValue.IsNull ? "null" : "undefined") + " (setting '" + KeyText(key) + "')");
        }

        JsObject proto = ProtoForPrimitive(baseValue, out _);
        bool ok = proto.Set(key, value, baseValue);
        if (!ok && strict)
        {
            // A string's length and index properties exist (read-only); V8 words that differently from creating a new one.
            if (baseValue.IsString && (key.String == "length" || (key.TryGetIndex(out uint si) && si < baseValue.AsString.Length)))
            {
                throw Errors.Type("Cannot assign to read only property '" + KeyText(key) + "' of string '" + ToStr(baseValue) + "'");
            }

            throw Errors.Type("Cannot create property '" + KeyText(key) + "' on " + baseValue.TypeOf + " '" + ToStr(baseValue) + "'");
        }
    }

    internal static string SetFailureMessage(JsObject o, PropertyKey key)
    {
        if (o is ProxyObject)
        {
            return "'set' on proxy: trap returned falsish for property '" + KeyText(key) + "'";
        }

        // V8 distinguishes a read-only data property, a getter-only accessor and a non-extensible object.
        JsObject? p = o;
        while (p is not null)
        {
            if (p.GetOwnProperty(key, out PropertyDescriptor d))
            {
                if (d.IsAccessor)
                {
                    return "Cannot set property " + KeyText(key) + " of " + ObjectLabel(o) + " which has only a getter";
                }

                return ReadOnlyMessage(o, key);
            }

            p = p.GetPrototypeOf();
        }

        return "Cannot add property " + KeyText(key) + ", object is not extensible";
    }

    /// <summary>V8's message for a store to a read-only data property: a function is shown by its source text, any other object by its label.</summary>
    internal static string ReadOnlyMessage(JsObject o, PropertyKey key) =>
        "Cannot assign to read only property '" + KeyText(key) + "' of " + (o.IsCallable && o is not ProxyObject ? "function '" : "object '") + ObjectLabel(o) + "'";

    /// <summary>
    /// How V8 names an object in "Cannot delete property 'x' of ..." and the other store failures. A function shows its source text;
    /// an Error its "Name: message" header; arrays, wrappers, dates, regular expressions and typed arrays "[object Class]";
    /// anything else "#&lt;Constructor&gt;", or "[object Object]" when no constructor function is reachable (null prototype).
    /// </summary>
    internal static string ObjectLabel(JsObject o)
    {
        if (o is ProxyObject)
        {
            return "#<Object>";
        }

        if (o.IsCallable)
        {
            return FunctionText(o);
        }

        if (o is JsError)
        {
            return Errors.Header(o);
        }

        if (o is JsArray or JsStringObject or JsPrimitiveObject or JsTypedArray || o.ClassName is "Date" or "RegExp")
        {
            return "[object " + o.ClassName + "]";
        }

        string name = ConstructorName(o);
        return name.Length == 0 ? "[object Object]" : "#<" + name + ">";
    }

    /// <summary>The text Function.prototype.toString gives (20.2.3.5): the source of a script function, otherwise a NativeFunction form.</summary>
    internal static string FunctionText(JsObject f)
    {
        if (f is ScriptFunction sf)
        {
            return sf.Code.SourceText;
        }

        // A bound function, a Proxy and the revoker have no name that fits the NativeFunction grammar (20.2.3.5).
        if (f is BoundFunction or ProxyObject)
        {
            return "function () { [native code] }";
        }

        string name = f is NativeFunction nf ? nf.InitialName : f.GetOwnProperty("name", out PropertyDescriptor d) && d.Value.IsString ? d.Value.AsString : string.Empty;
        return "function " + name + "() { [native code] }";
    }

    /// <summary>The name of the constructor function an object reaches through "constructor", read without running script; empty when there is none.</summary>
    internal static string ConstructorName(JsObject o)
    {
        JsValue c = default;
        for (JsObject? q = o; q is not null && q is not ProxyObject; q = q.Proto)
        {
            if (q.GetOwnProperty("constructor", out PropertyDescriptor cd))
            {
                c = cd.IsAccessor ? default : cd.Value;
                break;
            }
        }

        if (c.Ref is JsObject co && co.IsCallable)
        {
            if (co is ScriptFunction sf && sf.Code.Name.Length > 0)
            {
                return sf.Code.Name;
            }

            if (co.GetOwnProperty("name", out PropertyDescriptor d) && d.Value.IsString)
            {
                return d.Value.AsString;
            }
        }

        return string.Empty;
    }

    /// <summary>How V8 names a value in messages from built-ins and the iteration protocol: the type, then the value for primitives.</summary>
    internal static string KindText(JsValue v) => v.Ref switch
    {
        null => "undefined",
        JsSymbol => "symbol",
        BigInteger => "bigint",
        JsObject o => o.IsCallable ? "function" : "object",
        _ when v.IsNull => "object null",
        _ when v.IsString => "string \"" + v.AsString + "\"",
        _ => v.TypeOf + " " + DisplayValue(v),
    };

    /// <summary>GetMethod (7.3.11).</summary>
    internal static JsValue GetMethod(JsValue v, PropertyKey key)
    {
        JsValue f = GetV(v, key);
        if (f.IsNullish)
        {
            return default;
        }

        if (!IsCallable(f))
        {
            throw Errors.Type(KindText(f) + " is not a function");
        }

        return f;
    }

    internal static JsValue Call(JsValue f, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        if (f.Ref is JsObject o && o.IsCallable)
        {
            return o.Call(thisValue, args);
        }

        throw Errors.Type(DisplayValue(f) + " is not a function");
    }

    internal static JsValue Call(JsValue f, JsValue thisValue) => Call(f, thisValue, default);

    internal static JsValue Invoke(JsValue v, PropertyKey key, ReadOnlySpan<JsValue> args)
    {
        JsValue f = GetV(v, key);
        if (!IsCallable(f))
        {
            throw Errors.Type(DisplayValue(f) + " is not a function");
        }

        return ((JsObject)f.Ref!).Call(v, args);
    }

    internal static JsObject Construct(JsObject ctor, ReadOnlySpan<JsValue> args, JsObject? newTarget = null)
    {
        JsValue r = ctor.Construct(args, newTarget ?? ctor);
        return (JsObject)r.Ref!;
    }

    internal static bool IsArray(JsValue v)
    {
        if (v.Ref is JsArray)
        {
            return true;
        }

        return v.Ref is JsObject o && o.IsArrayExotic();
    }

    internal static long LengthOfArrayLike(JsObject o) => o is JsArray a ? a.Length : ToLength(o.Get("length"));

    /// <summary>OrdinaryHasInstance (7.3.21).</summary>
    internal static bool OrdinaryHasInstance(JsValue c, JsValue o)
    {
        if (!IsCallable(c))
        {
            return false;
        }

        if (c.Ref is BoundFunction bf)
        {
            return InstanceOf(o, Obj(bf.Target));
        }

        if (o.Ref is not JsObject obj)
        {
            return false;
        }

        JsValue p = ((JsObject)c.Ref!).Get("prototype");
        if (p.Ref is not JsObject proto)
        {
            throw Errors.Type("Function has non-object prototype '" + ToStr(p) + "' in instanceof check");
        }

        for (JsObject? q = obj.GetPrototypeOf(); q is not null; q = q.GetPrototypeOf())
        {
            if (ReferenceEquals(q, proto))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>InstanceofOperator (13.10.2).</summary>
    internal static bool InstanceOf(JsValue v, JsValue target)
    {
        if (target.Ref is not JsObject t)
        {
            throw Errors.Type("Right-hand side of 'instanceof' is not an object");
        }

        JsValue h = GetMethod(target, new PropertyKey(WellKnown.HasInstance));
        if (!h.IsUndefined)
        {
            return ToBoolean(((JsObject)h.Ref!).Call(target, [v]));
        }

        if (!t.IsCallable)
        {
            throw Errors.Type("Right-hand side of 'instanceof' is not callable");
        }

        return OrdinaryHasInstance(target, v);
    }

    /// <summary>GetPrototypeFromConstructor (10.1.14).</summary>
    internal static JsObject GetPrototypeFromConstructor(JsObject ctor, Func<Realm, JsObject> fallback)
    {
        JsValue p = ctor.Get("prototype");
        if (p.Ref is JsObject po)
        {
            return po;
        }

        return fallback(GetFunctionRealm(ctor));
    }

    internal static Realm GetFunctionRealm(JsObject f)
    {
        while (true)
        {
            switch (f)
            {
                case JsFunction jf when f is not BoundFunction:
                    return jf.Realm;
                case BoundFunction b:
                    f = b.Target;
                    continue;
                default:
                    if (f.ProxyTarget(out JsObject? t, out bool revoked))
                    {
                        if (revoked)
                        {
                            throw Errors.Type("Cannot perform 'GetFunctionRealm' on a proxy that has been revoked");
                        }

                        f = t!;
                        continue;
                    }

                    return CurrentRealm;
            }
        }
    }

    internal static JsObject OrdinaryCreateFromConstructor(JsObject? newTarget, Func<Realm, JsObject> fallback)
    {
        Realm r = CurrentRealm;
        if (newTarget is null)
        {
            return new JsObject(fallback(r));
        }

        return new JsObject(GetPrototypeFromConstructor(newTarget, fallback));
    }

    /// <summary>SpeciesConstructor (7.3.22).</summary>
    internal static JsObject SpeciesConstructor(JsObject o, JsObject defaultCtor)
    {
        JsValue c = o.Get("constructor");
        if (c.IsUndefined)
        {
            return defaultCtor;
        }

        if (c.Ref is not JsObject co)
        {
            throw Errors.Type("The .constructor property is not an object");
        }

        JsValue s = co.Get(new PropertyKey(WellKnown.Species));
        if (s.IsNullish)
        {
            return defaultCtor;
        }

        if (IsConstructor(s))
        {
            return (JsObject)s.Ref!;
        }

        throw Errors.Type("object.constructor[Symbol.species] is not a constructor");
    }

    /// <summary>CreateListFromArrayLike (7.3.18).</summary>
    internal static JsValue[] ListFromArrayLike(JsValue v)
    {
        if (v.Ref is not JsObject o)
        {
            throw Errors.Type("CreateListFromArrayLike called on non-object");
        }

        long n = LengthOfArrayLike(o);
        if (n > 10_000_000)
        {
            throw Errors.Range("Invalid array length");
        }

        Agent.Current.Alloc(n * 16);
        var list = new JsValue[n];
        for (long i = 0; i < n; i++)
        {
            list[i] = o.Get(new PropertyKey(NumberToStr(i)));
        }

        return list;
    }

    internal static JsArray CreateArray(IReadOnlyList<JsValue> items) => JsArray.FromList(CurrentRealm.ArrayPrototype, items);

    internal static JsObject CreateIterResult(JsValue value, bool done)
    {
        Realm r = CurrentRealm;
        var o = new JsObject(r.IterResultShape);
        o.Slots[0] = value;
        o.Slots[1] = Bool(done);
        return o;
    }

    // ---- iteration (7.4) ----------------------------------------------------------------------

    internal static IteratorRecord GetIteratorFromMethod(JsValue obj, JsValue method)
    {
        JsValue it = Call(method, obj);
        if (it.Ref is not JsObject io)
        {
            throw Errors.Type("Result of the Symbol.iterator method is not an object");
        }

        return new IteratorRecord(io, io.Get("next"));
    }

    internal static IteratorRecord GetIterator(JsValue obj, bool async = false)
    {
        if (async)
        {
            JsValue am = GetMethod(obj, new PropertyKey(WellKnown.AsyncIterator));
            if (am.IsUndefined)
            {
                JsValue sm = GetMethod(obj, new PropertyKey(WellKnown.Iterator));
                if (sm.IsUndefined)
                {
                    throw Errors.Type(DisplayValue(obj) + " is not async iterable");
                }

                IteratorRecord sync = GetIteratorFromMethod(obj, sm);
                return AsyncFromSync.Create(sync);
            }

            return GetIteratorFromMethod(obj, am);
        }

        // V8 words every failure to find an iterator here the same way, including a Symbol.iterator that is not callable.
        JsValue m = obj.IsNullish ? default : GetV(obj, new PropertyKey(WellKnown.Iterator));
        if (!IsCallable(m))
        {
            throw Errors.Type(KindText(obj) + " is not iterable (cannot read property Symbol(Symbol.iterator))");
        }

        return GetIteratorFromMethod(obj, m);
    }

    /// <summary>IteratorStepValue (7.4.10): true with the value, or false when done.</summary>
    internal static bool IteratorStepValue(IteratorRecord rec, out JsValue value)
    {
        value = default;
        JsValue r;
        try
        {
            r = Call(rec.Next, Obj(rec.Iterator));
            if (r.Ref is not JsObject ro)
            {
                throw Errors.Type("Iterator result " + DisplayValue(r) + " is not an object");
            }

            if (ToBoolean(ro.Get("done")))
            {
                rec.Done = true;
                return false;
            }

            value = ro.Get("value");
            return true;
        }
        catch (JsException)
        {
            rec.Done = true;
            throw;
        }
    }

    /// <summary>IteratorClose (7.4.11) for a normal completion.</summary>
    internal static void IteratorClose(IteratorRecord rec)
    {
        JsValue ret = GetMethod(Obj(rec.Iterator), "return");
        if (ret.IsUndefined)
        {
            return;
        }

        JsValue r = ((JsObject)ret.Ref!).Call(Obj(rec.Iterator), default);
        if (!r.IsObject)
        {
            throw Errors.Type("Iterator result " + DisplayValue(r) + " is not an object");
        }
    }

    /// <summary>IteratorClose for a throw completion: errors from return() are swallowed.</summary>
    internal static void IteratorCloseSilently(IteratorRecord rec)
    {
        try
        {
            JsValue ret = GetMethod(Obj(rec.Iterator), "return");
            if (!ret.IsUndefined)
            {
                ((JsObject)ret.Ref!).Call(Obj(rec.Iterator), default);
            }
        }
        catch (JsException)
        {
        }
    }

    internal static List<JsValue> IterableToList(JsValue iterable, JsValue method)
    {
        IteratorRecord rec = GetIteratorFromMethod(iterable, method);
        var list = new List<JsValue>();
        while (IteratorStepValue(rec, out JsValue v))
        {
            Agent.Current.Tick();
            list.Add(v);
        }

        return list;
    }

    // ---- messages -------------------------------------------------------------------------------

    internal static string KeyText(PropertyKey k) => k.Ref is string s ? s : "Symbol(" + ((JsSymbol)k.Ref).Description + ")";

    /// <summary>How V8 renders a value inside an error message ("x is not a function").</summary>
    internal static string DisplayValue(JsValue v)
    {
        switch (v.Ref)
        {
            case null: return "undefined";
            case string s: return "\"" + s + "\"";
            case Rope rp: return "\"" + rp.Flatten() + "\"";
            case JsSymbol sym: return sym.ToString();
            case BigInteger b: return b.ToString(Inv) + "n";
            case JsObject o:
                if (o.IsCallable)
                {
                    return o is ScriptFunction sf ? sf.Code.SourceText : "function";
                }

                if (o is JsArray)
                {
                    return "[object Array]";
                }

                return ConstructorName(o) is { Length: > 0 } cn ? "#<" + cn + ">" : "#<Object>";
            default:
                return v.IsNumber ? NumberToStr(v.Num) : v.IsNull ? "null" : v.IsTrue ? "true" : "false";
        }
    }

    internal static string DebugString(JsValue v)
    {
        switch (v.Ref)
        {
            case string s: return s;
            case Rope rp: return rp.Flatten();
            case JsObject o:
                if (o is JsError)
                {
                    try
                    {
                        JsValue st = o.Get("stack");
                        if (st.IsString)
                        {
                            return st.AsString;
                        }
                    }
                    catch (JsException)
                    {
                    }
                }

                return "[object " + o.ClassName + "]";
            default: return DisplayValue(v);
        }
    }

    /// <summary>Renders the thrown value of an uncaught exception without running script (no getters or toString).</summary>
    internal static string DescribeThrown(JsValue v)
    {
        if (v.Ref is JsError e)
        {
            var sb = new StringBuilder();
            JsObject? p = e;
            string name = "Error";
            string msg = "";
            for (JsObject? q = p; q is not null; q = q.Proto)
            {
                int ns = q.Shape.Lookup("name", out PropFlags nf);
                if (ns >= 0 && (nf & PropFlags.Accessor) == 0 && q.Slots[ns].IsString)
                {
                    name = q.Slots[ns].AsString;
                    break;
                }
            }

            int ms = e.Shape.Lookup("message", out PropFlags mf);
            if (ms >= 0 && (mf & PropFlags.Accessor) == 0 && e.Slots[ms].IsString)
            {
                msg = e.Slots[ms].AsString;
            }

            sb.Append(name);
            if (msg.Length > 0)
            {
                sb.Append(": ").Append(msg);
            }

            return sb.ToString();
        }

        if (v.Ref is JsObject o)
        {
            return "[object " + o.ClassName + "]";
        }

        return v.IsString ? v.AsString : DisplayValue(v);
    }
}

/// <summary>An Iterator Record (7.4.1).</summary>
internal sealed class IteratorRecord
{
    internal IteratorRecord(JsObject iterator, JsValue next)
    {
        Iterator = iterator;
        Next = next;
    }

    internal JsObject Iterator { get; }

    internal JsValue Next { get; }

    internal bool Done { get; set; }
}

/// <summary>The well-known symbols (6.1.5.1), shared by all realms.</summary>
internal static class WellKnown
{
    internal static readonly JsSymbol AsyncIterator = new("Symbol.asyncIterator");
    internal static readonly JsSymbol HasInstance = new("Symbol.hasInstance");
    internal static readonly JsSymbol IsConcatSpreadable = new("Symbol.isConcatSpreadable");
    internal static readonly JsSymbol Iterator = new("Symbol.iterator");
    internal static readonly JsSymbol Match = new("Symbol.match");
    internal static readonly JsSymbol MatchAll = new("Symbol.matchAll");
    internal static readonly JsSymbol Replace = new("Symbol.replace");
    internal static readonly JsSymbol Search = new("Symbol.search");
    internal static readonly JsSymbol Species = new("Symbol.species");
    internal static readonly JsSymbol Split = new("Symbol.split");
    internal static readonly JsSymbol ToPrimitive = new("Symbol.toPrimitive");
    internal static readonly JsSymbol ToStringTag = new("Symbol.toStringTag");
    internal static readonly JsSymbol Unscopables = new("Symbol.unscopables");
}
