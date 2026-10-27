using System.Numerics;

namespace RecursiveAI.OneEcma;

/// <summary>The Math object (ECMA-262 21.3).</summary>
internal static partial class Builtins
{
    private static void SetupMath(Realm r)
    {
        var math = r.NewObject();
        r.Global.DefineDirect("Math", JsValue.FromObject(math), PropFlags.Hidden);
        math.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Math"), PropFlags.Configurable);
        Realm.Value(math, "E", Ops.Num(2.718281828459045), PropFlags.None);
        Realm.Value(math, "LN10", Ops.Num(2.302585092994046), PropFlags.None);
        Realm.Value(math, "LN2", Ops.Num(0.6931471805599453), PropFlags.None);
        Realm.Value(math, "LOG10E", Ops.Num(0.4342944819032518), PropFlags.None);
        Realm.Value(math, "LOG2E", Ops.Num(1.4426950408889634), PropFlags.None);
        Realm.Value(math, "PI", Ops.Num(3.141592653589793), PropFlags.None);
        Realm.Value(math, "SQRT1_2", Ops.Num(0.7071067811865476), PropFlags.None);
        Realm.Value(math, "SQRT2", Ops.Num(1.4142135623730951), PropFlags.None);

        void M1(string n, Func<double, double> f) => r.Method(math, n, 1, (t, a, nt) => Ops.Num(f(Ops.ToNumber(A(a, 0)))));
        M1("abs", Math.Abs);
        M1("acos", Math.Acos);
        M1("acosh", Math.Acosh);
        M1("asin", Math.Asin);
        M1("asinh", d => d == 0 ? d : Math.Asinh(d));
        M1("atan", Math.Atan);
        M1("atanh", d => d == 0 ? d : Math.Atanh(d));
        M1("cbrt", Cbrt);
        M1("ceil", Math.Ceiling);
        M1("cos", Math.Cos);
        M1("cosh", Math.Cosh);
        M1("exp", Math.Exp);
        M1("expm1", Expm1);
        M1("floor", Math.Floor);
        M1("fround", d => (double)(float)d);
        M1("f16round", Float16Round);
        M1("log", Math.Log);
        M1("log1p", Log1p);
        M1("log10", Math.Log10);
        M1("log2", Math.Log2);
        M1("round", MathRound);
        M1("sign", d => double.IsNaN(d) || d == 0 ? d : Math.Sign(d));
        M1("sin", Math.Sin);
        M1("sinh", Math.Sinh);
        M1("sqrt", Math.Sqrt);
        M1("tan", Math.Tan);
        M1("tanh", Math.Tanh);
        M1("trunc", Math.Truncate);
        r.Method(math, "clz32", 1, (t, a, nt) => Ops.Num(BitOperations.LeadingZeroCount(Ops.ToUint32(A(a, 0)))));
        r.Method(math, "atan2", 2, (t, a, nt) =>
        {
            double y = Ops.ToNumber(A(a, 0));
            double x = Ops.ToNumber(A(a, 1));
            return Ops.Num(Math.Atan2(y, x));
        });
        r.Method(math, "imul", 2, (t, a, nt) =>
        {
            uint x = Ops.ToUint32(A(a, 0));
            uint y = Ops.ToUint32(A(a, 1));
            return Ops.Num(unchecked((int)(x * y)));
        });
        r.Method(math, "pow", 2, (t, a, nt) =>
        {
            double x = Ops.ToNumber(A(a, 0));
            double y = Ops.ToNumber(A(a, 1));
            return Ops.Num(Ops.Pow(x, y));
        });
        r.Method(math, "random", 0, (t, a, nt) => Ops.Num(Agent.Current.Host.NextRandom()));
        r.Method(math, "max", 2, (t, a, nt) =>
        {
            double m = double.NegativeInfinity;
            foreach (JsValue v in a)
            {
                double d = Ops.ToNumber(v);
                m = double.IsNaN(d) || double.IsNaN(m) ? double.NaN : (d > m || (d == 0 && m == 0 && !double.IsNegative(d)) ? d : m);
            }

            return Ops.Num(m);
        });
        r.Method(math, "min", 2, (t, a, nt) =>
        {
            double m = double.PositiveInfinity;
            foreach (JsValue v in a)
            {
                double d = Ops.ToNumber(v);
                m = double.IsNaN(d) || double.IsNaN(m) ? double.NaN : (d < m || (d == 0 && m == 0 && double.IsNegative(d)) ? d : m);
            }

            return Ops.Num(m);
        });
        r.Method(math, "hypot", 2, (t, a, nt) =>
        {
            // 21.3.2.18: coerce every argument first, then infinity wins over NaN.
            var nums = new double[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                nums[i] = Ops.ToNumber(a[i]);
            }

            bool nan = false;
            double max = 0;
            foreach (double d in nums)
            {
                if (double.IsInfinity(d))
                {
                    return Ops.Num(double.PositiveInfinity);
                }

                nan |= double.IsNaN(d);
                max = Math.Max(max, Math.Abs(d));
            }

            if (nan)
            {
                return Ops.Num(double.NaN);
            }

            if (max == 0)
            {
                return Ops.Num(0);
            }

            // Scale by the largest magnitude and sum with compensation so neither overflow nor underflow loses the result.
            double sum = 0;
            double comp = 0;
            foreach (double d in nums)
            {
                double s = d / max;
                double term = (s * s) - comp;
                double next = sum + term;
                comp = (next - sum) - term;
                sum = next;
            }

            return Ops.Num(Math.Sqrt(sum) * max);
        });
        r.Method(math, "sumPrecise", 1, (t, a, nt) => SumPrecise(A(a, 0)));
    }

