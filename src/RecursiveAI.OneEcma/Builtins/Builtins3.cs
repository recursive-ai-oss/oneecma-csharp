using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>EnumerateObjectProperties (14.7.5.9): own enumerable string keys of the object, then of each prototype, skipping shadowed and deleted keys.</summary>
internal sealed class ForInState
{
    private JsObject? _current;
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private List<PropertyKey>? _keys;
    private int _pos;

    internal ForInState(JsValue v)
    {
        _current = v.IsNullish ? null : Ops.ToObject(v);
    }

    internal bool Next(out string? key)
    {
        while (_current is not null)
        {
            if (_keys is null)
            {
                _keys = [];
                _current.OwnKeys(_keys);
                _pos = 0;
            }

            while (_pos < _keys.Count)
            {
                PropertyKey k = _keys[_pos++];
                if (k.Ref is not string s || _visited.Contains(s))
                {
                    continue;
                }

                if (!_current.GetOwnProperty(k, out PropertyDescriptor d))
                {
                    continue;
                }

                _visited.Add(s);
                if (d.Enumerable)
                {
                    key = s;
                    return true;
                }
            }

            _current = _current.GetPrototypeOf();
            _keys = null;
        }

        key = null;
        return false;
    }
}

/// <summary>The seam where a RegExp implementation plugs in (RegExp literals compile to a call here).</summary>
internal static class RegExpHook
{
    internal static Func<Realm, string, string, JsValue>? Factory;

    internal static JsValue Create(Realm r, string pattern, string flags)
    {
        if (Factory is null)
        {
            throw Errors.Syntax("RegExp objects are not available in this build");
        }

        return Factory(r, pattern, flags);
    }
}

internal static partial class Builtins
{
    private static void SetupGlobals(Realm r)
    {
        JsObject g = r.Global;
        r.EvalFunction = r.Method(g, "eval", 1, (t, a, nt) =>
        {
            if (!A(a, 0).IsString)
            {
                return A(a, 0);
            }

            return PerformEval(r, A(a, 0).AsString);
        });
        r.Intrinsics["%eval%"] = r.EvalFunction;
        r.Method(g, "isNaN", 1, (t, a, nt) => Ops.Bool(double.IsNaN(Ops.ToNumber(A(a, 0)))));
        r.Method(g, "isFinite", 1, (t, a, nt) => Ops.Bool(double.IsFinite(Ops.ToNumber(A(a, 0)))));
        NativeFunction parseFloat = r.Method(g, "parseFloat", 1, (t, a, nt) =>
        {
            string s = NumberConv.TrimJs(Ops.ToStr(A(a, 0)));
            int i = 0;
            if (i < s.Length && s[i] is '+' or '-')
            {
                i++;
            }

            if (string.CompareOrdinal(s, i, "Infinity", 0, 8) == 0)
            {
                return Ops.Num(s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity);
            }

            int start = i;
            bool digits = false;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
                digits = true;
            }

            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                    digits = true;
                }
            }

            if (!digits)
            {
                return Ops.Num(double.NaN);
            }

