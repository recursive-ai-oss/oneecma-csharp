using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma;

/// <summary>An Intl service instance (Collator, NumberFormat, ...): the service object behind the internal slots, plus the cached bound function.</summary>
internal sealed class JsIntl : JsObject
{
    internal readonly string Kind;

    /// <summary>The service (for Locale a <see cref="LocaleId"/>).</summary>
    internal object? Data;

    /// <summary>[[BoundFormat]] / [[BoundCompare]].</summary>
    internal JsObject? Bound;

    /// <summary>Kind specific extras (requested locale list, Segmenter granularity, ...).</summary>
    internal object? Extra;

    internal JsIntl(JsObject? proto, string kind)
        : base(proto)
    {
        Kind = kind;
    }
}

/// <summary>
/// The ECMA-402 JavaScript surface: the Intl object, its constructors, the shared option-reading abstract operations
/// (GetOption, GetNumberOption, ...) and the locale-sensitive overrides on String, Number, BigInt, Array and Date.
/// The services under <c>Intl/</c> do the work; this layer reads and converts values in the order the specification observes.
/// </summary>
internal static partial class IntlJs
{
    internal static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    private static readonly BclTimeZoneSource Zones = new();

    internal static void Setup(Realm r)
    {
        var intl = r.NewObject();
        r.Global.DefineDirect("Intl", JsValue.FromObject(intl), PropFlags.Hidden);
        intl.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Intl"), PropFlags.Configurable);
        r.Intrinsics["%Intl%"] = intl;

        // ECMA-402 8.3.1 / 8.3.2
        r.Method(intl, "getCanonicalLocales", 1, (t, a, nt) => StringList(Locales(A(a, 0))));
        r.Method(intl, "supportedValuesOf", 1, (t, a, nt) =>
        {
            string key = Ops.ToStr(A(a, 0));
            return Svc(() => StringList(IntlEnumeration.SupportedValuesOf(key)));
        });

        SetupLocale(r, intl);
        SetupCollator(r, intl);
        SetupNumberFormat(r, intl);
        SetupPluralRules(r, intl);
        SetupListFormat(r, intl);
        SetupRelativeTimeFormat(r, intl);
        SetupDisplayNames(r, intl);
        SetupDateTimeFormat(r, intl);
        SetupDurationFormat(r, intl);
        SetupSegmenter(r, intl);
        SetupLocaleMethods(r);
    }

    private static JsSymbol FallbackSymbol(Realm r)
    {
        // One symbol per realm, created on first use and kept on the Intl object's hidden registry.
        if (!r.Intrinsics.TryGetValue("%Intl.FallbackSymbolHolder%", out JsObject? holder))
        {
            holder = new JsObject((JsObject?)null);
            holder.DefineDirect("s", JsValue.FromSymbol(new JsSymbol("IntlLegacyConstructedSymbol")), PropFlags.None);
            r.Intrinsics["%Intl.FallbackSymbolHolder%"] = holder;
        }

        return (JsSymbol)holder.Get("s").Ref!;
    }

    // ---- errors ------------------------------------------------------------------------------------------

