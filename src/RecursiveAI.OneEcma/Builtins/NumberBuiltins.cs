using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    private static void SetupNumber(Realm r)
    {
        var nproto = new JsPrimitiveObject(r.ObjectPrototype, JsValue.FromNumber(0), "Number");
        r.NumberPrototype = nproto;
        NativeFunction nctor = r.Ctor("Number", 1, (t, a, nt) =>
        {
            double n = 0;
            if (a.Length > 0)
            {
                JsValue p = Ops.ToNumeric(a[0]);
                n = p.IsBigInt ? (double)(BigInteger)p.Ref! : p.Num;
            }

            if (nt is null)
            {
                return Ops.Num(n);
            }

            return JsValue.FromObject(new JsPrimitiveObject(Ops.GetPrototypeFromConstructor(nt, x => x.NumberPrototype), Ops.Num(n), "Number"));
        }, nproto);
        double ThisNum(JsValue t) => t.IsNumber ? t.Num : t.Ref is JsPrimitiveObject { ClassName: "Number" } p ? p.Value.Num : throw Errors.Type("Number.prototype.valueOf requires that 'this' be a Number");
        r.Method(nproto, "valueOf", 0, (t, a, nt) => Ops.Num(ThisNum(t)));
        r.Method(nproto, "toString", 1, (t, a, nt) =>
        {
            double n = ThisNum(t);
            double radix = A(a, 0).IsUndefined ? 10 : Ops.ToIntegerOrInfinity(A(a, 0));
            if (radix < 2 || radix > 36)
            {
                throw Errors.Range("toString() radix must be between 2 and 36");
            }

            return Ops.Str(radix == 10 ? Ops.NumberToStr(n) : NumberConv.ToRadixString(n, (int)radix));
        });
        Realm.Value(nctor, "MAX_SAFE_INTEGER", Ops.Num(9007199254740991d), PropFlags.None);
        Realm.Value(nctor, "MIN_SAFE_INTEGER", Ops.Num(-9007199254740991d), PropFlags.None);
        Realm.Value(nctor, "MAX_VALUE", Ops.Num(double.MaxValue), PropFlags.None);
        Realm.Value(nctor, "MIN_VALUE", Ops.Num(double.Epsilon), PropFlags.None);
        Realm.Value(nctor, "EPSILON", Ops.Num(Math.Pow(2, -52)), PropFlags.None);
        Realm.Value(nctor, "NaN", Ops.Num(double.NaN), PropFlags.None);
        Realm.Value(nctor, "POSITIVE_INFINITY", Ops.Num(double.PositiveInfinity), PropFlags.None);
        Realm.Value(nctor, "NEGATIVE_INFINITY", Ops.Num(double.NegativeInfinity), PropFlags.None);
        r.Method(nctor, "isNaN", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsNaN(A(a, 0).Num)));
        r.Method(nctor, "isFinite", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsFinite(A(a, 0).Num)));
        r.Method(nctor, "isInteger", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsFinite(A(a, 0).Num) && Math.Floor(A(a, 0).Num) == A(a, 0).Num));
        r.Method(nctor, "isSafeInteger", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsFinite(A(a, 0).Num) && Math.Floor(A(a, 0).Num) == A(a, 0).Num && Math.Abs(A(a, 0).Num) <= 9007199254740991d));
    }

    private static void SetupBigInt(Realm r)
    {
        var biproto = r.NewObject();
        r.BigIntPrototype = biproto;
        r.Ctor("BigInt", 1, (t, a, nt) =>
        {
            if (nt is not null)
            {
                throw Errors.Type("BigInt is not a constructor");
            }

            JsValue p = Ops.ToPrimitive(A(a, 0), "number");
            if (p.IsNumber)
            {
                if (!double.IsFinite(p.Num) || Math.Floor(p.Num) != p.Num)
                {
                    throw Errors.Range("The number " + Ops.NumberToStr(p.Num) + " cannot be converted to a BigInt because it is not an integer");
                }

                return JsValue.FromBigInt(new BigInteger(p.Num));
            }

            return JsValue.FromBigInt(ToBigInt(p));
        }, biproto);
        BigInteger ThisBig(JsValue t) => t.Ref is BigInteger b ? b : t.Ref is JsPrimitiveObject { ClassName: "BigInt" } p ? (BigInteger)p.Value.Ref! : throw Errors.Type("BigInt.prototype.valueOf requires that 'this' be a BigInt");
        r.Method(biproto, "valueOf", 0, (t, a, nt) => JsValue.FromBigInt(ThisBig(t)));
        r.Method(biproto, "toString", 0, (t, a, nt) =>
        {
            BigInteger b = ThisBig(t);
            double radix = A(a, 0).IsUndefined ? 10 : Ops.ToIntegerOrInfinity(A(a, 0));
            if (radix < 2 || radix > 36)
            {
                throw Errors.Range("toString() radix must be between 2 and 36");
            }

            if (radix == 10)
            {
                return Ops.Str(b.ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
            }

            var sb = new StringBuilder();
            BigInteger v = BigInteger.Abs(b);
            if (v.IsZero)
            {
                sb.Append('0');
            }

            while (v > 0)
            {
                sb.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(v % (int)radix)]);
                v /= (int)radix;
            }

            return Ops.Str((b.Sign < 0 ? "-" : string.Empty) + sb);
        });
        biproto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("BigInt"), PropFlags.Configurable);
    }

    internal static BigInteger ToBigInt(JsValue v)
    {
        JsValue p = Ops.ToPrimitive(v, "number");
        switch (p.Ref)
        {
            case BigInteger b:
                return b;
            case string or Rope:
                if (NumberConv.TryStringToBigInt(p.AsString, out BigInteger r))
                {
                    return r;
                }

                throw Errors.Syntax("Cannot convert " + p.AsString + " to a BigInt");
            case JsSymbol:
                throw Errors.Type("Cannot convert a Symbol value to a bigint");
            default:
                if (p.IsBoolean)
                {
                    return p.IsTrue ? BigInteger.One : BigInteger.Zero;
                }

                throw Errors.Type("Cannot convert " + Ops.DisplayValue(p) + " to a BigInt");
        }
    }
}