            if (i < s.Length && s[i] is 'e' or 'E')
            {
                int save = i++;
                if (i < s.Length && s[i] is '+' or '-')
                {
                    i++;
                }

                if (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    while (i < s.Length && char.IsAsciiDigit(s[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    i = save;
                }
            }

            _ = start;
            return Ops.Num(NumberConv.StringToNumber(s[..i]));
        });
        NativeFunction parseInt = r.Method(g, "parseInt", 2, (t, a, nt) =>
        {
            string s = NumberConv.TrimJs(Ops.ToStr(A(a, 0)));
            int radix = Ops.ToInt32(A(a, 1));
            int i = 0;
            bool neg = false;
            if (i < s.Length && s[i] is '+' or '-')
            {
                neg = s[i] == '-';
                i++;
            }

            bool strip = true;
            if (radix != 0)
            {
                if (radix < 2 || radix > 36)
                {
                    return Ops.Num(double.NaN);
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

            if (strip && i + 1 < s.Length && s[i] == '0' && s[i + 1] is 'x' or 'X')
            {
                i += 2;
                radix = 16;
            }

            double v = 0;
            int n = 0;
            for (; i < s.Length; i++, n++)
            {
                int d = NumberConv.DigitValue(s[i]);
                if (d < 0 || d >= radix)
                {
                    break;
                }

                v = (v * radix) + d;
            }

            return n == 0 ? Ops.Num(double.NaN) : Ops.Num(neg ? -v : v);
        });
        r.Value((JsObject)r.Intrinsics["%Number%"], "parseFloat", JsValue.FromObject(parseFloat));
        r.Value((JsObject)r.Intrinsics["%Number%"], "parseInt", JsValue.FromObject(parseInt));
    }

    /// <summary>PerformEval for indirect eval (19.2.1.1): global scope, sloppy unless the code says otherwise.</summary>
    internal static JsValue PerformEval(Realm r, string src)
    {
        Syntax.ParseResult pr = Syntax.JsParser.Parse(src, new Syntax.ParseOptions { Goal = Syntax.ParseGoal.Eval, FileName = "<eval>" });
        if (pr.HasErrors)
        {
            throw Errors.Syntax(pr.Diagnostics[0].Headline);
        }

        FunctionCode code = Compiler.CompileEval(pr.Program, pr.Source, false);
        var f = new Frame(code, r) { Env = null, This = JsValue.FromObject(r.Global) };
        return Vm.RunEntry(r.Agent, f);
    }

    private static void SetupMathJson(Realm r)
    {
        var math = r.NewObject();
        r.Global.DefineDirect("Math", JsValue.FromObject(math), PropFlags.Hidden);
        math.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Math"), PropFlags.Configurable);
        r.Value(math, "PI", Ops.Num(Math.PI), PropFlags.None);
        r.Value(math, "E", Ops.Num(Math.E), PropFlags.None);
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

    // ---- host-defined: print and $262 -------------------------------------------------------------------------------------

    private static void SetupHost(Realm r)
    {
        if (!r.Agent.InstallTest262Host)
        {
            return;
        }

        r.Method(r.Global, "print", 1, (t, a, nt) =>
        {
            r.Agent.Host.Print(a.Length == 0 ? string.Empty : Ops.ToStr(a[0]));
            return default;
        });
        JsObject h = r.NewObject();
        r.Global.DefineDirect("$262", JsValue.FromObject(h), PropFlags.Hidden);
        r.Value(h, "global", JsValue.FromObject(r.Global));
        r.Method(h, "gc", 0, (t, a, nt) => default);
        r.Method(h, "createRealm", 0, (t, a, nt) =>
        {
            Realm nr = Realm.Create(r.Agent);
            return nr.Global.Get("$262");
        });
        r.Method(h, "evalScript", 1, (t, a, nt) => ScriptRunner.RunScript(r, Ops.ToStr(A(a, 0)), "<evalScript>"));
        r.Method(h, "detachArrayBuffer", 1, (t, a, nt) => throw Errors.Type("detachArrayBuffer is not available in this build"));
        var agent = r.NewObject();
        r.Value(h, "agent", JsValue.FromObject(agent));
        var dda = new HtmlDdaObject(r.ObjectPrototype);
        r.Value(h, "IsHTMLDDA", JsValue.FromObject(dda));
    }
}

/// <summary>The [[IsHTMLDDA]] test object (B.3.6): typeof is "undefined", falsy, loosely equal to null, callable returning null.</summary>
internal sealed class HtmlDdaObject : JsObject
{
    internal HtmlDdaObject(JsObject proto)
        : base(proto)
    {
    }

    internal override bool IsHtmlDda => true;

    internal override bool IsCallable => true;

    internal override string TypeOfName => "undefined";

    internal override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => JsValue.Null;
}
