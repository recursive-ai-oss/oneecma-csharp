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
        Realm.Value((JsObject)r.Intrinsics["%Number%"], "parseFloat", JsValue.FromObject(parseFloat));
        Realm.Value((JsObject)r.Intrinsics["%Number%"], "parseInt", JsValue.FromObject(parseInt));
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
        Realm.Value(h, "global", JsValue.FromObject(r.Global));
        r.Method(h, "gc", 0, (t, a, nt) => default);
        r.Method(h, "createRealm", 0, (t, a, nt) =>
        {
            Realm nr = Realm.Create(r.Agent);
            return nr.Global.Get("$262");
        });
        r.Method(h, "evalScript", 1, (t, a, nt) => ScriptRunner.RunScript(r, Ops.ToStr(A(a, 0)), "<evalScript>"));
        r.Method(h, "detachArrayBuffer", 1, (t, a, nt) => throw Errors.Type("detachArrayBuffer is not available in this build"));
        var agent = r.NewObject();
        Realm.Value(h, "agent", JsValue.FromObject(agent));
        var dda = new HtmlDdaObject(r.ObjectPrototype);
        Realm.Value(h, "IsHTMLDDA", JsValue.FromObject(dda));
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
