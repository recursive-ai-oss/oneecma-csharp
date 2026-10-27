using System.Text;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma;

/// <summary>A RegExp instance (ECMA-262 22.2.4): the compiled matcher, the original source and flags, and the realm it was created in.</summary>
internal sealed class JsRegExp : JsObject
{
    internal RegExpProgram Program = null!;
    internal string Source = string.Empty;
    internal string Flags = string.Empty;
    internal bool Global;
    internal bool Sticky;
    internal bool HasIndices;
    internal bool FullUnicode;

    /// <summary>The realm the object was allocated in (Annex B legacy features, [[Realm]]).</summary>
    internal readonly Realm Realm;

    /// <summary>[[LegacyFeaturesEnabled]]: true only for objects created directly by %RegExp% (Annex B legacy static properties).</summary>
    internal readonly bool LegacyFeatures;

    internal JsRegExp(JsObject? proto, Realm realm, bool legacy)
        : base(proto)
    {
        Realm = realm;
        LegacyFeatures = legacy;

        // ECMA-262 22.2.3.2 RegExpAlloc step 5: lastIndex is writable, not enumerable, not configurable.
        AddProperty("lastIndex", JsValue.FromNumber(0), PropFlags.Writable);
    }

    internal override string ClassName => "RegExp";

    internal void Initialize(RegExpProgram program, string source, string flags)
    {
        Program = program;
        Source = source;
        Flags = flags;
        Global = flags.Contains('g');
        Sticky = flags.Contains('y');
        HasIndices = flags.Contains('d');
        FullUnicode = flags.Contains('u') || flags.Contains('v');
    }
}

/// <summary>Per-realm RegExp state: intrinsics, the compiled-pattern cache and the Annex B legacy static properties.</summary>
internal sealed class RegExpRealmData
{
    internal JsObject Prototype = null!;
    internal JsObject Constructor = null!;
    internal JsObject IteratorPrototype = null!;
    internal JsObject ExecFunction = null!;
    internal readonly Dictionary<(string, string), RegExpProgram> Cache = new();
    internal AgentFuel? Fuel;

    // Annex B legacy static properties (legacy RegExp features): all of them read as "empty" (TypeError) until a match by a
    // legacy-enabled instance of this realm fills them in.
    internal bool MatchValid;
    internal bool InputValid;
    internal string Input = string.Empty;
    internal string Matched = string.Empty;
    internal int Start;
    internal int End;
    internal int[] Captures = [];
}

internal sealed partial class Realm
{
    internal RegExpRealmData RegExpData = new();
}

/// <summary>Charges the regular-expression matcher's work against the agent's step budget, so a runaway pattern ends in the same limit report as a runaway loop.</summary>
internal sealed class AgentFuel : FuelMeter
{
    private readonly Agent _agent;

    internal AgentFuel(Agent agent)
        : base(long.MaxValue)
    {
        _agent = agent;
    }

    public override bool TryConsume(long n)
    {
        // Throws the agent's limit exception when the budget, the clock or cancellation says stop.
        _agent.Charge(n);
        return true;
    }

    public override void Refund(long n)
    {
        if (n > 0)
        {
            _agent.Fuel += n;
        }
    }
}

internal static partial class RegExpBuiltins
{
    private const int CacheLimit = 512;

    internal static RegExpProgram CompilePattern(Realm r, string pattern, string flags)
    {
        RegExpRealmData d = r.RegExpData;
        if (d.Cache.TryGetValue((pattern, flags), out RegExpProgram? p))
        {
            return p;
        }

        RegExpCompileResult res = RegExpProgram.Compile(pattern, flags);
        if (res.Program is null)
        {
            throw Errors.Syntax(res.Diagnostics.Count > 0 ? res.Diagnostics[0].Message : "Invalid regular expression: /" + pattern + "/" + flags + ": Invalid pattern");
        }

        if (d.Cache.Count >= CacheLimit)
        {
            d.Cache.Clear();
        }

        d.Cache[(pattern, flags)] = res.Program;
        return res.Program;
    }

    /// <summary>The value of a regular-expression literal, evaluated each time the literal is (ECMA-262 13.2.7.3).</summary>
    internal static JsValue CreateLiteral(Realm r, string pattern, string flags)
    {
        RegExpRealmData d = r.RegExpData;
        var o = new JsRegExp(d.Prototype, r, true);
        o.Initialize(CompilePattern(r, pattern, flags), pattern, flags);
        return JsValue.FromObject(o);
    }