    internal static T Svc<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (IntlException e)
        {
            throw e.Kind == IntlErrorKind.Range ? Errors.Range(e.Message) : Errors.Type(e.Message);
        }
    }

    internal static void Svc(Action f)
    {
        try
        {
            f();
        }
        catch (IntlException e)
        {
            throw e.Kind == IntlErrorKind.Range ? Errors.Range(e.Message) : Errors.Type(e.Message);
        }
    }

    private static JsException Incompat(string method, JsValue t) =>
        Errors.Type("Method " + method + " called on incompatible receiver " + (t.IsString ? t.AsString : Ops.DisplayValue(t)));

    internal static JsIntl This(JsValue t, string kind, string method) =>
        t.Ref is JsIntl o && o.Kind == kind ? o : throw Incompat("Intl." + kind + ".prototype." + method, t);

    // ---- locale lists and the default locale ---------------------------------------------------------------

    internal static string DefaultLocale()
    {
        string tag = Agent.Current.Host.DefaultLocale;
        try
        {
            return LocaleResolver.CanonicalizeTag(tag);
        }
        catch (IntlException)
        {
            return "en-US";
        }
    }

    internal static string DefaultTimeZone() => Agent.Current.Host.TimeZoneId;

    internal static ITimeZoneSource ZoneSource => Zones;

    /// <summary>CanonicalizeLocaleList (ECMA-402 9.2.1).</summary>
    internal static List<string> Locales(JsValue locales)
    {
        var seen = new List<string>();
        if (locales.IsUndefined)
        {
            return seen;
        }

        if (locales.IsString || (locales.Ref is JsIntl li && li.Kind == "Locale"))
        {
            seen.Add(TagOf(locales));
            return seen;
        }

        JsObject o = Ops.ToObject(locales);
        long len = Ops.ToLength(o.Get("length"));
        for (long k = 0; k < len; k++)
        {
            Agent.Current.Tick();
            var pk = new PropertyKey(Ops.NumberToStr(k));
            if (!o.HasProperty(pk))
            {
                continue;
            }

            JsValue v = o.Get(pk);
            if (!v.IsString && v.Ref is not JsObject)
            {
                throw Errors.Type("Incorrect locale information provided");
            }

            string tag = TagOf(v);
            if (!seen.Contains(tag))
            {
                seen.Add(tag);
            }
        }

        return seen;
    }

    private static string TagOf(JsValue v)
    {
        if (v.Ref is JsIntl { Kind: "Locale" } l)
        {
            return ((LocaleId)l.Data!).ToString();
        }

        string s = Ops.ToStr(v);
        return Svc(() => LocaleResolver.CanonicalizeTag(s));
    }

    internal static JsValue StringList(IEnumerable<string> items)
    {
        var l = new List<JsValue>();
        foreach (string s in items)
        {
            l.Add(Ops.Str(s));
        }

        return JsValue.FromObject(Ops.CreateArray(l));
    }

    // ---- option reading (ECMA-402 9.2.10 - 9.2.14) ----------------------------------------------------------

    /// <summary>CoerceOptionsToObject: undefined gives no object (all options read as undefined), anything else goes through ToObject.</summary>
    internal static JsObject? CoerceOptions(JsValue options) => options.IsUndefined ? null : Ops.ToObject(options);

    /// <summary>GetOptionsObject: undefined gives no object, an object is itself, anything else is a TypeError.</summary>
    internal static JsObject? GetOptionsObject(JsValue options)
    {
        if (options.IsUndefined)
        {
            return null;
        }

        return options.Ref as JsObject ?? throw Errors.Type("Incorrect options provided");
    }

    /// <summary>Reads options of one service, naming it in error messages.</summary>
    internal readonly struct Opts(JsObject? o, string service)
    {
        internal JsObject? Obj => o;

        internal JsValue Raw(string prop) => o is null ? default : o.Get(prop);

        /// <summary>GetOption with type string.</summary>
        internal string? Str(string prop, params string[] values)
        {
            JsValue v = Raw(prop);
            if (v.IsUndefined)
            {
                return null;
            }

            string s = Ops.ToStr(v);
            if (values.Length > 0 && Array.IndexOf(values, s) < 0)
            {
                throw Errors.Range($"Value {s} out of range for Intl.{service} options property {prop}");
            }

            return s;
        }

        /// <summary>GetOption with type boolean.</summary>
        internal bool? Bool(string prop)
        {
            JsValue v = Raw(prop);
            return v.IsUndefined ? null : Ops.ToBoolean(v);
        }

        /// <summary>GetNumberOption (9.2.14): null for undefined.</summary>
        internal int? Num(string prop, int min, int max) => ToNum(Raw(prop), prop, min, max);

        internal static int? ToNum(JsValue v, string prop, int min, int max)
        {
            if (v.IsUndefined)
            {
                return null;
            }

            double d = Ops.ToNumber(v);
            if (double.IsNaN(d) || d < min || d > max)
            {
                throw Errors.Range($"{prop} value is out of range.");
            }

            return (int)Math.Floor(d);
        }
    }

    // ---- constructors --------------------------------------------------------------------------------------

    internal delegate JsValue CtorBody(JsValue thisValue, ReadOnlySpan<JsValue> args, JsObject newTarget, bool called);

    /// <summary>An Intl constructor with prototype, <c>supportedLocalesOf</c> and the toStringTag installed.</summary>
    internal static NativeFunction MakeCtor(Realm r, JsObject intl, string name, int length, bool callable, CtorBody body, out JsObject proto)
    {
        proto = r.NewObject();
        NativeFunction? self = null;
        self = new NativeFunction(r, name, length, (t, a, nt) =>
        {
            if (nt is null && !callable)
            {
                throw Errors.Type($"Constructor Intl.{name} requires 'new'");
            }

            return body(t, a, nt ?? self!, nt is null);
        }, true, null);
        self.DefineDirect("prototype", JsValue.FromObject(proto), PropFlags.None);
        proto.DefineDirect("constructor", JsValue.FromObject(self), PropFlags.Hidden);
        intl.DefineDirect(name, JsValue.FromObject(self), PropFlags.Hidden);
        r.Intrinsics["%Intl." + name + "%"] = self;
        r.Intrinsics["%Intl." + name + ".prototype%"] = proto;
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Intl." + name), PropFlags.Configurable);
        r.Method(self, "supportedLocalesOf", 1, (t, a, nt) =>
        {
            List<string> requested = Locales(A(a, 0));
            // FilterLocales (9.2.9): the options are coerced and localeMatcher is read, whatever the service.
            var o = new Opts(CoerceOptions(A(a, 1)), name);
            o.Str("localeMatcher", "lookup", "best fit");
            return StringList(LocaleResolver.SupportedLocales(requested));
        });
        return self;
    }

    internal static JsIntl Allocate(Realm r, JsObject newTarget, string kind)
    {
        JsObject proto = Ops.GetPrototypeFromConstructor(newTarget, x => x.Intrinsics["%Intl." + kind + ".prototype%"]);
        return new JsIntl(proto, kind);
    }

    /// <summary>A getter returning a cached bound function (format, compare): <c>name</c> is empty and the length is 1.</summary>
    internal static void BoundGetter(Realm r, JsObject proto, string kind, string prop, int length, Func<JsIntl, JsValue, JsValue> body)
    {
        r.Getter(proto, prop, prop, (t, a, nt) =>
        {
            JsIntl o = This(t, kind, prop);
            if (o.Bound is null)
            {
                o.Bound = new NativeFunction(r, string.Empty, length, (t2, a2, nt2) => body(o, A(a2, 0)));
            }

            return JsValue.FromObject(o.Bound);
        });
    }

    // ---- results -------------------------------------------------------------------------------------------

    internal static void Put(JsObject o, string name, JsValue v) => o.CreateDataPropertyOrThrow(name, v);

    internal static void Put(JsObject o, string name, string? v)
    {
        if (v is not null)
        {
            o.CreateDataPropertyOrThrow(name, Ops.Str(v));
        }
    }

    internal static void Put(JsObject o, string name, int? v)
    {
        if (v is not null)
        {
            o.CreateDataPropertyOrThrow(name, Ops.Num(v.Value));
        }
    }

    internal static void Put(JsObject o, string name, bool? v)
    {
        if (v is not null)
        {
            o.CreateDataPropertyOrThrow(name, Ops.Bool(v.Value));
        }
    }

    /// <summary>The parts list shared by every formatToParts: objects with <c>type</c>, <c>value</c> and optionally more.</summary>
    internal static JsObject Part(Realm r, string type, string value, params (string, string?)[] extra)
    {
        var o = r.NewObject();
        o.CreateDataPropertyOrThrow("type", Ops.Str(type));
        o.CreateDataPropertyOrThrow("value", Ops.Str(value));
        foreach ((string k, string? v) in extra)
        {
            if (v is not null)
            {
                o.CreateDataPropertyOrThrow(k, Ops.Str(v));
            }
        }

        return o;
    }
}
