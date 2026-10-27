using System.Text;

namespace RecursiveAI.OneEcma;

internal static partial class RegExpBuiltins
{
    private static void RequireCoercible(JsValue t, string method)
    {
        if (t.IsNullish)
        {
            throw Errors.Type("String.prototype." + method + " called on null or undefined");
        }
    }

    /// <summary>For matchAll and replaceAll: a RegExp argument must carry the g flag (22.1.3.14, 22.1.3.20).</summary>
    private static void RequireGlobalFlag(JsObject regexp, string method)
    {
        JsValue flags = regexp.Get("flags");
        if (flags.IsNullish)
        {
            throw Errors.Type("String.prototype." + method + " called with a non-global RegExp argument");
        }

        if (!Ops.ToStr(flags).Contains('g'))
        {
            throw Errors.Type("String.prototype." + method + " called with a non-global RegExp argument");
        }
    }

    private static void SetupStringMethods(Realm r)
    {
        JsObject proto = r.StringPrototype;

        r.Method(proto, "match", 1, (t, a, nt) =>
        {
            RequireCoercible(t, "match");
            JsValue regexp = Ops.Arg(a, 0);
            if (regexp.IsObject)
            {
                JsValue matcher = Ops.GetMethod(regexp, new PropertyKey(WellKnown.Match));
                if (!matcher.IsUndefined)
                {
                    return Ops.Call(matcher, regexp, [t]);
                }
            }

            string s = Ops.ToStr(t);
            JsRegExp rx = RegExpCreate(r, regexp, default);
            return Ops.Invoke(JsValue.FromObject(rx), new PropertyKey(WellKnown.Match), [Ops.Str(s)]);
        });

        r.Method(proto, "matchAll", 1, (t, a, nt) =>
        {
            RequireCoercible(t, "matchAll");
            JsValue regexp = Ops.Arg(a, 0);
            if (regexp.IsObject)
            {
                if (IsRegExp(regexp))
                {
                    RequireGlobalFlag((JsObject)regexp.Ref!, "matchAll");
                }

                JsValue matcher = Ops.GetMethod(regexp, new PropertyKey(WellKnown.MatchAll));
                if (!matcher.IsUndefined)
                {
                    return Ops.Call(matcher, regexp, [t]);
                }
            }

            string s = Ops.ToStr(t);
            JsRegExp rx = RegExpCreate(r, regexp, Ops.Str("g"));
            return Ops.Invoke(JsValue.FromObject(rx), new PropertyKey(WellKnown.MatchAll), [Ops.Str(s)]);
        });

        r.Method(proto, "replace", 2, (t, a, nt) =>
        {
            RequireCoercible(t, "replace");
            JsValue searchValue = Ops.Arg(a, 0);
            JsValue replaceValue = Ops.Arg(a, 1);
            if (searchValue.IsObject)
            {
                JsValue replacer = Ops.GetMethod(searchValue, new PropertyKey(WellKnown.Replace));
                if (!replacer.IsUndefined)
                {
                    return Ops.Call(replacer, searchValue, [t, replaceValue]);
                }
            }

            string str = Ops.ToStr(t);
            string searchString = Ops.ToStr(searchValue);
            bool functional = Ops.IsCallable(replaceValue);
            string replStr = functional ? string.Empty : Ops.ToStr(replaceValue);
            int position = str.IndexOf(searchString, StringComparison.Ordinal);
            if (position < 0)
            {
                return Ops.Str(str);
            }

            string replacement = functional
                ? Ops.ToStr(Ops.Call(replaceValue, default, [Ops.Str(searchString), Num(position), Ops.Str(str)]))
                : GetSubstitution(searchString, str, position, [], default, replStr);
            Agent.Current.Alloc(((long)str.Length + replacement.Length) * 2);
            return Ops.Str(string.Concat(str.AsSpan(0, position), replacement, str.AsSpan(position + searchString.Length)));
        });

        r.Method(proto, "replaceAll", 2, (t, a, nt) =>
        {
            RequireCoercible(t, "replaceAll");
            JsValue searchValue = Ops.Arg(a, 0);
            JsValue replaceValue = Ops.Arg(a, 1);
            if (searchValue.IsObject)
            {
                if (IsRegExp(searchValue))
                {
                    RequireGlobalFlag((JsObject)searchValue.Ref!, "replaceAll");
                }

                JsValue replacer = Ops.GetMethod(searchValue, new PropertyKey(WellKnown.Replace));
                if (!replacer.IsUndefined)
                {
                    return Ops.Call(replacer, searchValue, [t, replaceValue]);
                }
            }

            Agent ag = Agent.Current;
            string str = Ops.ToStr(t);
            string searchString = Ops.ToStr(searchValue);
            bool functional = Ops.IsCallable(replaceValue);
            string replStr = functional ? string.Empty : Ops.ToStr(replaceValue);
            int searchLength = searchString.Length;
            int advanceBy = Math.Max(1, searchLength);
            var positions = new List<int>();
            int position = str.IndexOf(searchString, 0, StringComparison.Ordinal);
            while (position >= 0)
            {
                ag.Tick();
                positions.Add(position);
                position = position + advanceBy > str.Length ? -1 : str.IndexOf(searchString, position + advanceBy, StringComparison.Ordinal);
            }

            int endOfLastMatch = 0;
            var result = new StringBuilder();
            foreach (int p in positions)
            {
                ag.Tick();
                string replacement = functional
                    ? Ops.ToStr(Ops.Call(replaceValue, default, [Ops.Str(searchString), Num(p), Ops.Str(str)]))
                    : GetSubstitution(searchString, str, p, [], default, replStr);
                ag.Alloc(((long)(p - endOfLastMatch) + replacement.Length) * 2);
                result.Append(str, endOfLastMatch, p - endOfLastMatch).Append(replacement);
                endOfLastMatch = p + searchLength;
            }

            if (endOfLastMatch < str.Length)
            {
                ag.Alloc((str.Length - endOfLastMatch) * 2L);
                result.Append(str, endOfLastMatch, str.Length - endOfLastMatch);
            }

            return Ops.Str(result.ToString());
        });

        r.Method(proto, "search", 1, (t, a, nt) =>
        {
            RequireCoercible(t, "search");
            JsValue regexp = Ops.Arg(a, 0);
            if (regexp.IsObject)
            {
                JsValue searcher = Ops.GetMethod(regexp, new PropertyKey(WellKnown.Search));
                if (!searcher.IsUndefined)
                {
                    return Ops.Call(searcher, regexp, [t]);
                }
            }

            string s = Ops.ToStr(t);
            JsRegExp rx = RegExpCreate(r, regexp, default);
            return Ops.Invoke(JsValue.FromObject(rx), new PropertyKey(WellKnown.Search), [Ops.Str(s)]);
        });

        r.Method(proto, "split", 2, (t, a, nt) =>
        {
            RequireCoercible(t, "split");
            JsValue separator = Ops.Arg(a, 0);
            JsValue limit = Ops.Arg(a, 1);
            if (separator.IsObject)
            {
                JsValue splitter = Ops.GetMethod(separator, new PropertyKey(WellKnown.Split));
                if (!splitter.IsUndefined)
                {
                    return Ops.Call(splitter, separator, [t, limit]);
                }
            }

            Agent ag = Agent.Current;
            string s = Ops.ToStr(t);
            uint lim = limit.IsUndefined ? uint.MaxValue : Ops.ToUint32(limit);
            string sep = Ops.ToStr(separator);
            var arr = new JsArray(r.ArrayPrototype);
            if (lim == 0)
            {
                return JsValue.FromObject(arr);
            }

            if (separator.IsUndefined)
            {
                arr.Push(Ops.Str(s));
                return JsValue.FromObject(arr);
            }

            if (sep.Length == 0)
            {
                long outLen = Math.Min(lim, s.Length);
                ag.Alloc(outLen * 48);
                for (int i = 0; i < outLen; i++)
                {
                    ag.Tick();
                    arr.Push(Ops.Str(s[i].ToString()));
                }

                return JsValue.FromObject(arr);
            }

            if (s.Length == 0)
            {
                arr.Push(Ops.Str(s));
                return JsValue.FromObject(arr);
            }

            int ii = 0;
            int j = s.IndexOf(sep, 0, StringComparison.Ordinal);
            while (j >= 0)
            {
                ag.Tick();
                ag.Alloc(((j - ii) * 2L) + 24);
                arr.Push(Ops.Str(s.Substring(ii, j - ii)));
                if (arr.Length == lim)
                {
                    return JsValue.FromObject(arr);
                }

                ii = j + sep.Length;
                j = ii > s.Length ? -1 : s.IndexOf(sep, ii, StringComparison.Ordinal);
            }

            arr.Push(Ops.Str(s.Substring(ii)));
            return JsValue.FromObject(arr);
        });
    }
}
