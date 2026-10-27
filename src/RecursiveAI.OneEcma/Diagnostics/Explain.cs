using System.Globalization;
using System.Numerics;
using System.Text;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Diagnostics;

/// <summary>
/// Turns a runtime error into an explained report using what a traced replay saw: where the failing value came from, what the
/// object actually holds and what the name was probably meant to be. It reads the live objects of the failing frame and never
/// runs script (no getters, no proxy traps), so an explanation cannot change the outcome of the run.
/// </summary>
internal static partial class Explain
{
    /// <summary>At most this many bindings are listed for a frame.</summary>
    internal const int MaxLocals = 24;

    private const int MaxPreview = 60;
    private const int MaxKeys = 6;

    // ---- locals ----------------------------------------------------------------------------------

    internal static IReadOnlyList<LocalInfo>? LocalsOf(Frame f)
    {
        var list = new List<LocalInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (f.Code.Kind is FunctionKind.Script)
        {
            foreach (KeyValuePair<string, Cell> kv in f.Realm.GlobalLexicals)
            {
                if (list.Count >= MaxLocals)
                {
                    break;
                }

                if (IsUserName(kv.Key))
                {
                    list.Add(new LocalInfo(kv.Key, kv.Value.Value.IsTdz ? "<uninitialized>" : Clip(Preview(kv.Value.Value), MaxPreview)));
                }
            }

            return list;
        }

        for (Env? e = f.Env; e is not null && list.Count < MaxLocals; e = e.Parent)
        {
            if (e.Info is { } info)
            {
                for (int i = 0; i < info.Names.Length && i < e.Slots.Length && list.Count < MaxLocals; i++)
                {
                    string n = info.Names[i];
                    if (IsUserName(n) && seen.Add(n))
                    {
                        list.Add(new LocalInfo(n, e.Slots[i].IsTdz ? "<uninitialized>" : Clip(Preview(e.Slots[i]), MaxPreview)));
                    }
                }

                if (info.IsFunctionVarScope)
                {
                    break;
                }
            }
        }

        return list;
    }

    private static bool IsUserName(string n) => n.Length > 0 && n[0] != '*' && n[0] != '%' && n[0] != '#' && n != "arguments";

    // ---- previews --------------------------------------------------------------------------------

