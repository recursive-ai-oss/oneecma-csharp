using System.Globalization;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma.ExecutorSpike;

/// <summary>
/// Intrinsics, global object and the resource counters both executors charge: fuel on loop back-edges
/// and calls, and the JavaScript call depth.
/// </summary>
internal sealed class Realm
{
    internal long Fuel;
    internal int Depth;
    internal int MaxDepth;

    /// <summary>Argument stack shared by call sites of the closure executor and by native calls.</summary>
    internal JsValue[] ArgStack = new JsValue[128];
    internal int Sp;

    /// <summary>Pooled activations of the closure executor, one per call depth.</summary>
    internal Closure.Frame?[] Frames;

    internal Realm(long fuel = long.MaxValue / 2, int maxDepth = 10_000)
    {
        Fuel = fuel;
        MaxDepth = maxDepth;
        Frames = new Closure.Frame?[Math.Min(maxDepth + 2, 64)];
        ObjectProto = new JsObject(null);
        FunctionProto = new JsObject(ObjectProto);
        ArrayProto = new JsObject(ObjectProto);
        StringProto = new JsObject(ObjectProto);
        ErrorProto = new JsObject(ObjectProto);
        Global = new JsObject(ObjectProto);
        ArrayRoot = Shape.RootFor(ArrayProto);
        InstallBuiltins();
    }

    internal JsObject ObjectProto { get; }

    internal JsObject FunctionProto { get; }

    internal JsObject ArrayProto { get; }

    internal JsObject StringProto { get; }

    internal JsObject ErrorProto { get; }

    internal JsObject Global { get; }

    internal Shape ArrayRoot { get; }

    internal JsObject NewObject() => new(ObjectProto);

    internal JsArray NewArray(int capacity = 0) => new(ArrayProto, capacity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Exception OutOfFuel() => new SpikeLimitException("fuel exhausted");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Exception TooDeep() => new SpikeLimitException("maximum call depth exceeded");

    internal JsThrow MakeError(string name, string message)
    {
        var e = new JsObject(ErrorProto);
        e.AddProperty("name", JsValue.FromString(name));
        e.AddProperty("message", JsValue.FromString(message));
        return new JsThrow(JsValue.FromObject(e));
    }

    internal JsThrow TypeError(string message) => MakeError("TypeError", message);

    internal JsThrow ReferenceError(string message) => MakeError("ReferenceError", message);

    internal JsThrow NotCallable(string calleeText) => TypeError(calleeText + " is not a function");

    private void Def(JsObject target, string name, NativeFn fn) =>
        target.AddProperty(name, JsValue.FromObject(new NativeFunction(this, name, fn)));

    private static double Arg(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? Conv.ToNumber(a[i]) : double.NaN;

    private static string ThisStr(JsValue t) => Conv.ToStr(t);

    private void InstallBuiltins()
    {
        Global.AddProperty("undefined", default);
        Global.AddProperty("NaN", JsValue.FromNumber(double.NaN));
        Global.AddProperty("Infinity", JsValue.FromNumber(double.PositiveInfinity));
        Global.AddProperty("globalThis", JsValue.FromObject(Global));

        var math = NewObject();
        Def(math, "floor", (_, a) => JsValue.FromNumber(Math.Floor(Arg(a, 0))));
        Def(math, "sqrt", (_, a) => JsValue.FromNumber(Math.Sqrt(Arg(a, 0))));
        Def(math, "abs", (_, a) => JsValue.FromNumber(Math.Abs(Arg(a, 0))));
        Def(math, "max", (_, a) =>
        {
            double r = double.NegativeInfinity;
            for (int i = 0; i < a.Length; i++)
            {
                double d = Conv.ToNumber(a[i]);
                r = double.IsNaN(d) || double.IsNaN(r) ? double.NaN : Math.Max(r, d);
            }

            return JsValue.FromNumber(r);
        });
        Def(math, "min", (_, a) =>
        {
            double r = double.PositiveInfinity;
            for (int i = 0; i < a.Length; i++)
            {
                double d = Conv.ToNumber(a[i]);
                r = double.IsNaN(d) || double.IsNaN(r) ? double.NaN : Math.Min(r, d);
            }

            return JsValue.FromNumber(r);
        });
        Def(math, "imul", (_, a) => JsValue.FromNumber(unchecked(Conv.ToInt32(Arg(a, 0)) * Conv.ToInt32(Arg(a, 1)))));
        Global.AddProperty("Math", JsValue.FromObject(math));

        var str = NewObject();
        Def(str, "fromCharCode", (_, a) =>
        {
            var chars = new char[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                chars[i] = (char)(ushort)Conv.ToInt32(Conv.ToNumber(a[i]));
            }

            return JsValue.FromString(new string(chars));
        });
        Global.AddProperty("String", JsValue.FromObject(str));

        Def(StringProto, "charCodeAt", (t, a) =>
        {
            string s = ThisStr(t);
            double d = a.Length > 0 ? Conv.ToNumber(a[0]) : 0;
            int i = double.IsNaN(d) ? 0 : (int)d;
            return JsValue.FromNumber((uint)i < (uint)s.Length ? s[i] : double.NaN);
        });
        Def(StringProto, "indexOf", (t, a) =>
            JsValue.FromNumber(ThisStr(t).IndexOf(a.Length > 0 ? Conv.ToStr(a[0]) : "undefined", StringComparison.Ordinal)));
        Def(StringProto, "substring", (t, a) =>
        {
            string s = ThisStr(t);
            int from = (int)Math.Clamp(Arg(a, 0) is var f && double.IsNaN(f) ? 0 : f, 0, s.Length);
            int to = a.Length > 1 ? (int)Math.Clamp(double.IsNaN(Arg(a, 1)) ? 0 : Arg(a, 1), 0, s.Length) : s.Length;
            return JsValue.FromString(s.Substring(Math.Min(from, to), Math.Abs(to - from)));
        });

        Def(ArrayProto, "push", (t, a) =>
        {
            var arr = (JsArray)t.Ref!;
            for (int i = 0; i < a.Length; i++)
            {
                arr.Push(a[i]);
            }

            return JsValue.FromNumber(arr.Length);
        });
        Def(ArrayProto, "pop", (t, _) =>
        {
            var arr = (JsArray)t.Ref!;
            if (arr.Length == 0)
            {
                return default;
            }

            JsValue v = arr.Items[--arr.Length];
            arr.Items[arr.Length] = default;
            return v;
        });
        Def(ArrayProto, "join", (t, a) =>
        {
            var arr = (JsArray)t.Ref!;
            string sep = a.Length > 0 && !a[0].IsUndefined ? Conv.ToStr(a[0]) : ",";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }

                if (!arr.Items[i].IsNullish)
                {
                    sb.Append(Conv.ToStr(arr.Items[i]));
                }
            }

            return JsValue.FromString(sb.ToString());
        });

        ErrorProto.AddProperty("name", JsValue.FromString("Error"));
        var error = new NativeFunction(this, "Error", (_, a) =>
        {
            var e = new JsObject(ErrorProto);
            e.AddProperty("message", a.Length > 0 ? JsValue.FromString(Conv.ToStr(a[0])) : JsValue.FromString(string.Empty));
            return JsValue.FromObject(e);
        });
        Global.AddProperty("Error", JsValue.FromObject(error));
    }
}

