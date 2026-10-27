using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    private static void SetupMathJson(Realm r)
    {
        var math = r.NewObject();
        r.Global.DefineDirect("Math", JsValue.FromObject(math), PropFlags.Hidden);
        math.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Math"), PropFlags.Configurable);
        Realm.Value(math, "PI", Ops.Num(Math.PI), PropFlags.None);
        Realm.Value(math, "E", Ops.Num(Math.E), PropFlags.None);
        void M1(string n, Func<double, double> f) => r.Method(math, n, 1, (t, a, nt) => Ops.Num(f(Ops.ToNumber(A(a, 0)))));
        M1("floor", Math.Floor);
        M1("ceil", Math.Ceiling);
        M1("abs", Math.Abs);
        M1("sqrt", Math.Sqrt);
        M1("trunc", Math.Truncate);
        M1("sign", d => double.IsNaN(d) ? d : d == 0 ? d : Math.Sign(d));
        M1("round", d => double.IsFinite(d) ? (d == Math.Floor(d) ? d : Math.Floor(d + 0.5)) : d);
        r.Method(math, "pow", 2, (t, a, nt) => Ops.Num(Ops.Pow(Ops.ToNumber(A(a, 0)), Ops.ToNumber(A(a, 1)))));
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

        var json = r.NewObject();
        r.Global.DefineDirect("JSON", JsValue.FromObject(json), PropFlags.Hidden);
        json.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("JSON"), PropFlags.Configurable);
        r.Method(json, "stringify", 3, (t, a, nt) =>
        {
            string indent = A(a, 2).IsNumber ? new string(' ', (int)Math.Clamp(A(a, 2).Num, 0, 10)) : A(a, 2).IsString ? A(a, 2).AsString[..Math.Min(10, A(a, 2).AsString.Length)] : string.Empty;
            var sb = new StringBuilder();
            return JsonSerialize(JsValue.FromObject(Wrap(r, A(a, 0))), "", indent, sb, new List<JsObject>(), 0) ? Ops.Str(sb.ToString()) : default;
        });
    }

    private static JsObject Wrap(Realm r, JsValue v)
    {
        var holder = r.NewObject();
        holder.CreateDataProperty("", v);
        return holder;
    }

    private static string JsonQuote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.NumberFormatInfo.InvariantInfo));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static bool JsonSerialize(JsValue holderV, string key, string indent, StringBuilder sb, List<JsObject> stack, int depth)
    {
        JsValue v = ((JsObject)holderV.Ref!).Get(key);
        if (v.Ref is JsObject o)
        {
            JsValue tj = o.Get("toJSON");
            if (Ops.IsCallable(tj))
            {
                v = Ops.Call(tj, v, [Ops.Str(key)]);
            }
        }

        if (v.Ref is JsPrimitiveObject { ClassName: "Number" or "Boolean" or "BigInt" } pw)
        {
            v = pw.ClassName == "Number" ? Ops.Num(Ops.ToNumber(v)) : pw.Value;
        }
        else if (v.Ref is JsStringObject)
        {
            v = Ops.Str(Ops.ToStr(v));
        }

        switch (v.Ref)
        {
            case null:
            case JsSymbol:
                return false;
            case string or Rope:
                sb.Append(JsonQuote(v.AsString));
                return true;
            case System.Numerics.BigInteger:
                throw Errors.Type("Do not know how to serialize a BigInt");
            case JsObject obj when !obj.IsCallable:
                if (stack.Contains(obj))
                {
                    throw Errors.Type("Converting circular structure to JSON");
                }

                if (stack.Count > 2000)
                {
                    throw Errors.Range("Maximum call stack size exceeded");
                }

                stack.Add(obj);
                string inner = indent.Length == 0 ? string.Empty : "\n" + string.Concat(Enumerable.Repeat(indent, depth + 1));
                string outer = indent.Length == 0 ? string.Empty : "\n" + string.Concat(Enumerable.Repeat(indent, depth));
                if (Ops.IsArray(v))
                {
                    long len = Ops.LengthOfArrayLike(obj);
                    sb.Append('[');
                    for (long i = 0; i < len; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append(inner);
                        if (!JsonSerialize(v, IdxKey(i), indent, sb, stack, depth + 1))
                        {
                            sb.Append("null");
                        }
                    }

                    sb.Append(len > 0 ? outer : string.Empty).Append(']');
                }
                else
                {
                    sb.Append('{');
                    bool first = true;
                    var keys = new List<PropertyKey>();
                    obj.OwnKeys(keys);
                    foreach (PropertyKey k in keys)
                    {
                        if (k.IsSymbol || !(obj.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable))
                        {
                            continue;
                        }

                        int mark = sb.Length;
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        sb.Append(inner).Append(JsonQuote(k.String!)).Append(indent.Length == 0 ? ":" : ": ");
                        if (!JsonSerialize(v, k.String!, indent, sb, stack, depth + 1))
                        {
                            sb.Length = mark;
                        }
                        else
                        {
                            first = false;
                        }
                    }

                    sb.Append(first ? string.Empty : outer).Append('}');
                }

                stack.RemoveAt(stack.Count - 1);
                return true;
            case JsObject:
                return false;
            default:
                if (v.IsNumber)
                {
                    sb.Append(double.IsFinite(v.Num) ? Ops.NumberToStr(v.Num) : "null");
                }
                else
                {
                    sb.Append(v.IsNull ? "null" : v.IsTrue ? "true" : "false");
                }

                return true;
        }
    }
}
