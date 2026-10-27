using System.Text;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma;

internal static partial class RegExpBuiltins
{
    private static JsValue Num(long n) => JsValue.FromNumber(n);

    internal static long AdvanceStringIndex(string s, long index, bool unicode)
    {
        // ECMA-262 22.2.7.3
        if (!unicode || index + 1 >= s.Length)
        {
            return index + 1;
        }

        int i = (int)index;
        return char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]) ? index + 2 : index + 1;
    }

    private static JsObject ThisObject(JsValue t, string method)
    {
        if (t.Ref is JsObject o)
        {
            return o;
        }

        throw Incompatible("RegExp.prototype." + method, t);
    }

    /// <summary>RegExpExec (22.2.7.1).</summary>
    internal static JsValue RegExpExec(JsObject r, string s)
    {
        JsValue exec = r.Get("exec");
        if (Ops.IsCallable(exec))
        {
            JsValue res = ((JsObject)exec.Ref!).Call(JsValue.FromObject(r), [Ops.Str(s)]);
            if (!res.IsObject && !res.IsNull)
            {
                throw Errors.Type("RegExp exec method returned something other than an Object or null");
            }

            return res;
        }

        if (r is not JsRegExp rx)
        {
            throw Incompatible("RegExp.prototype.exec", JsValue.FromObject(r));
        }

        return BuiltinExec(rx, s);
    }

    /// <summary>RegExpBuiltinExec (22.2.7.2).</summary>
    internal static JsValue BuiltinExec(JsRegExp r, string s)
    {
        Agent ag = Agent.Current;
        long lastIndex = Ops.ToLength(r.Get("lastIndex"));
        bool global = r.Global;
        bool sticky = r.Sticky;
        if (!global && !sticky)
        {
            lastIndex = 0;
        }

        if (lastIndex > s.Length)
        {
            if (global || sticky)
            {
                PutLastIndex(r, Num(0));
            }

            return Null;
        }

        RegExpRealmData data = ag.Realm.RegExpData;
        data.Fuel ??= new AgentFuel(ag);
        RegExpExecResult res = r.Program.Exec(s, (int)lastIndex, sticky, data.Fuel);
        if (res.Outcome == RegExpOutcome.LimitExceeded)
        {
            throw res.Limit == RegExpLimitKind.Memory
                ? ag.Limit(LimitKind.Memory, "Execution exceeded the allocation limit of " + ag.Limits.MaxAllocatedBytes.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " bytes (regular expression backtrack stack)")
                : ag.Limit(LimitKind.Fuel, "Execution exceeded the step limit of " + ag.Limits.Fuel.ToString(System.Globalization.NumberFormatInfo.InvariantInfo) + " steps");
        }

        if (res.Outcome == RegExpOutcome.NoMatch)
        {
            if (global || sticky)
            {
                PutLastIndex(r, Num(0));
            }

            return Null;
        }

        RegExpMatch m = res.Match!;
        int e = m.End;
        if (global || sticky)
        {
            PutLastIndex(r, Num(e));
        }

        // Annex B legacy static properties: only legacy-enabled instances of the current realm update them.
        if (ReferenceEquals(r.Realm, ag.Realm) && r.LegacyFeatures)
        {
            UpdateLegacy(ag.Realm, s, m);
        }
        else
        {
            RegExpRealmData cd = ag.Realm.RegExpData;
            cd.MatchValid = false;
            cd.InputValid = false;
        }

        Realm cr = ag.Realm;
        int n = m.GroupCount;
        var a = new JsArray(cr.ArrayPrototype, n + 1);
        a.Push(Ops.Str(s.Substring(m.Start, e - m.Start)));
        var names = r.Program.GroupNames;
        bool hasGroups = names.Count > 0;
        for (int i = 1; i <= n; i++)
        {
            a.Push(m.IsGroupMatched(i) ? Ops.Str(s.Substring(m.GetGroupStart(i), m.GetGroupEnd(i) - m.GetGroupStart(i))) : default);
        }

        a.DefineDirect("index", Num(m.Start), PropFlags.Default);
        a.DefineDirect("input", Ops.Str(s), PropFlags.Default);
        JsValue groups = default;
        if (hasGroups)
        {
            var go = new JsObject((JsObject?)null);
            foreach (RegExpGroupName gn in names)
            {
                go.CreateDataPropertyOrThrow(gn.Name, ParticipatingValue(a, m, gn));
            }

            groups = JsValue.FromObject(go);
        }

        a.DefineDirect("groups", groups, PropFlags.Default);
        if (r.HasIndices)
        {
            var ia = new JsArray(cr.ArrayPrototype, n + 1);
            for (int i = 0; i <= n; i++)
            {
                ia.Push(m.IsGroupMatched(i) ? Ops.Obj(Ops.CreateArray([Num(m.GetGroupStart(i)), Num(m.GetGroupEnd(i))])) : default);
            }

            JsValue ig = default;
            if (hasGroups)
            {
                var igo = new JsObject((JsObject?)null);
                foreach (RegExpGroupName gn in names)
                {
                    int g = ParticipatingGroup(m, gn);
                    igo.CreateDataPropertyOrThrow(gn.Name, g >= 0 ? Ops.Obj(Ops.CreateArray([Num(m.GetGroupStart(g)), Num(m.GetGroupEnd(g))])) : default);
                }

                ig = JsValue.FromObject(igo);
            }

            ia.DefineDirect("groups", ig, PropFlags.Default);
            a.DefineDirect("indices", JsValue.FromObject(ia), PropFlags.Default);
        }

        return JsValue.FromObject(a);
    }

    private static readonly JsValue Null = JsValue.Null;

    private static int ParticipatingGroup(RegExpMatch m, RegExpGroupName gn)
    {
        // A repeated name maps to several groups; the one that took part in the match supplies the value.
        int found = -1;
        foreach (int g in gn.Groups)
        {
            if (m.IsGroupMatched(g))
            {
                found = g;
            }
        }

        return found;
    }

    private static JsValue ParticipatingValue(JsArray a, RegExpMatch m, RegExpGroupName gn)
    {
        int g = ParticipatingGroup(m, gn);
        return g >= 0 ? a.Get(new PropertyKey(g.ToString(System.Globalization.NumberFormatInfo.InvariantInfo))) : default;
    }

    private static JsRegExp ThisRegExp(JsValue t, string method)
    {
        if (t.Ref is JsRegExp r)
        {
            return r;
        }

        throw Incompatible("RegExp.prototype." + method, t);
    }

    private static void FlagGetter(Realm r, JsObject proto, string name, char flag)
    {
        r.Getter(proto, new PropertyKey(name), name, (t, a, nt) =>
        {
            // RegExpHasFlag (22.2.6.4.1)
            if (t.Ref is not JsObject o)
            {
                throw Errors.Type("RegExp.prototype." + name + " getter called on non-object " + Ops.DisplayValue(t));
            }

            if (o is not JsRegExp rx)
            {
                if (ReferenceEquals(o, r.RegExpData.Prototype))
                {
                    return default;
                }

                throw Errors.Type("RegExp.prototype." + name + " getter called on non-RegExp object");
            }

            return Ops.Bool(rx.Flags.Contains(flag));
        });
    }

    internal static string EscapePatternSource(string src)
    {
        // EscapeRegExpPattern (22.2.6.13.1): the result must parse back to the same pattern when put between slashes.
        if (src.Length == 0)
        {
            return "(?:)";
        }

        StringBuilder? sb = null;
        bool inClass = false;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            string? rep = null;
            if (c == '\\' && i + 1 < src.Length)
            {
                char n = src[i + 1];
                string? lt = LineTerminatorEscape(n);
                if (lt is not null)
                {
                    sb ??= new StringBuilder(src, 0, i, src.Length + 8);
                    sb.Append(lt);
                    i++;
                    continue;
                }

                sb?.Append(c).Append(n);
                i++;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                rep = "\\/";
            }
            else
            {
                rep = LineTerminatorEscape(c);
            }

            if (rep is not null)
            {
                sb ??= new StringBuilder(src, 0, i, src.Length + 8);
                sb.Append(rep);
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb is null ? src : sb.ToString();
    }

    private static string? LineTerminatorEscape(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\u2028' => "\\u2028",
        '\u2029' => "\\u2029",
        _ => null,
    };

    private static void SetupPrototype(Realm r, JsObject proto)
    {
        RegExpRealmData d = r.RegExpData;
        d.ExecFunction = r.Method(proto, "exec", 1, (t, a, nt) =>
        {
            JsRegExp rx = ThisRegExp(t, "exec");
            return BuiltinExec(rx, Ops.ToStr(Ops.Arg(a, 0)));
        });
        r.Method(proto, "test", 1, (t, a, nt) =>
        {
            JsObject rx = ThisObject(t, "test");
            string s = Ops.ToStr(Ops.Arg(a, 0));
            return Ops.Bool(!RegExpExec(rx, s).IsNull);
        });
        r.Method(proto, "toString", 0, (t, a, nt) =>
        {
            JsObject rx = ThisObject(t, "toString");
            string src = Ops.ToStr(rx.Get("source"));
            string fl = Ops.ToStr(rx.Get("flags"));
            return Ops.Str("/" + src + "/" + fl);
        });
        // Annex B.2.4.1 RegExp.prototype.compile ( pattern, flags )
        r.Method(proto, "compile", 2, (t, a, nt) =>
        {
            JsRegExp o = ThisRegExp(t, "compile");
            Realm cur = Ops.CurrentRealm;
            if (!ReferenceEquals(o.Realm, cur) || !o.LegacyFeatures)
            {
                throw Errors.Type("RegExp.prototype.compile cannot be used on this object");
            }

            JsValue pattern = Ops.Arg(a, 0);
            JsValue flags = Ops.Arg(a, 1);
            JsValue p;
            JsValue f;
            if (pattern.Ref is JsRegExp pr)
            {
                if (!flags.IsUndefined)
                {
                    throw Errors.Type("Invalid flags supplied to RegExp constructor '" + Ops.ToStr(flags) + "'");
                }

                p = Ops.Str(pr.Source);
                f = Ops.Str(pr.Flags);
            }
            else
            {
                p = pattern;
                f = flags;
            }

            return JsValue.FromObject(Initialize(o, p, f));
        });

        FlagGetter(r, proto, "dotAll", 's');
        r.Getter(proto, new PropertyKey("flags"), "flags", (t, a, nt) =>
        {
            // 22.2.6.4 get RegExp.prototype.flags
            if (t.Ref is not JsObject o)
            {
                throw Errors.Type("RegExp.prototype.flags getter called on non-object " + Ops.DisplayValue(t));
            }

            var sb = new StringBuilder(8);
            (string, char)[] table = [("hasIndices", 'd'), ("global", 'g'), ("ignoreCase", 'i'), ("multiline", 'm'), ("dotAll", 's'), ("unicode", 'u'), ("unicodeSets", 'v'), ("sticky", 'y')];
            foreach ((string name, char ch) in table)
            {
                if (Ops.ToBoolean(o.Get(name)))
                {
                    sb.Append(ch);
                }
            }

            return Ops.Str(sb.ToString());
        });
        FlagGetter(r, proto, "global", 'g');
        FlagGetter(r, proto, "hasIndices", 'd');
        FlagGetter(r, proto, "ignoreCase", 'i');
        FlagGetter(r, proto, "multiline", 'm');
        r.Getter(proto, new PropertyKey("source"), "source", (t, a, nt) =>
        {
            if (t.Ref is not JsObject o)
            {
                throw Errors.Type("RegExp.prototype.source getter called on non-object " + Ops.DisplayValue(t));
            }

            if (o is not JsRegExp rx)
            {
                if (ReferenceEquals(o, d.Prototype))
                {
                    return Ops.Str("(?:)");
                }

                throw Errors.Type("RegExp.prototype.source getter called on non-RegExp object");
            }

            return Ops.Str(EscapePatternSource(rx.Source));
        });
        FlagGetter(r, proto, "sticky", 'y');
        FlagGetter(r, proto, "unicode", 'u');
        FlagGetter(r, proto, "unicodeSets", 'v');

        r.Method(proto, WellKnown.Match, "[Symbol.match]", 1, (t, a, nt) => SymbolMatch(ThisObject(t, "@@match"), Ops.ToStr(Ops.Arg(a, 0))));
        r.Method(proto, WellKnown.MatchAll, "[Symbol.matchAll]", 1, (t, a, nt) => SymbolMatchAll(r, ThisObject(t, "@@matchAll"), Ops.ToStr(Ops.Arg(a, 0))));
        r.Method(proto, WellKnown.Replace, "[Symbol.replace]", 2, (t, a, nt) => SymbolReplace(ThisObject(t, "@@replace"), Ops.ToStr(Ops.Arg(a, 0)), Ops.Arg(a, 1)));
        r.Method(proto, WellKnown.Search, "[Symbol.search]", 1, (t, a, nt) => SymbolSearch(ThisObject(t, "@@search"), Ops.ToStr(Ops.Arg(a, 0))));
        r.Method(proto, WellKnown.Split, "[Symbol.split]", 2, (t, a, nt) => SymbolSplit(r, ThisObject(t, "@@split"), Ops.ToStr(Ops.Arg(a, 0)), Ops.Arg(a, 1)));
    }

    private static string FlagsOf(JsObject rx) => Ops.ToStr(rx.Get("flags"));

    private static long GetLastIndex(JsObject rx) => Ops.ToLength(rx.Get("lastIndex"));

    private static void SetLastIndex(JsObject rx, long v) => PutLastIndex(rx, Num(v));

    internal static void PutLastIndex(JsObject o, JsValue v)
    {
        if (!o.Set("lastIndex", v, JsValue.FromObject(o)))
        {
            throw Errors.Type("Cannot assign to read only property 'lastIndex' of object '[object " + o.ClassName + "]'");
        }
    }

    // 22.2.6.8 RegExp.prototype [ %Symbol.match% ] ( string )
    private static JsValue SymbolMatch(JsObject rx, string s)
    {
        Agent ag = Agent.Current;
        string flags = FlagsOf(rx);
        if (!flags.Contains('g'))
        {
            return RegExpExec(rx, s);
        }

        bool fullUnicode = flags.Contains('u') || flags.Contains('v');
        SetLastIndex(rx, 0);
        var arr = new JsArray(ag.Realm.ArrayPrototype);
        while (true)
        {
            ag.Tick();
            JsValue result = RegExpExec(rx, s);
            if (result.IsNull)
            {
                return arr.Count == 0 ? Null : JsValue.FromObject(arr);
            }

            string matchStr = Ops.ToStr(((JsObject)result.Ref!).Get("0"));
            ag.Alloc(matchStr.Length * 2L + 16);
            arr.Push(Ops.Str(matchStr));
            if (matchStr.Length == 0)
            {
                long thisIndex = GetLastIndex(rx);
                SetLastIndex(rx, AdvanceStringIndex(s, thisIndex, fullUnicode));
            }
        }
    }

    // 22.2.6.9 RegExp.prototype [ %Symbol.matchAll% ] ( string )
    private static JsValue SymbolMatchAll(Realm r, JsObject rx, string s)
    {
        JsObject c = Ops.SpeciesConstructor(rx, r.RegExpData.Constructor);
        string flags = FlagsOf(rx);
        JsObject matcher = Ops.Construct(c, [JsValue.FromObject(rx), Ops.Str(flags)]);
        long lastIndex = GetLastIndex(rx);
        SetLastIndex(matcher, lastIndex);
        return JsValue.FromObject(new RegExpStringIterator(r.RegExpData.IteratorPrototype, matcher, s, flags.Contains('g'), flags.Contains('u') || flags.Contains('v')));
    }

    // 22.2.6.11 RegExp.prototype [ %Symbol.replace% ] ( string, replaceValue )
    private static JsValue SymbolReplace(JsObject rx, string s, JsValue replaceValue)
    {
        Agent ag = Agent.Current;
        int lengthS = s.Length;
        bool functional = Ops.IsCallable(replaceValue);
        string replaceStr = functional ? string.Empty : Ops.ToStr(replaceValue);
        string flags = FlagsOf(rx);
        bool global = flags.Contains('g');
        bool fullUnicode = false;
        if (global)
        {
            fullUnicode = flags.Contains('u') || flags.Contains('v');
            SetLastIndex(rx, 0);
        }

        var results = new List<JsObject>();
        while (true)
        {
            ag.Tick();
            JsValue result = RegExpExec(rx, s);
            if (result.IsNull)
            {
                break;
            }

            var ro = (JsObject)result.Ref!;
            results.Add(ro);
            if (!global)
            {
                break;
            }

            string matchStr = Ops.ToStr(ro.Get("0"));
            if (matchStr.Length == 0)
            {
                long thisIndex = GetLastIndex(rx);
                SetLastIndex(rx, AdvanceStringIndex(s, thisIndex, fullUnicode));
            }
        }

        var acc = new StringBuilder();
        int nextSourcePosition = 0;
        foreach (JsObject result in results)
        {
            ag.Tick();
            long resultLength = Ops.LengthOfArrayLike(result);
            long nCaptures = Math.Max(resultLength - 1, 0);
            string matched = Ops.ToStr(result.Get("0"));
            int matchLength = matched.Length;
            double posD = Ops.ToIntegerOrInfinity(result.Get("index"));
            int position = (int)Math.Max(Math.Min(posD, lengthS), 0);
            var captures = new List<JsValue>();
            for (long n = 1; n <= nCaptures; n++)
            {
                ag.Tick();
                JsValue capN = result.Get(new PropertyKey(n.ToString(System.Globalization.NumberFormatInfo.InvariantInfo)));
                if (!capN.IsUndefined)
                {
                    capN = Ops.Str(Ops.ToStr(capN));
                }

                captures.Add(capN);
            }

            JsValue namedCaptures = result.Get("groups");
            string replacement;
            if (functional)
            {
                var args = new List<JsValue> { Ops.Str(matched) };
                args.AddRange(captures);
                args.Add(Num(position));
                args.Add(Ops.Str(s));
                if (!namedCaptures.IsUndefined)
                {
                    args.Add(namedCaptures);
                }

                replacement = Ops.ToStr(Ops.Call(replaceValue, default, args.ToArray()));
            }
            else
            {
                if (!namedCaptures.IsUndefined)
                {
                    namedCaptures = JsValue.FromObject(Ops.ToObject(namedCaptures));
                }

                replacement = GetSubstitution(matched, s, position, captures, namedCaptures, replaceStr);
            }

            if (position >= nextSourcePosition)
            {
                ag.Alloc(((long)(position - nextSourcePosition) + replacement.Length) * 2);
                acc.Append(s, nextSourcePosition, position - nextSourcePosition).Append(replacement);
                nextSourcePosition = position + matchLength;
            }
        }

        if (nextSourcePosition >= lengthS)
        {
            return Ops.Str(acc.ToString());
        }

        ag.Alloc((lengthS - nextSourcePosition) * 2L);
        return Ops.Str(acc.Append(s, nextSourcePosition, lengthS - nextSourcePosition).ToString());
    }

    /// <summary>GetSubstitution (22.1.3.19.1).</summary>
    internal static string GetSubstitution(string matched, string str, int position, IReadOnlyList<JsValue> captures, JsValue namedCaptures, string template)
    {
        int stringLength = str.Length;
        int i = 0;
        StringBuilder? sb = null;
        int literalStart = 0;
        while (i < template.Length)
        {
            if (template[i] != '$' || i + 1 >= template.Length)
            {
                i++;
                continue;
            }

            char c = template[i + 1];
            string? refReplacement = null;
            int refLength = 2;
            if (c == '$')
            {
                refReplacement = "$";
            }
            else if (c == '`')
            {
                refReplacement = str.Substring(0, Math.Min(position, stringLength));
            }
            else if (c == '&')
            {
                refReplacement = matched;
            }
            else if (c == '\'')
            {
                int tailPos = position + matched.Length;
                refReplacement = tailPos >= stringLength ? string.Empty : str.Substring(tailPos);
            }
            else if (c >= '0' && c <= '9')
            {
                int digitCount = i + 2 < template.Length && template[i + 2] >= '0' && template[i + 2] <= '9' ? 2 : 1;
                int index = digitCount == 2 ? ((c - '0') * 10) + (template[i + 2] - '0') : c - '0';
                int captureLen = captures.Count;
                if (index > captureLen && digitCount == 2)
                {
                    digitCount = 1;
                    index = c - '0';
                }

                refLength = 1 + digitCount;
                if (index >= 1 && index <= captureLen)
                {
                    JsValue cap = captures[index - 1];
                    refReplacement = cap.IsUndefined ? string.Empty : Ops.ToStr(cap);
                }
                else
                {
                    i += refLength;
                    continue;
                }
            }
            else if (c == '<')
            {
                int gt = template.IndexOf('>', i);
                if (gt < 0 || namedCaptures.IsUndefined)
                {
                    i += 2;
                    continue;
                }

                string groupName = template.Substring(i + 2, gt - i - 2);
                JsValue cap = ((JsObject)namedCaptures.Ref!).Get(groupName);
                refReplacement = cap.IsUndefined ? string.Empty : Ops.ToStr(cap);
                refLength = gt + 1 - i;
            }
            else
            {
                i++;
                continue;
            }

            sb ??= new StringBuilder();
            sb.Append(template, literalStart, i - literalStart).Append(refReplacement);
            i += refLength;
            literalStart = i;
        }

        if (sb is null)
        {
            return template;
        }

        return sb.Append(template, literalStart, template.Length - literalStart).ToString();
    }

    // 22.2.6.12 RegExp.prototype [ %Symbol.search% ] ( string )
    private static JsValue SymbolSearch(JsObject rx, string s)
    {
        JsValue previous = rx.Get("lastIndex");
        if (!Ops.SameValue(previous, Num(0)))
        {
            PutLastIndex(rx, Num(0));
        }

        JsValue result = RegExpExec(rx, s);
        JsValue current = rx.Get("lastIndex");
        if (!Ops.SameValue(current, previous))
        {
            PutLastIndex(rx, previous);
        }

        return result.IsNull ? Num(-1) : ((JsObject)result.Ref!).Get("index");
    }

    // 22.2.6.14 RegExp.prototype [ %Symbol.split% ] ( string, limit )
    private static JsValue SymbolSplit(Realm r, JsObject rx, string s, JsValue limit)
    {
        Agent ag = Agent.Current;
        JsObject c = Ops.SpeciesConstructor(rx, r.RegExpData.Constructor);
        string flags = FlagsOf(rx);
        bool unicodeMatching = flags.Contains('u') || flags.Contains('v');
        string newFlags = flags.Contains('y') ? flags : flags + "y";
        JsObject splitter = Ops.Construct(c, [JsValue.FromObject(rx), Ops.Str(newFlags)]);
        var arr = new JsArray(ag.Realm.ArrayPrototype);
        uint lim = limit.IsUndefined ? uint.MaxValue : Ops.ToUint32(limit);
        if (lim == 0)
        {
            return JsValue.FromObject(arr);
        }

        if (s.Length == 0)
        {
            JsValue z0 = RegExpExec(splitter, s);
            if (!z0.IsNull)
            {
                return JsValue.FromObject(arr);
            }

            arr.Push(Ops.Str(s));
            return JsValue.FromObject(arr);
        }

        int size = s.Length;
        int p = 0;
        long q = p;
        while (q < size)
        {
            ag.Tick();
            SetLastIndex(splitter, q);
            JsValue z = RegExpExec(splitter, s);
            if (z.IsNull)
            {
                q = AdvanceStringIndex(s, q, unicodeMatching);
                continue;
            }

            long e = Math.Min(GetLastIndex(splitter), size);
            if (e == p)
            {
                q = AdvanceStringIndex(s, q, unicodeMatching);
                continue;
            }

            ag.Alloc(((q - p) * 2) + 16);
            arr.Push(Ops.Str(s.Substring(p, (int)q - p)));
            if (arr.Length == lim)
            {
                return JsValue.FromObject(arr);
            }

            p = (int)e;
            var zo = (JsObject)z.Ref!;
            long numberOfCaptures = Math.Max(Ops.LengthOfArrayLike(zo) - 1, 0);
            for (long i = 1; i <= numberOfCaptures; i++)
            {
                ag.Tick();
                arr.Push(zo.Get(new PropertyKey(i.ToString(System.Globalization.NumberFormatInfo.InvariantInfo))));
                if (arr.Length == lim)
                {
                    return JsValue.FromObject(arr);
                }
            }

            q = p;
        }

        arr.Push(Ops.Str(s.Substring(p)));
        return JsValue.FromObject(arr);
    }

    // ---- %RegExpStringIteratorPrototype% (22.2.9) -----------------------------------------------------------------------------

    private static void SetupIterator(Realm r)
    {
        var ip = new JsObject(r.IteratorPrototype);
        r.RegExpData.IteratorPrototype = ip;
        r.Intrinsics["%RegExpStringIteratorPrototype%"] = ip;
        r.Method(ip, "next", 0, (t, a, nt) =>
        {
            if (t.Ref is not RegExpStringIterator it)
            {
                throw Incompatible("%RegExpStringIterator%.prototype.next", t);
            }

            if (it.Done)
            {
                return Ops.Obj(Ops.CreateIterResult(default, true));
            }

            JsValue match = RegExpExec(it.Matcher, it.Text);
            if (match.IsNull)
            {
                it.Done = true;
                return Ops.Obj(Ops.CreateIterResult(default, true));
            }

            if (!it.Global)
            {
                it.Done = true;
                return Ops.Obj(Ops.CreateIterResult(match, false));
            }

            string matchStr = Ops.ToStr(((JsObject)match.Ref!).Get("0"));
            if (matchStr.Length == 0)
            {
                long thisIndex = GetLastIndex(it.Matcher);
                SetLastIndex(it.Matcher, AdvanceStringIndex(it.Text, thisIndex, it.Unicode));
            }

            return Ops.Obj(Ops.CreateIterResult(match, false));
        });
        ip.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("RegExp String Iterator"), PropFlags.Configurable);
    }
}

internal sealed class RegExpStringIterator : JsObject
{
    internal readonly JsObject Matcher;
    internal readonly string Text;
    internal readonly bool Global;
    internal readonly bool Unicode;
    internal bool Done;

    internal RegExpStringIterator(JsObject proto, JsObject matcher, string text, bool global, bool unicode)
        : base(proto)
    {
        Matcher = matcher;
        Text = text;
        Global = global;
        Unicode = unicode;
    }
}