/// <summary>Operators and property access shared by both executors.</summary>
internal static class Ops
{
    internal static JsValue Add(JsValue a, JsValue b)
    {
        if (a.IsNumber && b.IsNumber)
        {
            return JsValue.FromNumber(a.Num + b.Num);
        }

        return AddSlow(a, b);
    }

    private static JsValue AddSlow(JsValue a, JsValue b)
    {
        if (a.IsString || b.IsString || a.IsObject || b.IsObject)
        {
            return Concat(a, b);
        }

        return JsValue.FromNumber(Conv.ToNumber(a) + Conv.ToNumber(b));
    }

    internal static JsValue Concat(JsValue a, JsValue b)
    {
        object l = a.Ref is string or Rope ? a.Ref : Conv.ToStr(a);
        object r = b.Ref is string or Rope ? b.Ref : Conv.ToStr(b);
        int ll = l is string ls ? ls.Length : ((Rope)l).Length;
        int rl = r is string rs ? rs.Length : ((Rope)r).Length;
        if (ll == 0)
        {
            return new JsValue(r, 0);
        }

        if (rl == 0)
        {
            return new JsValue(l, 0);
        }

        long total = (long)ll + rl;
        if (total > 512 * 1024 * 1024)
        {
            throw new SpikeLimitException("string too long");
        }

        if (total < 64 && l is string a1 && r is string b1)
        {
            return new JsValue(string.Concat(a1, b1), 0);
        }

        return new JsValue(new Rope(l, r, (int)total), 0);
    }

    internal static bool IsLess(JsValue a, JsValue b, out bool undefinedResult)
    {
        undefinedResult = false;
        if (a.IsString && b.IsString)
        {
            return string.CompareOrdinal(Conv.ToStr(a), Conv.ToStr(b)) < 0;
        }

        double x = Conv.ToNumber(a);
        double y = Conv.ToNumber(b);
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            undefinedResult = true;
            return false;
        }

