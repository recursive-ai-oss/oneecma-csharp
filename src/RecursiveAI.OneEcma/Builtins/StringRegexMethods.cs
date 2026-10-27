namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    /// <summary>The String.prototype methods that dispatch through the RegExp protocol: match, matchAll, replace, replaceAll, search and split.</summary>
    private static void SetupStringRegexMethods(Realm r, JsObject proto)
    {
        r.Method(proto, "split", 2, (t, a, nt) =>
        {
            string s = ThisStr(t, "split");
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
    }
}
