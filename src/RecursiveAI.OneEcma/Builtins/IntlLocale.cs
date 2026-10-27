using RecursiveAI.OneEcma.Intl;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma;

internal static partial class IntlJs
{
    private static readonly string[] WeekdayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static LocaleId LocaleOf(JsIntl o) => (LocaleId)o.Data!;

    /// <summary>InitializeLocale / Intl.Locale ( tag [ , options ] ), ECMA-402 15.1.1.</summary>
    private static void SetupLocale(Realm r, JsObject intl)
    {
        NativeFunction ctor = MakeCtor(r, intl, "Locale", 1, false, (t, a, nt, called) =>
        {
            JsIntl loc = Allocate(r, nt, "Locale");
            JsValue tagArg = A(a, 0);
            if (!tagArg.IsString && tagArg.Ref is not JsObject)
            {
                throw Errors.Type("First argument to Intl.Locale constructor can't be empty or missing");
            }

            string tag = tagArg.Ref is JsIntl { Kind: "Locale" } other ? LocaleOf(other).ToString() : Ops.ToStr(tagArg);
            var o = new Opts(CoerceOptions(A(a, 1)), "Locale");
            LocaleId id = LocaleId.Parse(tag) ?? throw Errors.Range("Incorrect locale information provided");
            id.Canonicalize();
            UpdateLanguageId(id, o);

            string? calendar = TypeOption(o, "calendar");
            string? collation = TypeOption(o, "collation");
            string? fw = o.Str("firstDayOfWeek");
            if (fw is not null)
            {
                fw = fw switch
                {
                    "0" or "7" => "sun",
                    "1" => "mon",
                    "2" => "tue",
                    "3" => "wed",
                    "4" => "thu",
                    "5" => "fri",
                    "6" => "sat",
                    _ => fw,
                };
                CheckType(fw, "firstDayOfWeek");
            }

            string? hc = o.Str("hourCycle", "h11", "h12", "h23", "h24");
            string? kf = o.Str("caseFirst", "upper", "lower", "false");
            bool? kn = o.Bool("numeric");
            string? nu = TypeOption(o, "numberingSystem");

            id.Canonicalize();
            Apply(id, "ca", calendar);
            Apply(id, "co", collation);
            Apply(id, "fw", fw);
            Apply(id, "hc", hc);
            Apply(id, "kf", kf);
            Apply(id, "kn", kn is null ? null : kn.Value ? "" : "false");
            Apply(id, "nu", nu);
            id.Canonicalize();
            loc.Data = id;
            return JsValue.FromObject(loc);
        }, out JsObject proto);
        _ = ctor;

        Accessor(r, proto, "baseName", id => id.LanguageTag());
        Accessor(r, proto, "calendar", id => id.GetKeyword("ca"));
        Accessor(r, proto, "caseFirst", id => id.GetKeyword("kf"));
        Accessor(r, proto, "collation", id => id.GetKeyword("co"));
        Accessor(r, proto, "firstDayOfWeek", id => id.GetKeyword("fw"));
        Accessor(r, proto, "hourCycle", id => id.GetKeyword("hc"));
        Accessor(r, proto, "language", id => id.Language);
        Accessor(r, proto, "numberingSystem", id => id.GetKeyword("nu"));
        r.Getter(proto, "numeric", "numeric", (t, a, nt) =>
        {
            string? kn = LocaleOf(This(t, "Locale", "numeric")).GetKeyword("kn");
            return Ops.Bool(kn is "" or "true");
        });
        Accessor(r, proto, "region", id => id.Region);
        Accessor(r, proto, "script", id => id.Script);

        r.Method(proto, "toString", 0, (t, a, nt) => Ops.Str(LocaleOf(This(t, "Locale", "toString")).ToString()));
        r.Method(proto, "maximize", 0, (t, a, nt) => Derived(r, This(t, "Locale", "maximize"), true));
        r.Method(proto, "minimize", 0, (t, a, nt) => Derived(r, This(t, "Locale", "minimize"), false));

        // 15.5.9: only the Gregorian calendar and Latin digits are carried, so the lists have one element.
        r.Method(proto, "getCalendars", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getCalendars"));
            return StringList([id.GetKeyword("ca") ?? "gregory"]);
        });
        r.Method(proto, "getCollations", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getCollations"));
            return StringList(id.GetKeyword("co") is { } co ? [co] : CollationsOf(id));
        });
        r.Method(proto, "getHourCycles", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getHourCycles"));
            return StringList(id.GetKeyword("hc") is { } hc ? [hc] : [HourCycleOf(id)]);
        });
        r.Method(proto, "getNumberingSystems", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getNumberingSystems"));
            return StringList([id.GetKeyword("nu") ?? "latn"]);
        });
        r.Method(proto, "getTimeZones", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getTimeZones"));
            if (id.Region is not { } region)
            {
                return default;
            }

            var zones = new List<string>();
            DataTree sup = CldrData.Supplemental;
            foreach (string z in sup.GetKeys("zoneRegions"))
            {
                if (sup.GetString("zoneRegions", z) == region)
                {
                    zones.Add(z);
                }
            }

            zones.Sort(StringComparer.Ordinal);
            return StringList(zones);
        });
        r.Method(proto, "getTextInfo", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getTextInfo"));
            var info = r.NewObject();
            string? script = id.Script;
            if (script is null)
            {
                script = id.Clone().Maximize().Script;
            }

            if (script is not null)
            {
                bool rtl = CldrData.Supplemental.GetString("rtlScripts", script) is not null;
                Put(info, "direction", rtl ? "rtl" : "ltr");
            }

            return JsValue.FromObject(info);
        });
        r.Method(proto, "getWeekInfo", 0, (t, a, nt) =>
        {
            LocaleId id = LocaleOf(This(t, "Locale", "getWeekInfo"));
            string region = RegionOf(id);
            DataTree sup = CldrData.Supplemental;
            string first = sup.GetString("weekData", "firstDay", region) ?? sup.GetString("weekData", "firstDay", "001") ?? "mon";
            if (id.GetKeyword("fw") is { } fw && Array.IndexOf(WeekdayNames, fw) >= 0)
            {
                first = fw;
            }

            string start = sup.GetString("weekData", "weekendStart", region) ?? sup.GetString("weekData", "weekendStart", "001") ?? "sat";
            string end = sup.GetString("weekData", "weekendEnd", region) ?? sup.GetString("weekData", "weekendEnd", "001") ?? "sun";
            var weekend = new List<JsValue>();
            int s = Array.IndexOf(WeekdayNames, start), e = Array.IndexOf(WeekdayNames, end);
            var days = new SortedSet<int>();
            for (int d = s; ; d = (d + 1) % 7)
            {
                days.Add(d + 1);
                if (d == e)
                {
                    break;
                }
            }

            foreach (int d in days)
            {
                weekend.Add(Ops.Num(d));
            }

            var info = r.NewObject();
            Put(info, "firstDay", Ops.Num(Array.IndexOf(WeekdayNames, first) + 1));
            Put(info, "weekend", JsValue.FromObject(Ops.CreateArray(weekend)));
            return JsValue.FromObject(info);
        });
    }

    private static void Accessor(Realm r, JsObject proto, string name, Func<LocaleId, string?> get) =>
        r.Getter(proto, name, name, (t, a, nt) =>
        {
            string? v = get(LocaleOf(This(t, "Locale", name)));
            return v is null ? default : Ops.Str(v);
        });

    /// <summary>The region RegionPreference (15.5.8) uses: the <c>rg</c> subdivision keyword, else the region, else the likely region.</summary>
    private static string RegionOf(LocaleId id)
    {
        if (id.GetKeyword("rg") is { Length: >= 2 } rg)
        {
            string region = rg[..2].ToUpperInvariantAscii();
            if (CldrData.Supplemental.GetString("weekData", "firstDay", region) is not null || LocaleId.IsRegionSubtag(region))
            {
                return region;
            }
        }

        return id.Region ?? id.Clone().Maximize().Region ?? "001";
    }

    private static string HourCycleOf(LocaleId id)
    {
        string region = RegionOf(id);
        DataTree sup = CldrData.Supplemental;
        string? preferred = sup.GetString("timeData", region, "_preferred") ?? sup.GetString("timeData", "001", "_preferred");
        return preferred switch
        {
            "h" => "h12",
            "K" => "h11",
            "k" => "h24",
            _ => "h23",
        };
    }

    private static SortedSet<string> CollationsOf(LocaleId id)
    {
        // The tailorings the language has, as BCP 47 types, without standard and search (15.5.10).
        string lang = id.Language;
        var list = new SortedSet<string>(StringComparer.Ordinal);
        if (LocaleResolver.BestAvailableLocale(lang) is not null)
        {
            DataTree sup = CldrData.Supplemental;
            foreach (string type in sup.GetKeys("collationTailoring", lang))
            {
                if (type is "standard" or "search")
                {
                    continue;
                }

                list.Add(type switch { "phonebook" => "phonebk", "traditional" => "trad", _ => type });
            }
        }
        else
        {
            list.Add("emoji");
            list.Add("eor");
        }

        return list;
    }

    private static JsValue Derived(Realm r, JsIntl source, bool maximize)
    {
        LocaleId id = LocaleOf(source).Clone();
        if (maximize)
        {
            id.Maximize();
        }
        else
        {
            id.Minimize();
        }

        // Construct(%Intl.Locale%, « tag ») parses and canonicalizes the new tag, so the result is a plain Locale.
        string tag = id.ToString();
        LocaleId fresh = LocaleId.Parse(tag) ?? id;
        fresh.Canonicalize();
        var loc = new JsIntl((JsObject)r.Intrinsics["%Intl.Locale.prototype%"], "Locale") { Data = fresh };
        return JsValue.FromObject(loc);
    }

    private static void UpdateLanguageId(LocaleId id, Opts o)
    {
        // 15.1.2 UpdateLanguageId: each subtag option replaces the one parsed from the tag.
        string? language = o.Str("language");
        if (language is not null && (!LocaleId.IsLanguageSubtag(language) || language.Length == 4))
        {
            throw Errors.Range("Incorrect locale information provided");
        }

        string? script = o.Str("script");
        if (script is not null && !LocaleId.IsScriptSubtag(script))
        {
            throw Errors.Range("Incorrect locale information provided");
        }

        string? region = o.Str("region");
        if (region is not null && !LocaleId.IsRegionSubtag(region))
        {
            throw Errors.Range("Incorrect locale information provided");
        }

        string? variants = o.Str("variants");
        List<string>? variantList = null;
        if (variants is not null)
        {
            if (variants.Length == 0)
            {
                throw Errors.Range("Incorrect locale information provided");
            }

            variantList = [];
            foreach (string v in variants.ToLowerInvariantAscii().Split('-'))
            {
                if (!LocaleId.IsVariantSubtag(v) || variantList.Contains(v))
                {
                    throw Errors.Range("Incorrect locale information provided");
                }

                variantList.Add(v);
            }
        }

        if (language is not null)
        {
            id.Language = language.ToLowerInvariantAscii();
        }

        if (script is not null)
        {
            id.Script = char.ToUpperInvariant(script[0]) + script[1..].ToLowerInvariantAscii();
        }

        if (region is not null)
        {
            id.Region = region.ToUpperInvariantAscii();
        }

        if (variantList is not null)
        {
            id.Variants.Clear();
            id.Variants.AddRange(variantList);
        }
    }

    /// <summary>A calendar, collation or numbering system option: a Unicode locale <c>type</c> (one or more groups of 3 to 8 alphanumerics).</summary>
    private static string? TypeOption(Opts o, string prop)
    {
        string? v = o.Str(prop);
        if (v is not null)
        {
            CheckType(v, prop);
        }

        return v;
    }

    private static void CheckType(string v, string prop)
    {
        try
        {
            LocaleResolver.ValidateNumberingSystem(v, "Locale");
        }
        catch (IntlException)
        {
            throw Errors.Range($"Invalid {prop} : {v}");
        }
    }

    private static void Apply(LocaleId id, string key, string? value)
    {
        if (value is not null)
        {
            id.SetKeyword(key, value.ToLowerInvariantAscii());
        }
    }
}