    /// <summary>RegExpCreate (22.2.3.1).</summary>
    internal static JsRegExp RegExpCreate(Realm r, JsValue pattern, JsValue flags)
    {
        var o = new JsRegExp(r.RegExpData.Prototype, r, true);
        Initialize(o, pattern, flags);
        return o;
    }

    /// <summary>RegExpInitialize (22.2.3.3).</summary>
    internal static JsRegExp Initialize(JsRegExp o, JsValue pattern, JsValue flags)
    {
        string p = pattern.IsUndefined ? string.Empty : Ops.ToStr(pattern);
        string f = flags.IsUndefined ? string.Empty : Ops.ToStr(flags);
        o.Initialize(CompilePattern(Ops.CurrentRealm, p, f), p, f);
        PutLastIndex(o, JsValue.FromNumber(0));
        return o;
    }

    /// <summary>IsRegExp (7.2.8).</summary>
    internal static bool IsRegExp(JsValue v)
    {
        if (v.Ref is not JsObject o)
        {
            return false;
        }

        JsValue m = o.Get(new PropertyKey(WellKnown.Match));
        if (!m.IsUndefined)
        {
            return Ops.ToBoolean(m);
        }

        return o is JsRegExp;
    }

    internal static JsException Incompatible(string method, JsValue receiver) =>
        Errors.Type("Method " + method + " called on incompatible receiver " + Ops.DisplayValue(receiver));

    // ---- legacy static properties (Annex B, legacy RegExp features) ----------------------------------------------------------

    private static void UpdateLegacy(Realm current, string s, RegExpMatch m)
    {
        RegExpRealmData d = current.RegExpData;
        d.MatchValid = true;
        d.InputValid = true;
        d.Input = s;
        d.Start = m.Start;
        d.End = m.End;
        int n = m.GroupCount;
        var caps = new int[2 * n];
        for (int i = 0; i < n; i++)
        {
            caps[2 * i] = m.GetGroupStart(i + 1);
            caps[(2 * i) + 1] = m.GetGroupEnd(i + 1);
        }

        d.Captures = caps;
    }

    private static string LegacyParen(RegExpRealmData d, int i)
    {
        if (2 * i + 1 >= d.Captures.Length || d.Captures[2 * i] < 0)
        {
            return string.Empty;
        }

        return d.Input.Substring(d.Captures[2 * i], d.Captures[(2 * i) + 1] - d.Captures[2 * i]);
    }

    private static void SetupLegacy(Realm r, NativeFunction ctor)
    {
        RegExpRealmData d = r.RegExpData;

        JsValue Read(JsValue t, bool inputSlot, Func<string> get)
        {
            // GetLegacyRegExpStaticProperty: the receiver must be this realm's %RegExp% itself, and the slot must hold a value.
            if (!ReferenceEquals(t.Ref, ctor))
            {
                throw Errors.Type("RegExp legacy static property accessed on an incompatible receiver");
            }

            if (!(inputSlot ? d.InputValid : d.MatchValid))
            {
                throw Errors.Type("RegExp legacy static properties are not available for this match");
            }

            return Ops.Str(get());
        }

        void Define(string name, string alias, bool inputSlot, Func<string> get, bool setter = false)
        {
            NativeFn getter = (t, a, nt) => Read(t, inputSlot, get);
            NativeFn? set = null;
            if (setter)
            {
                set = (t, a, nt) =>
                {
                    if (!ReferenceEquals(t.Ref, ctor))
                    {
                        throw Errors.Type("RegExp legacy static property accessed on an incompatible receiver");
                    }

                    d.Input = Ops.ToStr(Ops.Arg(a, 0));
                    d.InputValid = true;
                    return default;
                };
            }

            r.Getter(ctor, new PropertyKey(name), name, getter, set);
            r.Getter(ctor, new PropertyKey(alias), alias, getter, set);
        }

        Define("input", "$_", true, () => d.Input, true);
        Define("lastMatch", "$&", false, () => d.Input.Substring(d.Start, d.End - d.Start));
        Define("lastParen", "$+", false, () => d.Captures.Length == 0 ? string.Empty : LegacyParen(d, (d.Captures.Length / 2) - 1));
        Define("leftContext", "$`", false, () => d.Input.Substring(0, d.Start));
        Define("rightContext", "$'", false, () => d.Input.Substring(d.End));
        for (int i = 1; i <= 9; i++)
        {
            int idx = i - 1;
            string nm = "$" + i.ToString(System.Globalization.NumberFormatInfo.InvariantInfo);
            r.Getter(ctor, new PropertyKey(nm), nm, (t, a, nt) => Read(t, false, () => LegacyParen(d, idx)));
        }
    }

    // ---- constructor and RegExp.escape ---------------------------------------------------------------------------------------