    /// <summary>A short rendering of a value: literals for primitives, <c>{ key, key }</c> for an object, <c>Array (3 items)</c> for an array.</summary>
    internal static string Preview(JsValue v)
    {
        switch (v.Ref)
        {
            case null: return "undefined";
            case string or Rope: return "\"" + Clip(v.AsString, 30) + "\"";
            case BigInteger b: return b.ToString(NumberFormatInfo.InvariantInfo) + "n";
            case JsSymbol s: return s.ToString();
            case JsArray a: return "Array (" + Items((int)Math.Min(a.Length, int.MaxValue)) + ")";
            case JsFunction fn: return (fn is ScriptFunction { IsClassConstructor: true } ? "class " : "function ") + FunctionName(fn);
            case PromiseObject: return "Promise";
            case JsDate { Tv: double tv } when double.IsNaN(tv): return "Invalid Date";
            case JsError e: return (SafeData(e, "name") is { IsString: true } nm ? nm.AsString : "Error") + ": " + (SafeData(e, "message") is { IsString: true } ms ? Clip(ms.AsString, 30) : string.Empty);
            case JsObject o:
                {
                    string prefix = ClassPrefix(o);
                    List<string> keys = OwnKeys(o);
                    if (o is ProxyObject)
                    {
                        return "Proxy";
                    }

                    var sb = new StringBuilder(prefix);
                    if (prefix.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    if (keys.Count == 0)
                    {
                        return sb.Append("{}").ToString();
                    }

                    sb.Append("{ ");
                    for (int i = 0; i < keys.Count && i < MaxKeys; i++)
                    {
                        sb.Append(i > 0 ? ", " : string.Empty).Append(keys[i]);
                    }

                    if (keys.Count > MaxKeys)
                    {
                        sb.Append(", …");
                    }

                    return sb.Append(" }").ToString();
                }

            default:
                return v.IsNumber ? Ops.NumberToStr(v.Num) : v.IsNull ? "null" : v.IsTrue ? "true" : "false";
        }
    }

    private static string Items(int n) => n.ToString(NumberFormatInfo.InvariantInfo) + (n == 1 ? " item" : " items");

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The class name of an object that is not plain (<c>Point</c> for <c>new Point()</c>), else empty.</summary>
    private static string ClassPrefix(JsObject o)
    {
        string n = ConstructorNameOf(o);
        return n is "Object" or "" ? string.Empty : n;
    }

    private static string FunctionName(JsFunction f) => SafeData(f, "name") is { IsString: true } n && n.AsString.Length > 0 ? n.AsString : "(anonymous)";

    /// <summary>The name of the constructor an object inherits, read from data properties only.</summary>
    private static string ConstructorNameOf(JsObject o)
    {
        if (SafeData(o, "constructor").Ref is JsObject c && SafeData(c, "name") is { IsString: true } n)
        {
            return n.AsString;
        }

        return "Object";
    }

    /// <summary>A word for the kind of a value in a sentence: <c>an Object</c>, <c>an Array</c>, <c>a string</c>.</summary>
    internal static string TypeWord(JsValue v)
    {
        string w = v.Ref switch
        {
            null => "undefined",
            string or Rope => "string",
            BigInteger => "bigint",
            JsSymbol => "symbol",
            JsArray => "Array",
            JsFunction => "function",
            PromiseObject => "Promise",
            JsObject o => ConstructorNameOf(o),
            _ => v.IsNull ? "null" : v.IsNumber ? "number" : "boolean",
        };
        return w is "undefined" or "null" ? w : (IsVowel(w[0]) ? "an " : "a ") + w;
    }

    private static bool IsVowel(char c) => "AEIOUaeiou".Contains(c);

    // ---- safe reads ------------------------------------------------------------------------------

    /// <summary>Reads a data property along the prototype chain without running script; undefined when absent, inherited from a proxy or an accessor.</summary>
    private static JsValue SafeData(JsObject o, string key) => TryData(o, key, out JsValue v, out _) ? v : default;

    private static bool TryData(JsObject start, string key, out JsValue value, out bool inherited)
    {
        inherited = false;
        value = default;
        int guard = 0;
        for (JsObject? o = start; o is not null && guard++ < 64; o = o.Proto)
        {
            if (o is ProxyObject)
            {
                return false;
            }

            if (o.GetOwnProperty(new PropertyKey(key), out PropertyDescriptor d))
            {
                if (d.IsAccessor)
                {
                    return false;
                }

                value = d.Value;
                inherited = !ReferenceEquals(o, start);
                return true;
            }
        }

        return false;
    }

    private static List<string> OwnKeys(JsObject o)
    {
        var result = new List<string>();
        if (o is ProxyObject or JsTypedArray || o is JsArray { Length: > 10_000 })
        {
            // Listing the keys of a huge indexed object would cost more than the explanation is worth.
            return result;
        }

        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (k.String is { } s && o.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable && !(o is JsArray && k.TryGetIndex(out _)))
            {
                result.Add(s);
                if (result.Count >= 200)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Whether a value has a callable member, found without running script (for a primitive, on its prototype).</summary>
    private static bool HasMethod(JsValue v, string member, Realm realm)
    {
        JsObject? o = v.Ref switch
        {
            JsObject obj => obj,
            string or Rope => realm.StringPrototype,
            _ => v.IsNumber ? realm.NumberPrototype : v.IsBoolean ? realm.BooleanPrototype : null,
        };
        return o is not null && TryData(o, member, out JsValue m, out _) && m.Ref is JsFunction;
    }

    // ---- paths -----------------------------------------------------------------------------------

    private sealed class Path
    {
        internal string Root = "";
        internal List<string> Keys = [];
        internal List<bool> Bracket = [];
    }

    private static bool IsIdentStart(char c) => c == '_' || c == '$' || char.IsLetter(c);

    private static bool IsIdentPart(char c) => IsIdentStart(c) || char.IsDigit(c);

    /// <summary>Parses <c>a</c>, <c>a.b.c</c>, <c>a[0]</c> or <c>a["k"]</c>; anything else (calls, operators) is not a path.</summary>
    private static Path? ParsePath(string text)
    {
        text = text.Trim();
        int i = 0;
        if (text.Length == 0 || !IsIdentStart(text[0]))
        {
            return null;
        }

        while (i < text.Length && IsIdentPart(text[i]))
        {
            i++;
        }

        var p = new Path { Root = text[..i] };
        while (i < text.Length)
        {
            if (text[i] == '.' || (text[i] == '?' && i + 1 < text.Length && text[i + 1] == '.'))
            {
                i += text[i] == '?' ? 2 : 1;
                int s = i;
                while (i < text.Length && IsIdentPart(text[i]))
                {
                    i++;
                }

                if (i == s)
                {
                    return null;
                }

                p.Keys.Add(text[s..i]);
                p.Bracket.Add(false);
            }
            else if (text[i] == '[')
            {
                int close = text.IndexOf(']', i);
                if (close < 0)
                {
                    return null;
                }

                string inner = text[(i + 1)..close].Trim();
                if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[^1] == inner[0] && inner.IndexOf(inner[0], 1) == inner.Length - 1)
                {
                    p.Keys.Add(inner[1..^1]);
                }
                else if (inner.Length > 0 && inner.All(char.IsAsciiDigit))
                {
                    p.Keys.Add(inner);
                }
                else
                {
                    return null;
                }

                p.Bracket.Add(true);
                i = close + 1;
            }
            else
            {
                return null;
            }
        }

        return p;
    }

    private sealed class Eval
    {
        internal bool Ok;
        internal JsValue Value;
        internal JsValue Owner;       // value one step before the end; for a one-segment path, the root
        internal string? LastKey;
        internal bool Absent;         // the last key was not found at all
        internal JsValue Root;
        internal Origin? RootOrigin;
        internal JsObject? OwnerObject => Owner.Ref as JsObject;
    }

    private static Eval? Evaluate(Path p, StackFrameInfo frame, TraceState trace)
    {
        if (frame.ScopeRealm is not { } realm || !Resolve(p.Root, frame, realm, trace, out JsValue cur, out Origin? origin))
        {
            return null;
        }

        var r = new Eval { Root = cur, RootOrigin = origin, Owner = cur, Value = cur, Ok = true };
        for (int i = 0; i < p.Keys.Count; i++)
        {
            if (cur.IsNullish)
            {
                return null;
            }

            r.Owner = cur;
            r.LastKey = p.Keys[i];
            JsValue next = default;
            bool found = false;
            if (cur.Ref is JsObject o)
            {
                found = TryData(o, p.Keys[i], out next, out _);
            }
            else if (cur.IsString && p.Keys[i] == "length")
            {
                next = JsValue.FromNumber(cur.AsString.Length);
                found = true;
            }
            else if (cur.IsString && uint.TryParse(p.Keys[i], NumberStyles.None, NumberFormatInfo.InvariantInfo, out uint ix) && ix < cur.AsString.Length)
            {
                next = JsValue.FromString(cur.AsString[(int)ix].ToString());
                found = true;
            }

            if (!found && cur.Ref is JsObject o2 && (o2 is ProxyObject || HasAccessor(o2, p.Keys[i])))
            {
                return null;
            }

            r.Absent = !found;
            cur = next;
            r.Value = cur;
        }

        return r;
    }

    private static bool HasAccessor(JsObject start, string key)
    {
        for (JsObject? o = start; o is not null; o = o.Proto)
        {
            if (o.GetOwnProperty(new PropertyKey(key), out PropertyDescriptor d))
            {
                return d.IsAccessor;
            }
        }

        return false;
    }

    private static bool Resolve(string name, StackFrameInfo frame, Realm realm, TraceState trace, out JsValue value, out Origin? origin)
    {
        origin = null;
        value = default;
        for (Env? e = frame.ScopeEnv; e is not null; e = e.Parent)
        {
            if (e.Info is { } info)
            {
                int ix = info.IndexOf(name);
                if (ix >= 0 && ix < e.Slots.Length)
                {
                    if (e.Slots[ix].IsTdz)
                    {
                        return false;
                    }

                    value = e.Slots[ix];
                    origin = trace.BindingOrigin(e, ix);
                    return true;
                }
            }

            if (e.Extra is { } extra && extra.TryGetValue(name, out Cell? c))
            {
                value = c.Value;
                return !value.IsTdz;
            }
        }

        if (realm.GlobalLexicals.TryGetValue(name, out Cell? lc))
        {
            value = lc.Value;
            origin = trace.GlobalOrigin(name);
            return !value.IsTdz;
        }

        if (!HasAccessor(realm.Global, name) && TryData(realm.Global, name, out value, out _))
        {
            origin = trace.GlobalOrigin(name);
            return true;
        }

        return false;
    }

    /// <summary>All names visible from the frame: its scopes, the global lexicals and the global object's own keys.</summary>
    private static List<(string Name, JsValue Value)> VisibleBindings(StackFrameInfo frame, Realm realm)
    {
        var list = new List<(string, JsValue)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Env? e = frame.ScopeEnv; e is not null; e = e.Parent)
        {
            if (e.Info is { } info)
            {
                for (int i = 0; i < info.Names.Length && i < e.Slots.Length; i++)
                {
                    if (IsUserName(info.Names[i]) && seen.Add(info.Names[i]) && !e.Slots[i].IsTdz)
                    {
                        list.Add((info.Names[i], e.Slots[i]));
                    }
                }
            }
        }

        foreach (KeyValuePair<string, Cell> kv in realm.GlobalLexicals)
        {
            if (IsUserName(kv.Key) && seen.Add(kv.Key) && !kv.Value.Value.IsTdz)
            {
                list.Add((kv.Key, kv.Value.Value));
            }
        }

        foreach (string k in OwnKeys(realm.Global))
        {
            if (seen.Add(k) && TryData(realm.Global, k, out JsValue gv, out _))
            {
                list.Add((k, gv));
            }
        }

        // Non-enumerable globals (the built-in constructors) count too: `Obect` should find `Object`.
        var all = new List<PropertyKey>();
        realm.Global.OwnKeys(all);
        foreach (PropertyKey k in all)
        {
            if (k.String is { } s && seen.Add(s) && TryData(realm.Global, s, out JsValue gv, out _))
            {
                list.Add((s, gv));
            }
        }

        return list;
    }

    // ---- suggestions -----------------------------------------------------------------------------

    private static int Distance(string a, string b)
    {
        if (a.Length > 64 || b.Length > 64)
        {
            return 99;
        }

        // Optimal string alignment (Damerau-Levenshtein with adjacent transposition).
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>How close a candidate name is to the wanted one: lower is closer, 99 when it is not close at all.</summary>
    private static double Closeness(string wanted, string cand)
    {
        if (wanted == cand)
        {
            return 99;
        }

        if (string.Equals(wanted, cand, StringComparison.OrdinalIgnoreCase))
        {
            return 0.5;
        }

        if (wanted.Length >= 3 && cand.Length >= 3 && (cand.Contains(wanted, StringComparison.OrdinalIgnoreCase) || wanted.Contains(cand, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        int dist = Distance(wanted, cand);
        return dist <= Math.Max(2, wanted.Length / 3) && dist < Math.Max(wanted.Length, 1) ? dist + 1 : 99;
    }

    private static (string Name, JsValue Value)? Closest(string wanted, IEnumerable<(string Name, JsValue Value)> candidates, string? needsMember, Realm realm)
    {
        (string, JsValue)? best = null;
        double bestScore = 99;
        foreach ((string name, JsValue value) in candidates)
        {
            double c = Closeness(wanted, name);
            if (c >= 99)
            {
                continue;
            }

            if (needsMember is not null && HasMethod(value, needsMember, realm))
            {
                c -= 0.25;
            }

            if (c < bestScore)
            {
                bestScore = c;
                best = (name, value);
            }
        }

        return best;
    }

    private static string Short(JsValue v)
    {
        if (v.Ref is JsArray a)
        {
            return "Array, " + Items((int)Math.Min(a.Length, int.MaxValue));
        }

        string w = TypeWord(v);
        return w.StartsWith("an ", StringComparison.Ordinal) ? w[3..] : w.StartsWith("a ", StringComparison.Ordinal) ? w[2..] : w;
    }

    // ---- the explainer ---------------------------------------------------------------------------

    internal static void Apply(Diagnostic d, string headline, TraceState trace)
    {
        if (d.Stack.Count == 0 || d.Stack[0].ScopeRealm is null)
        {
            return;
        }

        StackFrameInfo frame = d.Stack[0];
        string text = d.Source.Text;
        Span primary = d.PrimarySpan;
        if (primary.Start < 0 || primary.End > text.Length || primary.End < primary.Start)
        {
            return;
        }

        string expr = text[primary.Start..primary.End];
        if (TryAffix(headline, "Cannot read properties of ", out string rest) || TryAffix(headline, "Cannot set properties of ", out rest))
        {
            ReadOfNullish(d, frame, trace, headline, expr, primary, rest);
        }
        else if (headline == "Reduce of empty array with no initial value")
        {
            ReduceOfEmpty(d, frame, trace, expr, primary);
        }
        else if (headline == "Cannot mix BigInt and other types, use explicit conversions")
        {
            BigIntMix(d, frame, trace, expr);
        }
        else if (headline.StartsWith("Cannot destructure ", StringComparison.Ordinal))
        {
            Destructure(d, frame, trace, headline, expr, primary);
        }
        else if (headline == "Invalid string length")
        {
            StringLength(d, frame, trace, expr, primary);
        }
        else if (headline == "Invalid time value")
        {
            InvalidDate(d, frame, trace, expr, primary);
        }
        else if (headline.EndsWith(" is not a function", StringComparison.Ordinal))
        {
            NotAFunction(d, frame, trace, headline[..^" is not a function".Length], expr);
        }
        else if (headline.EndsWith(" is not defined", StringComparison.Ordinal))
        {
            NotDefined(d, frame, headline[..^" is not defined".Length]);
        }
        else if (TryAffix(headline, "Cannot access '", out rest) && rest.EndsWith("' before initialization", StringComparison.Ordinal))
        {
            string name = rest[..^"' before initialization".Length];
            d.PrimaryLabel = "`" + name + "` is used before its declaration runs";
            AddDeclaration(d, name, "is declared here", "let", "const", "class");
            d.Help.Add("move the declaration above its first use");
        }
        else if (headline == "Assignment to constant variable.")
        {
            string name = LeadingIdent(expr);
            d.PrimaryLabel = "assignment to a `const` binding";
            if (name.Length > 0)
            {
                AddDeclaration(d, name, "is declared `const` here", "const");
                d.Help.Add("declare `" + name + "` with `let` if it is meant to change");
            }
        }
        else if (headline.EndsWith(" is not iterable", StringComparison.Ordinal))
        {
            NotIterable(d, frame, trace, headline[..^" is not iterable".Length], expr);
        }
        else if (TryAffix(headline, "Class constructor ", out rest) && rest.EndsWith(" cannot be invoked without 'new'", StringComparison.Ordinal))
        {
            string name = rest[..^" cannot be invoked without 'new'".Length];
            d.PrimaryLabel = "`" + name + "` is a class and needs `new`";
            d.Help.Add("write `new " + name + "(...)`");
        }
        else if (headline.EndsWith(" is not a constructor", StringComparison.Ordinal))
        {
            NotAConstructor(d, frame, trace, headline[..^" is not a constructor".Length], expr);
        }
        else if (headline == "Maximum call stack size exceeded")
        {
            Recursion(d);
        }
        else if (BlamesArgument(headline))
        {
            BlameArgument(d, frame, trace, expr, primary);
        }
    }

    /// <summary>Adds a "did you mean" help line and the matching structured candidate.</summary>
    private static void Suggest(Diagnostic d, string name, string description)
    {
        d.Help.Add("did you mean `" + name + "`? (" + description + ")");
        d.Suggestions.Add(new DidYouMean(name, description));
    }

    private static bool TryAffix(string s, string prefix, out string rest)
    {
        rest = string.Empty;
        if (s.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = s[prefix.Length..];
            return true;
        }

        return false;
    }

    private static string LeadingIdent(string s)
    {
        int i = 0;
        while (i < s.Length && IsIdentPart(s[i]))
        {
            i++;
        }

        return s[..i];
    }

    private static string Pretty(Path p, int upTo)
    {
        var sb = new StringBuilder(p.Root);
        for (int i = 0; i < upTo; i++)
        {
            sb.Append(p.Bracket[i] ? "[" + (p.Keys[i].All(char.IsAsciiDigit) ? p.Keys[i] : "\"" + p.Keys[i] + "\"") + "]" : "." + p.Keys[i]);
        }

        return sb.ToString();
    }

    private static Label? LabelFor(Diagnostic d, Origin o, string message)
    {
        if (!ReferenceEquals(o.Source, d.Source) && o.Source.Name != d.Source.Name)
        {
            return null;
        }

        return new Label(new Span(o.Start, o.End), message);
    }

    /// <summary>Adds "comes from here" labels for a binding's origin and the earlier places the same object came from.</summary>
    private static void AddOrigin(Diagnostic d, string name, Origin? origin, JsValue current, bool valueIsTheProblem)
    {
        int n = 0;
        for (Origin? o = origin; o is not null && n < TraceState.MaxChain; o = o.Previous, n++)
        {
            string message;
            if (n == 0)
            {
                message = valueIsTheProblem && current.IsNullish
                    ? "`" + name + "` is set to " + (current.IsNull ? "null" : "undefined") + " here"
                    : "`" + name + "` comes from here: " + Preview(current);
            }
            else
            {
                message = "the same object was stored here before";
            }

            Label? l = LabelFor(d, o, message);
            if (l is not null && !d.SecondaryLabels.Any(x => x.Span == l.Span))
            {
                d.SecondaryLabels.Add(l);
                d.Provenance.Add(new ProvenanceStep(l.Span, message));
            }
        }

        if (n >= TraceState.MaxChain)
        {
            d.Notes.Add("provenance is shown for the last " + TraceState.MaxChain.ToString(NumberFormatInfo.InvariantInfo) + " places a value was stored");
        }
    }

    private static void ReadOfNullish(Diagnostic d, StackFrameInfo frame, TraceState trace, string headline, string expr, Span primary, string rest)
    {
        // rest: "undefined (reading 'reduce')" / "null (setting 'name')"
        bool isNull = rest.StartsWith("null", StringComparison.Ordinal);
        int q = rest.IndexOf('\'', StringComparison.Ordinal);
        string member = q >= 0 && rest.EndsWith("')", StringComparison.Ordinal) ? rest[(q + 1)..^2] : string.Empty;
        bool setting = headline.StartsWith("Cannot set", StringComparison.Ordinal);
        string nullWord = isNull ? "null" : "undefined";
        Path? p = ParsePath(expr);
        if (p is null)
        {
            return;
        }

        Eval? ev = Evaluate(p, frame, trace);
        if (ev is null || !ev.Value.IsNullish)
        {
            return;
        }

        string full = Pretty(p, p.Keys.Count);
        d.PrimaryLabel = "`" + full + "` is " + nullWord;
        if (p.Keys.Count > 0 && !p.Bracket[^1])
        {
            // Point at the name that produced the missing value, not at the whole chain before it.
            string last = p.Keys[^1];
            int start = primary.End - last.Length;
            if (start > primary.Start && d.Source.Text.AsSpan(start, last.Length).SequenceEqual(last))
            {
                d.MovePrimary(new Span(start, primary.End));
            }
        }

        // Where did the missing value come from?
        Origin? propOrigin = ev.OwnerObject is { } oo && ev.LastKey is { } lk && !ev.Absent ? trace.PropertyOrigin(oo, lk) : null;
        if (propOrigin is not null && p.Keys.Count > 0)
        {
            string key = p.Keys[^1];
            string msg = "`" + key + "` is set to " + nullWord + " here";
            Label? l = LabelFor(d, propOrigin, msg);
            if (l is not null)
            {
                d.SecondaryLabels.Add(l);
                d.Provenance.Add(new ProvenanceStep(l.Span, msg));
            }
        }
        else if (ev.RootOrigin is not null)
        {
            AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, p.Keys.Count == 0);
        }

        if (ExplainUndefinedOrigin(d, trace, full, ev.Value, member, setting, primary, advise: true))
        {
            return;
        }

        if (p.Keys.Count > 0 && ev.OwnerObject is { } owner && ev.LastKey is { } wantedKey && frame.ScopeRealm is { } realm)
        {
            var cands = new List<(string, JsValue)>();
            foreach (string k in OwnKeys(owner))
            {
                if (TryData(owner, k, out JsValue kv, out _))
                {
                    cands.Add((k, kv));
                }
            }

            (string Name, JsValue Value)? best = Closest(wantedKey, cands, setting ? null : member, realm);
            if (best is { } b)
            {
                Suggest(d, Pretty(p, p.Keys.Count - 1) + "." + b.Name, Short(b.Value));
                return;
            }

            // No name is close, but exactly one member holds something that has the method being called (`r.items.map` where
            // `r` is `{ data: [...] }`): the value was probably kept under another name.
            if (!setting && member.Length > 0)
            {
                var holders = cands.Where(c => !c.Item2.IsNullish && c.Item2.Ref is not JsFunction && HasMethod(c.Item2, member, realm)).ToList();
                if (holders.Count == 1)
                {
                    Suggest(d, Pretty(p, p.Keys.Count - 1) + "." + holders[0].Item1, Short(holders[0].Item2));
                    return;
                }
            }
        }

        if (setting)
        {
            d.Help.Add("create it first: `" + full + " = {};`");
        }
        else if (member.Length > 0)
        {
            d.Help.Add("guard it: `" + full + (member.All(char.IsAsciiDigit) ? "?.[" + member + "]" : "?." + member) + "`");
        }
    }

    private static void NotAFunction(Diagnostic d, StackFrameInfo frame, TraceState trace, string calleeText, string expr)
    {
        Path? p = ParsePath(calleeText);
        if (p is null || frame.ScopeRealm is not { } realm)
        {
            return;
        }

        if (p.Keys.Count == 0)
        {
            Eval? ev = Evaluate(p, frame, trace);
            if (ev is null)
            {
                return;
            }

            d.PrimaryLabel = "`" + p.Root + "` is " + TypeWord(ev.Value) + ", not a function";
            AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, false);
            ExplainUndefinedOrigin(d, trace, p.Root, ev.Value, string.Empty, false, d.PrimarySpan, advise: false);
            var near = VisibleBindings(frame, realm).Where(b => b.Value.Ref is JsFunction && b.Name != p.Root);
            if (Closest(p.Root, near, null, realm) is { } fb)
            {
                Suggest(d, fb.Name, Short(fb.Value));
            }

            return;
        }

        Path ownerPath = new() { Root = p.Root, Keys = p.Keys.GetRange(0, p.Keys.Count - 1), Bracket = p.Bracket.GetRange(0, p.Bracket.Count - 1) };
        Eval? oe = Evaluate(ownerPath, frame, trace);
        if (oe is null)
        {
            return;
        }

        string member = p.Keys[^1];
        string ownerText = Pretty(p, p.Keys.Count - 1);
        JsValue ov = oe.Value;
        if (ov.IsNullish)
        {
            return;
        }

        TryDataOf(ov, member, out JsValue present, out bool found);
        d.PrimaryLabel = found
            ? "`" + calleeText + "` is " + TypeWord(present) + ", not a function"
            : "`" + ownerText + "` is " + TypeWord(ov) + ", so it has no `" + member + "`";
        AddOrigin(d, ownerPath.Root, ownerPath.Keys.Count == 0 ? oe.RootOrigin : (oe.OwnerObject is { } oo2 && oe.LastKey is { } k2 ? trace.PropertyOrigin(oo2, k2) : null) ?? oe.RootOrigin, ownerPath.Keys.Count == 0 ? oe.Root : ov, false);

        if (ov.Ref is PromiseObject)
        {
            d.Help.Add("`" + ownerText + "` is a Promise; `await` it first");
            return;
        }

        if ((ov.IsString || ov.IsNumber) && !found)
        {
            JsObject other = ov.IsString ? realm.NumberPrototype : realm.StringPrototype;
            if (TryData(other, member, out JsValue om, out _) && om.Ref is JsFunction)
            {
                d.Help.Add(ov.IsString ? "convert it first: `Number(" + ownerText + ")." + member + "`" : "convert it first: `String(" + ownerText + ")." + member + "`");
                return;
            }
        }

        if (!found && ov.Ref is not JsObject && SuggestMember(d, ownerText, member, ov, realm))
        {
            return;
        }

        if (ov.Ref is JsObject owner)
        {
            // A property that holds the thing being called (items.map for result.map)?
            foreach (string k in OwnKeys(owner))
            {
                if (TryData(owner, k, out JsValue kv, out _) && !kv.IsNullish && kv.Ref is not JsFunction && HasMethod(kv, member, realm))
                {
                    Suggest(d, ownerText + "." + k + "." + member, Short(kv));
                    return;
                }
            }

            if (!found && SuggestMember(d, ownerText, member, ov, realm))
            {
                return;
            }

            if (!found && owner is not JsArray && ConstructorNameOf(owner) == "Object" && HasMethod(JsValue.FromObject(realm.ArrayPrototype), member, realm))
            {
                d.Help.Add("`" + ownerText + "` is a plain object, not an array; to use `" + member + "` on its values call `Object.values(" + ownerText + ")." + member + "(...)`, or on its [key, value] pairs `Object.entries(" + ownerText + ")." + member + "(...)`");
            }
            else if (OwnKeys(owner).Count == 0 && owner is not PromiseObject && owner is not JsArray)
            {
                d.Help.Add("`" + ownerText + "` has no own properties; define `" + member + "` before calling it");
            }
        }
    }

    private static void TryDataOf(JsValue v, string key, out JsValue value, out bool found)
    {
        value = default;
        found = v.Ref is JsObject o && TryData(o, key, out value, out _);
    }

    private static void NotDefined(Diagnostic d, StackFrameInfo frame, string name)
    {
        d.PrimaryLabel = "`" + name + "` is not declared in this scope";
        string? py = name switch { "True" => "true", "False" => "false", "None" => "null", _ => null };
        if (py is not null)
        {
            d.Help.Add("JavaScript spells it `" + py + "`");
            return;
        }

        if (frame.ScopeRealm is { } realm && Closest(name, VisibleBindings(frame, realm), null, realm) is { } best)
        {
            Suggest(d, best.Name, Short(best.Value));
        }
    }

    private static void NotIterable(Diagnostic d, StackFrameInfo frame, TraceState trace, string text, string expr)
    {
        Path? p = ParsePath(text);
        Eval? ev = p is null ? null : Evaluate(p, frame, trace);
        if (p is null || ev is null)
        {
            return;
        }

        d.PrimaryLabel = "`" + Pretty(p, p.Keys.Count) + "` is " + TypeWord(ev.Value) + ", which cannot be unpacked or looped over";
        if (p.Keys.Count == 0)
        {
            AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, false);
        }

        if (ev.Value.Ref is JsObject and not JsFunction and not PromiseObject)
        {
            d.Help.Add("use `Object.keys(" + text + ")`, `Object.entries(" + text + ")` or object destructuring `{ ... } = " + text + "`");
        }
        else if (ev.Value.Ref is PromiseObject)
        {
            d.Help.Add("`" + text + "` is a Promise; `await` it first");
        }
    }

    private static void NotAConstructor(Diagnostic d, StackFrameInfo frame, TraceState trace, string text, string expr)
    {
        Path? p = ParsePath(text);
        Eval? ev = p is null ? null : Evaluate(p, frame, trace);
        if (p is null || ev is null)
        {
            return;
        }

        if (ev.Value.Ref is ScriptFunction { Code.IsArrow: true })
        {
            d.PrimaryLabel = "an arrow function cannot be used with `new`";
            if (p.Keys.Count == 0)
            {
                AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, false);
            }

            d.Help.Add("declare it with `function " + p.Root + "() { ... }` or call it without `new`");
        }
        else
        {
            d.PrimaryLabel = "`" + text + "` is " + TypeWord(ev.Value);
        }
    }

    private static void Recursion(Diagnostic d)
    {
        // A frame repeating at the top of the stack is the loop.
        if (d.Stack.Count >= 3 && d.Stack[0].Function.Length > 0 && d.Stack[0].Function == d.Stack[1].Function && d.Stack[1].Function == d.Stack[2].Function)
        {
            string fn = d.Stack[0].Function;
            d.PrimaryLabel = "`" + fn + "` keeps calling itself";
            d.Help.Add("check that `" + fn + "` reaches a case that returns without calling itself");
        }
    }

    private static bool BlamesArgument(string h) =>
        h == "Cannot convert undefined or null to object" || h == "Invalid array length" || h.EndsWith(" in JSON", StringComparison.Ordinal)
        || h.Contains(" in JSON at position", StringComparison.Ordinal) || h.EndsWith(" is not valid JSON", StringComparison.Ordinal);

    private static void BlameArgument(Diagnostic d, StackFrameInfo frame, TraceState trace, string call, Span primary)
    {
        // A built-in rejected an argument: when the call has exactly one, it is the one.
        // The primary span is the callee; the argument list follows it.
        string t = d.Source.Text;
        int open = primary.End;
        while (open < t.Length && char.IsWhiteSpace(t[open]))
        {
            open++;
        }

        if (open >= t.Length || t[open] != '(')
        {
            return;
        }

        int close = FindClose(t, open);
        if (close < 0)
        {
            return;
        }

        string inner = t[(open + 1)..close];
        if (inner.Trim().Length == 0 || SplitTopLevel(inner) is not { Count: 1 })
        {
            return;
        }

        string callee = call.Trim();
        int lead = inner.Length - inner.TrimStart().Length;
        int start = open + 1 + lead;
        int end = start + inner.Trim().Length;
        string arg = inner.Trim();
        d.MovePrimary(new Span(start, end));
        Path? p = ParsePath(arg);
        Eval? ev = p is null ? null : Evaluate(p, frame, trace);
        if (p is null || ev is null)
        {
            d.PrimaryLabel = "`" + (callee.StartsWith("new ", StringComparison.Ordinal) ? callee[4..] : callee) + "` rejected this argument";
            if (callee == "JSON.parse" && ReadStringLiteral(t, start, end) is { } literal)
            {
                ExplainJsonText(d, frame, trace, arg, new Span(start, end), null, null, literal.Value);
            }

            return;
        }

        d.PrimaryLabel = "`" + Pretty(p, p.Keys.Count) + "` is " + (ev.Value.IsNullish || ev.Value.IsNumber || ev.Value.IsBoolean ? Preview(ev.Value) : TypeWord(ev.Value));
        if (p.Keys.Count == 0)
        {
            AddOrigin(d, p.Root, ev.RootOrigin, ev.Root, true);
        }

        if (callee == "JSON.parse" && ev.Value.IsString)
        {
            ExplainJsonText(d, frame, trace, arg, new Span(start, end), p, ev, ev.Value.AsString);
        }
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>, skipping strings; -1 when there is none.</summary>
    private static int FindClose(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'' or '`')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != c)
                {
                    j += s[j] == '\\' ? 2 : 1;
                }

                i = j;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Splits an argument list at top-level commas, skipping strings, templates and nested brackets.</summary>
    private static List<string>? SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0;
        int from = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '"' or '\'' or '`')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != c)
                {
                    j += s[j] == '\\' ? 2 : 1;
                }

                i = j;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(s[from..i]);
                from = i + 1;
            }
        }

        parts.Add(s[from..]);
        return depth == 0 ? parts : null;
    }

    /// <summary>Labels the declaration of a binding found by scanning the source for <c>keyword name</c>; the first match wins.</summary>
    private static void AddDeclaration(Diagnostic d, string name, string message, params string[] keywords)
    {
        string t = d.Source.Text;
        foreach (string kw in keywords)
        {
            int from = 0;
            while (from < t.Length)
            {
                int i = t.IndexOf(kw, from, StringComparison.Ordinal);
                if (i < 0)
                {
                    break;
                }

                from = i + kw.Length;
                bool startOk = i == 0 || !IsIdentPart(t[i - 1]);
                int j = i + kw.Length;
                if (!startOk || j >= t.Length || !char.IsWhiteSpace(t[j]))
                {
                    continue;
                }

                while (j < t.Length && char.IsWhiteSpace(t[j]))
                {
                    j++;
                }

                if (string.CompareOrdinal(t, j, name, 0, name.Length) == 0 && (j + name.Length >= t.Length || !IsIdentPart(t[j + name.Length])))
                {
                    d.SecondaryLabels.Add(new Label(new Span(j, j + name.Length), "`" + name + "` " + message));
                    return;
                }
            }
        }
    }
}
