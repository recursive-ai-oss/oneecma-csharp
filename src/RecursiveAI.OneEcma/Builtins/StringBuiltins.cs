using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    private static string ThisStr(JsValue t)
    {
        Ops.RequireObjectCoercible(t);
        return Ops.ToStr(t);
    }

    private static void SetupString(Realm r)
    {
        var proto = new JsStringObject(r.ObjectPrototype, string.Empty);
        r.StringPrototype = proto;
        NativeFunction ctor = r.Ctor("String", 1, (t, a, nt) =>
        {
            string s = a.Length == 0 ? string.Empty : nt is null && a[0].Ref is JsSymbol sy ? sy.ToString() : Ops.ToStr(a[0]);
            if (nt is null)
            {
                return Ops.Str(s);
            }

            return JsValue.FromObject(new JsStringObject(Ops.GetPrototypeFromConstructor(nt, x => x.StringPrototype), s));
        }, proto);
        r.Method(ctor, "fromCharCode", 1, (t, a, nt) =>
        {
            var sb = new StringBuilder();
            foreach (JsValue v in a)
            {
                sb.Append((char)(ushort)Ops.ToUint32(v));
            }

            return Ops.Str(sb.ToString());
        });
        string ThisS(JsValue t) => t.IsString ? t.AsString : t.Ref is JsStringObject so ? so.Value : throw Errors.Type("String.prototype.valueOf requires that 'this' be a String");
        r.Method(proto, "toString", 0, (t, a, nt) => Ops.Str(ThisS(t)));
        r.Method(proto, "valueOf", 0, (t, a, nt) => Ops.Str(ThisS(t)));
        r.Method(proto, "charAt", 1, (t, a, nt) =>
        {
            string s = ThisStr(t);
            double p = Ops.ToIntegerOrInfinity(A(a, 0));
            return Ops.Str(p >= 0 && p < s.Length ? s[(int)p].ToString() : string.Empty);
        });
        r.Method(proto, "charCodeAt", 1, (t, a, nt) =>
        {
            string s = ThisStr(t);
            double p = Ops.ToIntegerOrInfinity(A(a, 0));
            return Ops.Num(p >= 0 && p < s.Length ? s[(int)p] : double.NaN);
        });
        r.Method(proto, "indexOf", 1, (t, a, nt) =>
        {
            string s = ThisStr(t);
            string search = Ops.ToStr(A(a, 0));
            double p = Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            return Ops.Num(s.IndexOf(search, (int)p, StringComparison.Ordinal));
        });
        r.Method(proto, "slice", 2, (t, a, nt) =>
        {
            string s = ThisStr(t);
            long from = RelIndex(A(a, 0), s.Length, 0);
            long to = RelIndex(A(a, 1), s.Length, s.Length);
            return Ops.Str(from < to ? s.Substring((int)from, (int)(to - from)) : string.Empty);
        });
        r.Method(proto, "substring", 2, (t, a, nt) =>
        {
            string s = ThisStr(t);
            int from = (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 0)), 0, s.Length);
            int to = A(a, 1).IsUndefined ? s.Length : (int)Math.Clamp(Ops.ToIntegerOrInfinity(A(a, 1)), 0, s.Length);
            return Ops.Str(s.Substring(Math.Min(from, to), Math.Abs(to - from)));
        });
        r.Method(proto, "split", 2, (t, a, nt) =>
        {
            string s = ThisStr(t);
            var res = new JsArray(r.ArrayPrototype);
            if (A(a, 0).IsUndefined)
            {
                res.Push(Ops.Str(s));
                return JsValue.FromObject(res);
            }

            string sep = Ops.ToStr(A(a, 0));
            if (sep.Length == 0)
            {
                foreach (char c in s)
                {
                    res.Push(Ops.Str(c.ToString()));
                }
            }
            else
            {
                foreach (string part in s.Split(sep))
                {
                    res.Push(Ops.Str(part));
                }
            }

            return JsValue.FromObject(res);
        });
        var iterProto = new JsObject(r.IteratorPrototype);
        r.Method(iterProto, "next", 0, (t, a, nt) =>
        {
            if (t.Ref is not StringIteratorObject it)
            {
                throw Errors.Type("next method called on incompatible receiver " + Ops.DisplayValue(t));
            }

            if (it.Text is null || it.Pos >= it.Text.Length)
            {
                it.Text = null;
                return JsValue.FromObject(Ops.CreateIterResult(default, true));
            }

            int n = char.IsHighSurrogate(it.Text[it.Pos]) && it.Pos + 1 < it.Text.Length && char.IsLowSurrogate(it.Text[it.Pos + 1]) ? 2 : 1;
            string cp = it.Text.Substring(it.Pos, n);
            it.Pos += n;
            return JsValue.FromObject(Ops.CreateIterResult(Ops.Str(cp), false));
        });
        iterProto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("String Iterator"), PropFlags.Configurable);
        r.Method(proto, WellKnown.Iterator, "[Symbol.iterator]", 0, (t, a, nt) => JsValue.FromObject(new StringIteratorObject(iterProto) { Text = ThisStr(t) }));
    }
}