    /// <summary>Math.round (21.3.2.28): round half toward +Infinity, keeping -0 for results in [-0.5, 0).</summary>
    private static double MathRound(double x)
    {
        if (!double.IsFinite(x) || x == 0 || Math.Abs(x) >= 4503599627370496d)
        {
            return x;
        }

        double fl = Math.Floor(x);
        double res = x - fl >= 0.5 ? fl + 1 : fl;
        return res == 0 && x < 0 ? -0d : res;
    }

    private static double Cbrt(double x)
    {
        double y = Math.Cbrt(x);
        double rounded = Math.Round(y);
        return rounded * rounded * rounded == x ? rounded : y; // exact cubes stay exact
    }

    private static double Expm1(double x)
    {
        if (double.IsNaN(x) || x == 0)
        {
            return x;
        }

        if (Math.Abs(x) < 1e-5)
        {
            return x + (x * x / 2) + (x * x * x / 6);
        }

        double u = Math.Exp(x);
        if (double.IsPositiveInfinity(u))
        {
            return u;
        }

        double um1 = u - 1;
        if (um1 == -1)
        {
            return -1;
        }

        return um1 == 0 ? x : um1 * x / Math.Log(u);
    }

    private static double Log1p(double x)
    {
        if (double.IsNaN(x) || x == 0 || double.IsPositiveInfinity(x))
        {
            return x;
        }

        if (x < -1)
        {
            return double.NaN;
        }

        if (x == -1)
        {
            return double.NegativeInfinity;
        }

        double u = 1 + x;
        return u == 1 ? x : Math.Log(u) * x / (u - 1);
    }

    /// <summary>Math.f16round: the nearest IEEE 754 binary16 value (ties to even) as a double.</summary>
    private static double Float16Round(double x)
    {
        if (!double.IsFinite(x) || x == 0)
        {
            return x;
        }

        double ax = Math.Abs(x);
        double result;
        if (ax >= 65520d)
        {
            result = double.PositiveInfinity; // 65520 is the midpoint between 65504 and the next (unrepresentable) step
        }
        else if (ax < 6.103515625e-5)
        {
            result = Math.Round(ax * 16777216d, MidpointRounding.ToEven) / 16777216d; // subnormals: multiples of 2^-24
        }
        else
        {
            int e = Math.ILogB(ax);
            double ulp = Math.ScaleB(1d, e - 10);
            result = Math.Round(ax / ulp, MidpointRounding.ToEven) * ulp;
        }

        return x < 0 ? -result : result;
    }

    /// <summary>Math.sumPrecise: the exact sum of the numbers in an iterable, rounded once.</summary>
    private static JsValue SumPrecise(JsValue items)
    {
        Ops.RequireObjectCoercible(items);
        IteratorRecord rec = Ops.GetIterator(items);
        // States: 0 minus-zero, 1 finite, 2 +Infinity, 3 -Infinity, 4 NaN.
        int state = 0;
        BigInteger sum = BigInteger.Zero; // in units of 2^-1074
        long count = 0;
        while (Ops.IteratorStepValue(rec, out JsValue next))
        {
            Agent.Current.Tick();
            if (count >= 9007199254740991L)
            {
                var err = Errors.Range("Math.sumPrecise: too many values");
                Ops.IteratorCloseSilently(rec);
                throw err;
            }

            if (!next.IsNumber)
            {
                var err = Errors.Type(Ops.DisplayValue(next) + " is not a number");
                Ops.IteratorCloseSilently(rec);
                throw err;
            }

            count++;
            double n = next.Num;
            if (state == 4)
            {
                continue;
            }

            if (double.IsNaN(n))
            {
                state = 4;
            }
            else if (double.IsPositiveInfinity(n))
            {
                state = state == 3 ? 4 : 2;
            }
            else if (double.IsNegativeInfinity(n))
            {
                state = state == 2 ? 4 : 3;
            }
            else if (state is 0 or 1 && !(n == 0 && double.IsNegative(n)))
            {
                state = 1;
                if (n != 0)
                {
                    long bits = BitConverter.DoubleToInt64Bits(n);
                    int exp = (int)((bits >> 52) & 0x7FF);
                    long frac = bits & 0xFFFFFFFFFFFFFL;
                    long m = exp == 0 ? frac : frac | (1L << 52);
                    int shift = exp == 0 ? 0 : exp - 1;
                    BigInteger term = new BigInteger(m) << shift;
                    sum += n < 0 ? -term : term;
                }
            }
        }

        switch (state)
        {
            case 2:
                return Ops.Num(double.PositiveInfinity);
            case 3:
                return Ops.Num(double.NegativeInfinity);
            case 4:
                return Ops.Num(double.NaN);
            case 0:
                return Ops.Num(-0d);
            default:
                return Ops.Num(sum.IsZero ? 0 : ScaledBigToDouble(sum, -1074));
        }
    }
}