    internal static void Setup(Realm r)
    {
        RegExpRealmData d = r.RegExpData;
        JsObject proto = r.NewObject();
        d.Prototype = proto;
        r.Intrinsics["%RegExp.prototype%"] = proto;
        NativeFunction? self = null;
        NativeFunction ctor = r.Ctor("RegExp", 2, (t, a, nt) =>
        {
            JsValue pattern = Ops.Arg(a, 0);
            JsValue flags = Ops.Arg(a, 1);

            // ECMA-262 22.2.4.1 RegExp ( pattern, flags )
            bool patternIsRegExp = IsRegExp(pattern);
            JsObject newTarget;
            if (nt is null)
            {
                newTarget = self!;
                if (patternIsRegExp && flags.IsUndefined)
                {
                    JsValue pc = ((JsObject)pattern.Ref!).Get("constructor");
                    if (Ops.SameValue(JsValue.FromObject(newTarget), pc))
                    {
                        return pattern;
                    }
                }
            }
            else
            {
                newTarget = nt;
            }

            JsValue p;
            JsValue f;
            if (pattern.Ref is JsRegExp pr)
            {
                p = Ops.Str(pr.Source);
                f = flags.IsUndefined ? Ops.Str(pr.Flags) : flags;
            }
            else if (patternIsRegExp)
            {
                var po = (JsObject)pattern.Ref!;
                p = po.Get("source");
                f = flags.IsUndefined ? po.Get("flags") : flags;
            }
            else
            {
                p = pattern;
                f = flags;
            }

            // RegExpAlloc (22.2.3.2); Annex B: legacy features are enabled only when newTarget is this realm's %RegExp%.
            Realm cur = Ops.CurrentRealm;
            JsObject protoObj = Ops.GetPrototypeFromConstructor(newTarget, x => x.RegExpData.Prototype);
            var o = new JsRegExp(protoObj, cur, ReferenceEquals(newTarget, cur.RegExpData.Constructor));
            return JsValue.FromObject(Initialize(o, p, f));
        }, proto);
        self = ctor;
        d.Constructor = ctor;
        r.Intrinsics["%RegExp%"] = ctor;
        r.Getter(ctor, new PropertyKey(WellKnown.Species), "[Symbol.species]", (t, a, nt) => t);
        r.Method(ctor, "escape", 1, (t, a, nt) => Ops.Str(Escape(Ops.Arg(a, 0))));
        SetupLegacy(r, ctor);
        SetupPrototype(r, proto);
        SetupIterator(r);
        SetupStringMethods(r);
    }

    // RegExp.escape (22.2.5.1)
    private static string Escape(JsValue s)
    {
        if (!s.IsString)
        {
            throw Errors.Type("RegExp.escape requires a string argument");
        }

        string str = s.AsString;
        var sb = new StringBuilder(str.Length + 8);
        bool first = true;
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            Agent.Current.Tick();
            if (first && char.IsAsciiLetterOrDigit(c))
            {
                sb.Append("\\x").Append(((int)c).ToString("x2", System.Globalization.NumberFormatInfo.InvariantInfo));
                first = false;
                continue;
            }

            first = false;
            if (char.IsHighSurrogate(c) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
            {
                sb.Append(c).Append(str[i + 1]);
                i++;
                continue;
            }

            EncodeForEscape(sb, c);
        }

        return sb.ToString();
    }

    private static void EncodeForEscape(StringBuilder sb, char c)
    {
        // 22.2.5.1.1 EncodeForRegExpEscape
        if ("^$\\.*+?()[]{}|/".Contains(c))
        {
            sb.Append('\\').Append(c);
            return;
        }

        switch (c)
        {
            case '\t': sb.Append("\\t"); return;
            case '\n': sb.Append("\\n"); return;
            case '\v': sb.Append("\\v"); return;
            case '\f': sb.Append("\\f"); return;
            case '\r': sb.Append("\\r"); return;
        }

        if (",-=<>#&!%:;@~'`\"".Contains(c) || IsWhiteSpaceOrLineTerminator(c) || char.IsSurrogate(c))
        {
            if (c <= 0xFF)
            {
                sb.Append("\\x").Append(((int)c).ToString("x2", System.Globalization.NumberFormatInfo.InvariantInfo));
            }
            else
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.NumberFormatInfo.InvariantInfo));
            }

            return;
        }

        sb.Append(c);
    }

    private static bool IsWhiteSpaceOrLineTerminator(char c) =>
        c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\u00A0' or '\u1680' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF'
        || (c >= '\u2000' && c <= '\u200A');
}