        return x < y;
    }

    internal static bool LessThan(JsValue a, JsValue b) => a.IsNumber && b.IsNumber ? a.Num < b.Num : IsLess(a, b, out _);

    internal static bool GreaterThan(JsValue a, JsValue b) => a.IsNumber && b.IsNumber ? a.Num > b.Num : IsLess(b, a, out _);

    internal static bool LessEq(JsValue a, JsValue b)
    {
        if (a.IsNumber && b.IsNumber)
        {
            return a.Num <= b.Num;
        }

        bool r = IsLess(b, a, out bool undef);
        return !r && !undef;
    }

    internal static bool GreaterEq(JsValue a, JsValue b)
    {
        if (a.IsNumber && b.IsNumber)
        {
            return a.Num >= b.Num;
        }

        bool r = IsLess(a, b, out bool undef);
        return !r && !undef;
    }

    internal static bool StrictEquals(JsValue a, JsValue b)
    {
        if (a.IsNumber)
        {
            return b.IsNumber && a.Num == b.Num;
        }

        if (a.IsString)
        {
            return b.IsString && (ReferenceEquals(a.Ref, b.Ref) || Conv.ToStr(a) == Conv.ToStr(b));
        }

        return ReferenceEquals(a.Ref, b.Ref);
    }

    internal static bool LooseEquals(JsValue a, JsValue b)
    {
        if (a.IsNullish || b.IsNullish)
        {
            return a.IsNullish && b.IsNullish;
        }

        if (a.IsObject && b.IsObject)
        {
            return ReferenceEquals(a.Ref, b.Ref);
        }

        if (a.IsString && b.IsString)
        {
            return StrictEquals(a, b);
        }

        if (a.IsObject || b.IsObject)
        {
            return false;
        }

        return Conv.ToNumber(a) == Conv.ToNumber(b);
    }

    internal static JsValue Exponent(double a, double b)
    {
        if (double.IsNaN(b) || (Math.Abs(a) == 1 && double.IsInfinity(b)))
        {
            return JsValue.FromNumber(double.NaN);
        }

        return JsValue.FromNumber(Math.Pow(a, b));
    }

    // ---- property access ------------------------------------------------------------------

    internal static JsValue GetMember(Realm realm, JsValue target, string key)
    {
        object? r = target.Ref;
        if (r is JsObject o)
        {
            if (r is JsFunction f && key == "prototype")
            {
                f.EnsurePrototype(realm);
            }

            return o.Get(key);
        }

        if (r is string or Rope)
        {
            if (key == "length")
            {
                return JsValue.FromNumber(r is string s ? s.Length : ((Rope)r).Length);
            }

            return realm.StringProto.Get(key);
        }

        if (target.IsNullish)
        {
            throw realm.TypeError($"Cannot read properties of {(target.IsNull ? "null" : "undefined")} (reading '{key}')");
        }

        return default;
    }

    internal static void SetMember(Realm realm, JsValue target, string key, JsValue value)
    {
        if (target.Ref is JsObject o)
        {
            if (o is JsFunction f && key == "prototype")
            {
                f.EnsurePrototype(realm);
            }

            o.Set(key, value);
            return;
        }

        if (target.IsNullish)
        {
            throw realm.TypeError($"Cannot set properties of {(target.IsNull ? "null" : "undefined")} (setting '{key}')");
        }
    }

    internal static JsValue GetIndex(Realm realm, JsValue target, JsValue index)
    {
        if (target.Ref is JsArray a && index.IsNumber)
        {
            double d = index.Num;
            int i = (int)d;
            if (i == d && (uint)i < (uint)a.Length)
            {
                return a.Items[i];
            }
        }
        else if (target.Ref is string s && index.IsNumber)
        {
            double d = index.Num;
            int i = (int)d;
            if (i == d && (uint)i < (uint)s.Length)
            {
                return JsValue.FromString(s[i].ToString());
            }
        }

        return GetMember(realm, target, Conv.ToStr(index));
    }

    internal static void SetIndex(Realm realm, JsValue target, JsValue index, JsValue value)
    {
        if (target.Ref is JsArray a && index.IsNumber)
        {
            double d = index.Num;
            int i = (int)d;
            if (i == d && i >= 0)
            {
                if (i < a.Length)
                {
                    a.Items[i] = value;
                    return;
                }

                if (i < a.Length + 1_000_000)
                {
                    a.SetLength(i + 1);
                    a.Items[i] = value;
                    return;
                }
            }
        }

        SetMember(realm, target, Conv.ToStr(index), value);
    }

    internal static bool HasProperty(JsValue target, JsValue key)
    {
        if (target.Ref is not JsObject o)
        {
            return false;
        }

        string k = Conv.ToStr(key);
        if (o is JsArray a && int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
        {
            return idx < a.Length;
        }

        for (JsObject? p = o; p is not null; p = p.Proto)
        {
            if (p.TryGetOwn(k, out _))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool InstanceOf(JsValue v, JsValue ctor)
    {
        if (ctor.Ref is not JsFunction f)
        {
            return false;
        }

        if (v.Ref is not JsObject o)
        {
            return false;
        }

        JsObject? proto = f.PrototypeProperty;
        for (JsObject? p = o.Proto; p is not null; p = p.Proto)
        {
            if (ReferenceEquals(p, proto))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Evaluates a binary operator on already evaluated operands.</summary>
    internal static JsValue Binary(Syntax.BinaryOp op, JsValue a, JsValue b)
    {
        switch (op)
        {
            case Syntax.BinaryOp.Add: return Add(a, b);
            case Syntax.BinaryOp.Subtract: return JsValue.FromNumber(Conv.ToNumber(a) - Conv.ToNumber(b));
            case Syntax.BinaryOp.Multiply: return JsValue.FromNumber(Conv.ToNumber(a) * Conv.ToNumber(b));
            case Syntax.BinaryOp.Divide: return JsValue.FromNumber(Conv.ToNumber(a) / Conv.ToNumber(b));
            case Syntax.BinaryOp.Remainder: return JsValue.FromNumber(Conv.ToNumber(a) % Conv.ToNumber(b));
            case Syntax.BinaryOp.Exponent: return Exponent(Conv.ToNumber(a), Conv.ToNumber(b));
            case Syntax.BinaryOp.Equal: return JsValue.FromBool(LooseEquals(a, b));
            case Syntax.BinaryOp.NotEqual: return JsValue.FromBool(!LooseEquals(a, b));
            case Syntax.BinaryOp.StrictEqual: return JsValue.FromBool(StrictEquals(a, b));
            case Syntax.BinaryOp.StrictNotEqual: return JsValue.FromBool(!StrictEquals(a, b));
            case Syntax.BinaryOp.LessThan: return JsValue.FromBool(LessThan(a, b));
            case Syntax.BinaryOp.LessThanOrEqual: return JsValue.FromBool(LessEq(a, b));
            case Syntax.BinaryOp.GreaterThan: return JsValue.FromBool(GreaterThan(a, b));
            case Syntax.BinaryOp.GreaterThanOrEqual: return JsValue.FromBool(GreaterEq(a, b));
            case Syntax.BinaryOp.ShiftLeft: return JsValue.FromNumber(Conv.ToInt32(Conv.ToNumber(a)) << (Conv.ToInt32(Conv.ToNumber(b)) & 31));
            case Syntax.BinaryOp.ShiftRight: return JsValue.FromNumber(Conv.ToInt32(Conv.ToNumber(a)) >> (Conv.ToInt32(Conv.ToNumber(b)) & 31));
            case Syntax.BinaryOp.UnsignedShiftRight: return JsValue.FromNumber((uint)Conv.ToInt32(Conv.ToNumber(a)) >> (Conv.ToInt32(Conv.ToNumber(b)) & 31));
            case Syntax.BinaryOp.BitwiseAnd: return JsValue.FromNumber(Conv.ToInt32(Conv.ToNumber(a)) & Conv.ToInt32(Conv.ToNumber(b)));
            case Syntax.BinaryOp.BitwiseOr: return JsValue.FromNumber(Conv.ToInt32(Conv.ToNumber(a)) | Conv.ToInt32(Conv.ToNumber(b)));
            case Syntax.BinaryOp.BitwiseXor: return JsValue.FromNumber(Conv.ToInt32(Conv.ToNumber(a)) ^ Conv.ToInt32(Conv.ToNumber(b)));
            case Syntax.BinaryOp.In: return JsValue.FromBool(HasProperty(b, a));
            case Syntax.BinaryOp.Instanceof: return JsValue.FromBool(InstanceOf(a, b));
            default: throw new SpikeUnsupportedException("binary operator " + op);
        }
    }

    internal static JsValue Unary(Syntax.UnaryOp op, JsValue v)
    {
        switch (op)
        {
            case Syntax.UnaryOp.Minus: return JsValue.FromNumber(-Conv.ToNumber(v));
            case Syntax.UnaryOp.Plus: return JsValue.FromNumber(Conv.ToNumber(v));
            case Syntax.UnaryOp.Not: return JsValue.FromBool(!Conv.ToBoolean(v));
            case Syntax.UnaryOp.BitwiseNot: return JsValue.FromNumber(~Conv.ToInt32(Conv.ToNumber(v)));
            case Syntax.UnaryOp.Typeof: return JsValue.FromString(Conv.TypeOf(v));
            case Syntax.UnaryOp.Void: return default;
            default: throw new SpikeUnsupportedException("unary operator " + op);
        }
    }
}
