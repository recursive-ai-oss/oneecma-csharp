using System.Globalization;
using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Number constructor and Number.prototype (ECMA-262 21.1), and the global parseInt, parseFloat, isNaN and isFinite.</summary>
internal static partial class Builtins
{
    private static readonly NumberFormatInfo Inv = NumberFormatInfo.InvariantInfo;

    private static double ThisNumber(JsValue t, string method) =>
        t.IsNumber ? t.Num : t.Ref is JsPrimitiveObject { ClassName: "Number" } p ? p.Value.Num : throw Errors.Type("Number.prototype." + method + " requires that 'this' be a Number");

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
                n = p.IsBigInt ? BigIntToDouble((BigInteger)p.Ref!) : p.Num;
            }

            if (nt is null)
            {
                return Ops.Num(n);
            }

            return JsValue.FromObject(new JsPrimitiveObject(Ops.GetPrototypeFromConstructor(nt, x => x.NumberPrototype), Ops.Num(n), "Number"));
        }, nproto);
        r.Method(nproto, "valueOf", 0, (t, a, nt) => Ops.Num(ThisNumber(t, "valueOf")));
        r.Method(nproto, "toString", 1, (t, a, nt) =>
        {
            double n = ThisNumber(t, "toString");
            double radix = A(a, 0).IsUndefined ? 10 : Ops.ToIntegerOrInfinity(A(a, 0));
            if (radix < 2 || radix > 36)
            {
                throw Errors.Range("toString() radix argument must be between 2 and 36");
            }

            return Ops.Str(radix == 10 ? Ops.NumberToStr(n) : NumberConv.ToRadixString(n, (int)radix));
        });
        r.Method(nproto, "toLocaleString", 0, (t, a, nt) => Ops.Str(Ops.NumberToStr(ThisNumber(t, "toLocaleString"))));
        r.Method(nproto, "toFixed", 1, (t, a, nt) =>
        {
            double x = ThisNumber(t, "toFixed");
            double f = Ops.ToIntegerOrInfinity(A(a, 0));
            if (f < 0 || f > 100)
            {
                throw Errors.Range("toFixed() digits argument must be between 0 and 100");
            }

            return Ops.Str(NumberToText.ToFixed(x, (int)f));
        });
        r.Method(nproto, "toExponential", 1, (t, a, nt) =>
        {
            double x = ThisNumber(t, "toExponential");
            double f = Ops.ToIntegerOrInfinity(A(a, 0));
            if (!double.IsFinite(x))
            {
                return Ops.Str(Ops.NumberToStr(x));
            }

            if (f < 0 || f > 100)
            {
                throw Errors.Range("toExponential() argument must be between 0 and 100");
            }

            return Ops.Str(NumberToText.ToExponential(x, A(a, 0).IsUndefined ? -1 : (int)f));
        });
        r.Method(nproto, "toPrecision", 1, (t, a, nt) =>
        {
            double x = ThisNumber(t, "toPrecision");
            if (A(a, 0).IsUndefined)
            {
                return Ops.Str(Ops.NumberToStr(x));
            }

            double p = Ops.ToIntegerOrInfinity(A(a, 0));
            if (!double.IsFinite(x))
            {
                return Ops.Str(Ops.NumberToStr(x));
            }

            if (p < 1 || p > 100)
            {
                throw Errors.Range("toPrecision() argument must be between 1 and 100");
            }

            return Ops.Str(NumberToText.ToPrecision(x, (int)p));
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

        // 19.2: the global functions; Number.parseFloat and Number.parseInt are the same function objects (21.1.2.12, 21.1.2.13).
        JsObject g = r.Global;
        r.Method(g, "isNaN", 1, (t, a, nt) => Ops.Bool(double.IsNaN(Ops.ToNumber(A(a, 0)))));
        r.Method(g, "isFinite", 1, (t, a, nt) => Ops.Bool(double.IsFinite(Ops.ToNumber(A(a, 0)))));
        NativeFunction parseFloat = r.Method(g, "parseFloat", 1, (t, a, nt) => Ops.Num(ParseFloat(Ops.ToStr(A(a, 0)))));
        NativeFunction parseInt = r.Method(g, "parseInt", 2, (t, a, nt) =>
        {
            string s = Ops.ToStr(A(a, 0));
            return Ops.Num(ParseInt(s, Ops.ToInt32(A(a, 1))));
        });
        Realm.Value(nctor, "parseFloat", JsValue.FromObject(parseFloat));
        Realm.Value(nctor, "parseInt", JsValue.FromObject(parseInt));
        SetupUriFunctions(r);
    }

    /// <summary>parseFloat (19.2.4): the longest prefix that is a StrDecimalLiteral, after leading white space.</summary>
    private static double ParseFloat(string input)
    {
        Cost(input.Length);
        int i = 0;
        while (i < input.Length && IsTrimWhite(input[i]))
        {
            i++;
        }

        int start = i;
        if (i < input.Length && input[i] is '+' or '-')
        {
            i++;
        }

        if (string.CompareOrdinal(input, i, "Infinity", 0, 8) == 0)
        {
            return input[start] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }

        bool digits = false;
        while (i < input.Length && char.IsAsciiDigit(input[i]))
        {
            i++;
            digits = true;
        }

        if (i < input.Length && input[i] == '.')
        {
            int save = i++;
            bool frac = false;
            while (i < input.Length && char.IsAsciiDigit(input[i]))
            {
                i++;
                frac = true;
            }

            if (!digits && !frac)
            {
                i = save;
            }

            digits |= frac;
        }

        if (!digits)
        {
            return double.NaN;
        }

        if (i < input.Length && input[i] is 'e' or 'E')
        {
            int save = i++;
            if (i < input.Length && input[i] is '+' or '-')
            {
                i++;
            }

            if (i < input.Length && char.IsAsciiDigit(input[i]))
            {
                while (i < input.Length && char.IsAsciiDigit(input[i]))
                {
                    i++;
                }
            }
            else
            {
                i = save;
            }
        }

        string text = input.Substring(start, i - start);
        if (text.EndsWith('.'))
        {
            text = text[..^1];
        }

        return double.Parse(text, NumberStyles.Float, Inv);
    }

    /// <summary>parseInt (19.2.5).</summary>
    private static double ParseInt(string input, int radix)
    {
        Cost(input.Length);
        int i = 0;
        while (i < input.Length && IsTrimWhite(input[i]))
        {
            i++;
        }

        bool neg = false;
        if (i < input.Length && input[i] is '+' or '-')
        {
            neg = input[i] == '-';
            i++;
        }

        bool strip = true;
        if (radix != 0)
        {
            if (radix < 2 || radix > 36)
            {
                return double.NaN;
            }

            if (radix != 16)
            {
                strip = false;
            }
        }
        else
        {
            radix = 10;
        }

        if (strip && i + 1 < input.Length && input[i] == '0' && input[i + 1] is 'x' or 'X')
        {
            i += 2;
            radix = 16;
        }

        int begin = i;
        while (i < input.Length)
        {
            int d = NumberConv.DigitValue(input[i]);
            if (d < 0 || d >= radix)
            {
                break;
            }

            i++;
        }

        if (i == begin)
        {
            return double.NaN;
        }

        double v;
        if (radix == 10)
        {
            // For radix 10 more than 20 significant digits may be dropped by the spec; parse the full text for the correctly rounded value.
            v = double.Parse(input.AsSpan(begin, i - begin), NumberStyles.None, Inv);
        }
        else if ((radix & (radix - 1)) == 0)
        {
            v = ParsePow2(input.AsSpan(begin, i - begin), radix);
        }
        else
        {
            if (i - begin <= 10)
            {
                v = 0;
                for (int k = begin; k < i; k++)
                {
                    v = (v * radix) + NumberConv.DigitValue(input[k]);
                }

                return neg ? -v : v;
            }

            // Exact accumulation, then one correct rounding.
            var big = BigInteger.Zero;
            for (int k = begin; k < i; k++)
            {
                big = (big * radix) + NumberConv.DigitValue(input[k]);
            }

            v = BigIntToDouble(big);
        }

        return neg ? -v : v;
    }

    /// <summary>Digits in a power-of-two radix to the nearest double (round half to even), exactly.</summary>
    private static double ParsePow2(ReadOnlySpan<char> digits, int radix)
    {
        int bits = BitOperations.TrailingZeroCount(radix);
        BigInteger b = BigInteger.Zero;
        if (digits.Length * bits <= 53)
        {
            ulong u = 0;
            foreach (char c in digits)
            {
                u = (u << bits) | (uint)NumberConv.DigitValue(c);
            }

            return u;
        }

        foreach (char c in digits)
        {
            b = (b << bits) | NumberConv.DigitValue(c);
        }

        return BigIntToDouble(b);
    }

    /// <summary>The Number value of a BigInt (21.1.1.1 step 2.b): nearest double, ties to even; infinity beyond the range.</summary>
    internal static double BigIntToDouble(BigInteger v) => ScaledBigToDouble(v, 0);

    /// <summary>The double nearest to v × 2^scale (ties to even), computed from the top 64 bits and a sticky bit.</summary>
    private static double ScaledBigToDouble(BigInteger v, int scale)
    {
        if (v.IsZero)
        {
            return 0;
        }

        bool neg = v.Sign < 0;
        BigInteger m = BigInteger.Abs(v);
        long bitLength = (long)m.GetBitLength();
        double result;
        if (bitLength <= 63)
        {
            result = Math.ScaleB((double)(ulong)m, scale);
        }
        else
        {
            int shift = (int)Math.Min(bitLength - 64, 100_000);
            ulong top = (ulong)(m >> shift);
            bool sticky = !(m & ((BigInteger.One << shift) - 1)).IsZero;
            if (sticky)
            {
                top |= 1; // the sticky bit sits below the 11 bits the double drops, so ties round correctly
            }

            result = bitLength - 64 > 100_000 ? double.PositiveInfinity : Math.ScaleB((double)top, shift + scale);
        }

        return neg ? -result : result;
    }

    // ---- BigInt --------------------------------------------------------------------------------------------------

    private static BigInteger ThisBigInt(JsValue t, string method) =>
        t.Ref is BigInteger b ? b : t.Ref is JsPrimitiveObject { ClassName: "BigInt" } p ? (BigInteger)p.Value.Ref! : throw Errors.Type("BigInt.prototype." + method + " requires that 'this' be a BigInt");

    private static void SetupBigInt(Realm r)
    {
        var biproto = r.NewObject();
        r.BigIntPrototype = biproto;
        NativeFunction ctor = r.Ctor("BigInt", 1, (t, a, nt) =>
        {
            if (nt is not null)
            {
                throw Errors.Type("BigInt is not a constructor");
            }

            JsValue p = Ops.ToPrimitive(A(a, 0), "number");
            if (p.IsNumber)
            {
                // NumberToBigInt (21.2.1.1.1)
                if (!double.IsFinite(p.Num) || Math.Floor(p.Num) != p.Num)
                {
                    throw Errors.Range("The number " + Ops.NumberToStr(p.Num) + " cannot be converted to a BigInt because it is not an integer");
                }

                return JsValue.FromBigInt(new BigInteger(p.Num));
            }

            return JsValue.FromBigInt(ToBigInt(p));
        }, biproto);
        r.Method(ctor, "asIntN", 2, (t, a, nt) =>
        {
            long bits = BigIntBits(A(a, 0));
            BigInteger v = ToBigInt(A(a, 1));
            return JsValue.FromBigInt(AsN(bits, v, signed: true));
        });
        r.Method(ctor, "asUintN", 2, (t, a, nt) =>
        {
            long bits = BigIntBits(A(a, 0));
            BigInteger v = ToBigInt(A(a, 1));
            return JsValue.FromBigInt(AsN(bits, v, signed: false));
        });
        r.Method(biproto, "valueOf", 0, (t, a, nt) => JsValue.FromBigInt(ThisBigInt(t, "valueOf")));
        r.Method(biproto, "toLocaleString", 0, (t, a, nt) => Ops.Str(ThisBigInt(t, "toLocaleString").ToString(Inv)));
        r.Method(biproto, "toString", 0, (t, a, nt) =>
        {
            BigInteger b = ThisBigInt(t, "toString");
            double radix = A(a, 0).IsUndefined ? 10 : Ops.ToIntegerOrInfinity(A(a, 0));
            if (radix < 2 || radix > 36)
            {
                throw Errors.Range("toString() radix argument must be between 2 and 36");
            }

            return Ops.Str(BigIntToString(b, (int)radix));
        });
        biproto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("BigInt"), PropFlags.Configurable);
    }

    /// <summary>ToIndex for the bits argument of BigInt.asIntN and asUintN, with V8's wording for a value out of range.</summary>
    private static long BigIntBits(JsValue v)
    {
        double n = Ops.ToIntegerOrInfinity(v);
        if (n < 0 || n > 9007199254740991d)
        {
            throw Errors.Range("Invalid value: not (convertible to) a safe integer");
        }

        return (long)n;
    }

    /// <summary>BigInt.asIntN and BigInt.asUintN (21.2.2.1, 21.2.2.2) without materialising 2^bits for huge widths.</summary>
    private static BigInteger AsN(long bits, BigInteger v, bool signed)
    {
        if (bits == 0)
        {
            return BigInteger.Zero;
        }

        long len = (long)v.GetBitLength();
        if (signed)
        {
            // GetBitLength of a negative value excludes the sign bit; a value fits in `bits` signed bits when len < bits.
            if (len < bits)
            {
                return v;
            }
        }
        else if (v.Sign >= 0 && len <= bits)
        {
            return v;
        }

        if (bits > (1 << 30))
        {
            throw Errors.Range("Maximum BigInt size exceeded");
        }

        Agent.Current.Alloc(bits / 8);
        BigInteger mod = BigInteger.One << (int)bits;
        BigInteger res = v & (mod - 1);
        if (signed && !(res >> (int)(bits - 1)).IsZero)
        {
            res -= mod;
        }

        return res;
    }

    /// <summary>BigInt::toString (6.1.6.2.23) in any radix, in time proportional to the digit count.</summary>
    internal static string BigIntToString(BigInteger b, int radix)
    {
        if (radix == 10)
        {
            Cost((long)b.GetBitLength());
            return b.ToString(Inv);
        }

        if (b.IsZero)
        {
            return "0";
        }

        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        BigInteger v = BigInteger.Abs(b);
        long bitLength = (long)v.GetBitLength();
        Agent.Current.Alloc((bitLength / 2) + 16);
        Cost(bitLength * 4);
        var sb = new StringBuilder();
        if ((radix & (radix - 1)) == 0)
        {
            int bits = BitOperations.TrailingZeroCount(radix);
            byte[] bytes = v.ToByteArray(isUnsigned: true, isBigEndian: false);
            long total = bitLength;
            for (long pos = 0; pos < total; pos += bits)
            {
                int d = 0;
                for (int k = 0; k < bits; k++)
                {
                    long bit = pos + k;
                    if (bit < total && (bytes[bit >> 3] & (1 << (int)(bit & 7))) != 0)
                    {
                        d |= 1 << k;
                    }
                }

                sb.Append(Digits[d]);
            }
        }
        else
        {
            // Peel off chunks of digits: radix^chunk fits in a ulong.
            int chunk = 1;
            ulong chunkBase = (ulong)radix;
            while (chunkBase * (ulong)radix < (1UL << 62) / (ulong)radix)
            {
                chunkBase *= (ulong)radix;
                chunk++;
            }

            var bigBase = new BigInteger(chunkBase);
            while (!v.IsZero)
            {
                v = BigInteger.DivRem(v, bigBase, out BigInteger rem);
                ulong rv = (ulong)rem;
                for (int k = 0; k < chunk; k++)
                {
                    sb.Append(Digits[(int)(rv % (ulong)radix)]);
                    rv /= (ulong)radix;
                    if (v.IsZero && rv == 0)
                    {
                        break;
                    }
                }
            }
        }

        while (sb.Length > 1 && sb[^1] == '0')
        {
            sb.Length--;
        }

        if (b.Sign < 0)
        {
            sb.Append('-');
        }

        char[] arr = sb.ToString().ToCharArray();
        Array.Reverse(arr);
        return new string(arr);
    }

    /// <summary>ToBigInt (7.1.13), with V8's wording for values that cannot be converted.</summary>
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
            case JsSymbol sy:
                throw Errors.Type("Cannot convert " + sy + " to a BigInt");
            default:
                if (p.IsBoolean)
                {
                    return p.IsTrue ? BigInteger.One : BigInteger.Zero;
                }

                throw Errors.Type("Cannot convert " + Ops.DisplayValue(p) + " to a BigInt");
        }
    }
}
