using System.Text.Json;
using System.Xml.Linq;
using RecursiveAI.OneEcma.LocaleDataGen;

// Usage: oneecma-localedata-gen --cldr <dir with json/ and common/> --ucd <dir> --tzdata <dir with zone.tab> --out <dir>
// Writes the embedded locale-data blobs. Output is a pure function of the pinned inputs.
string? cldr = null;
string? ucd = null;
string? output = null;
string? tzdata = null;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--cldr": cldr = args[i + 1]; break;
        case "--ucd": ucd = args[i + 1]; break;
        case "--tzdata": tzdata = args[i + 1]; break;
        case "--out": output = args[i + 1]; break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

if (cldr is null || ucd is null || output is null || tzdata is null)
{
    Console.Error.WriteLine("usage: --cldr <dir> --ucd <dir> --tzdata <dir> --out <dir>");
    return 2;
}

Directory.CreateDirectory(output);
var gen = new Generator(Path.Combine(cldr, "json"), Path.Combine(cldr, "common", "common"), ucd, tzdata, output);
gen.Run();
return 0;

/// <summary>Builds every blob. The locale list here is the only place a locale is added.</summary>
internal sealed class Generator(string json, string common, string ucd, string tzdata, string output)
{
    // Stored locales. A region locale needs its CLDR parent chain stored too (en-GB -> en-001 -> en -> und),
    // since each blob holds only what differs from its parent. en-US and pt-BR are CLDR default content
    // of en and pt, so they resolve to those blobs.
    private static readonly string[] Locales = ["und", "en", "en-001", "en-GB", "es", "es-419", "es-US", "fr", "de", "pt"];

    // Collation tailoring languages (CLDR common/collation/<lang>.xml).
    private static readonly string[] CollationLanguages = ["root", "en", "es", "fr", "de", "pt"];

    // (package, file, path inside main.<locale>, destination key)
    private static readonly (string Pkg, string File, string[] Path, string Dest)[] LocaleSources =
    [
        ("cldr-numbers-full", "numbers", ["numbers"], "numbers"),
        ("cldr-numbers-full", "currencies", ["numbers", "currencies"], "currencies"),
        ("cldr-dates-full", "ca-gregorian", ["dates", "calendars", "gregorian"], "gregorian"),
        ("cldr-dates-full", "dateFields", ["dates", "fields"], "dateFields"),
        ("cldr-dates-full", "timeZoneNames", ["dates", "timeZoneNames"], "timeZoneNames"),
        ("cldr-localenames-full", "languages", ["localeDisplayNames", "languages"], "languages"),
        ("cldr-localenames-full", "scripts", ["localeDisplayNames", "scripts"], "scripts"),
        ("cldr-localenames-full", "territories", ["localeDisplayNames", "territories"], "territories"),
        ("cldr-localenames-full", "variants", ["localeDisplayNames", "variants"], "variants"),
        ("cldr-localenames-full", "localeDisplayNames", ["localeDisplayNames"], "displayNames"),
        ("cldr-misc-full", "listPatterns", ["listPatterns"], "listPatterns"),
        ("cldr-misc-full", "layout", ["layout"], "layout"),
        ("cldr-units-full", "units", ["units"], "units"),
    ];

    // (file under cldr-core/supplemental, path inside "supplemental", destination key)
    private static readonly (string File, string[] Path, string Dest)[] SupplementalSources =
    [
        ("likelySubtags", ["likelySubtags"], "likelySubtags"),
        ("aliases", ["metadata", "alias"], "aliases"),
        ("parentLocales", ["parentLocales", "parentLocale"], "parentLocales"),
        ("currencyData", ["currencyData"], "currencyData"),
        ("weekData", ["weekData"], "weekData"),
        ("timeData", ["timeData"], "timeData"),
        ("calendarPreferenceData", ["calendarPreferenceData"], "calendarPreferenceData"),
        ("numberingSystems", ["numberingSystems"], "numberingSystems"),
        ("metaZones", ["metaZones", "metazoneInfo", "timezone"], "metaZoneInfo"),
        ("primaryZones", ["primaryZones"], "primaryZones"),
        ("metaZones", ["metaZones", "metazones"], "metaZoneMap"),
        ("pluralRanges", ["plurals"], "pluralRanges"),
        ("dayPeriods", ["dayPeriodRuleSet"], "dayPeriodRules"),
        ("dayPeriods", ["dayPeriodRuleSet-type-selection"], "dayPeriodSelectionRules"),
        ("territoryContainment", ["territoryContainment"], "territoryContainment"),
    ];

    private static readonly string[] Bcp47Files = ["calendar", "collation", "currency", "measure", "number", "timezone"];

    public void Run()
    {
        Console.WriteLine($"{"blob",-24} {"bytes",9}");
        Obj supplemental = BuildSupplemental();
        Emit("supplemental.bin", TreeWriter.Write(supplemental));
        Breakdown(supplemental);
        var resolved = new Dictionary<string, Obj>(StringComparer.Ordinal);
        foreach (string loc in Locales)
        {
            resolved[loc] = LoadLocale(loc);
        }

        var parents = ParentMap();
        foreach (string loc in Locales)
        {
            Obj delta = loc == "und" ? resolved[loc] : (Obj)(TreeOps.Delta(resolved[loc], resolved[parents[loc]]) ?? new Obj());
            delta.Members["@parent"] = new Str(loc == "und" ? "" : parents[loc]);
            Emit($"locale-{loc}.bin", TreeWriter.Write(delta));
            if (loc is "en" or "und")
            {
                Breakdown(delta);
            }
        }

        string core = Path.Combine(json, "cldr-core", "supplemental");
        Emit("plurals.bin", Plurals.Write(Path.Combine(core, "plurals.json"), Path.Combine(core, "ordinals.json")));
        Emit("collation.bin", Collation.Write(Path.Combine(common, "uca", "allkeys_CLDR.txt"), ucd));
        Emit("breaks.bin", Breaks.Write(ucd));
        Emit("normalization.bin", Normalization.Write(ucd));
    }

    // Informational: what each top-level section costs when stored on its own (shared strings counted in full).
    private static void Breakdown(Obj tree)
    {
        foreach (var (key, value) in tree.Members.Where(m => m.Value is Obj))
        {
            var single = new Obj();
            single.Members[key] = value;
            Console.WriteLine($"    {key,-28} {TreeWriter.Write(single).Length,9}");
        }
    }

    private void Emit(string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(output, name), bytes);
        Console.WriteLine($"{name,-24} {bytes.Length,9}");
    }

    // CLDR parentLocales override truncation (UTS #35 Locale Inheritance); otherwise drop the last subtag.
    private Dictionary<string, string> ParentMap()
    {
        Node explicitParents = TreeOps.FromJson(TreeOps.Load(Path.Combine(json, "cldr-core", "supplemental", "parentLocales.json")));
        var map = ((Obj)TreeOps.Dig(explicitParents, "supplemental", "parentLocales", "parentLocale")!).Members;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string loc in Locales.Where(l => l != "und"))
        {
            string parent;
            if (map.TryGetValue(loc, out Node? p))
            {
                parent = ((Str)p).Value;
            }
            else
            {
                int dash = loc.LastIndexOf('-');
                parent = dash < 0 ? "und" : loc[..dash];
            }

            if (parent == "root")
            {
                parent = "und";
            }

            if (!Locales.Contains(parent))
            {
                throw new InvalidOperationException($"parent {parent} of {loc} is not a stored locale");
            }

            result[loc] = parent;
        }

        return result;
    }

    private Obj LoadLocale(string loc)
    {
        var result = new Obj();
        foreach (var (pkg, file, path, dest) in LocaleSources)
        {
            string p = Path.Combine(json, pkg, "main", loc, file + ".json");
            if (!File.Exists(p))
            {
                continue;
            }

            Node root = TreeOps.FromJson(TreeOps.Load(p));
            Node main = ((Obj)TreeOps.Dig(root, "main")!).Members.Values.Single();
            Node? sub = TreeOps.Dig(main, path);
            if (sub is null)
            {
                continue;
            }

            TreeOps.DropNonLatinNumberSystems(sub);
            if (dest == "displayNames")
            {
                // Subdivision names are not part of ECMA-402; languages etc. are stored separately.
                var o = (Obj)sub;
                o.Members.Remove("subdivisions");
                o.Members.Remove("languages");
                o.Members.Remove("scripts");
                o.Members.Remove("territories");
                o.Members.Remove("variants");
            }

            result.Members[dest] = sub;
        }

        return result;
    }

    private Obj BuildSupplemental()
    {
        var sup = new Obj();
        string core = Path.Combine(json, "cldr-core", "supplemental");
        foreach (var (file, path, dest) in SupplementalSources)
        {
            Node root = TreeOps.FromJson(TreeOps.Load(Path.Combine(core, file + ".json")));
            Node? sub = TreeOps.Dig(root, ["supplemental", .. path]);
            if (sub is null)
            {
                throw new InvalidOperationException($"{file}: missing {string.Join('.', path)}");
            }

            if (dest is "dayPeriodRules" or "dayPeriodSelectionRules")
            {
                sub = KeepLanguages(sub);
            }

            sup.Members[dest] = sub;
        }

        // Locales that have a blob, and CLDR's default-content list as a set (value is unused).
        var stored = new Obj();
        foreach (string loc in Locales)
        {
            stored.Members[loc] = new Str("");
        }

        sup.Members["locales"] = stored;
        Node dc = TreeOps.FromJson(TreeOps.Load(Path.Combine(json, "cldr-core", "defaultContent.json")));
        var dcSet = new Obj();
        foreach (Node n in ((Arr)TreeOps.Dig(dc, "defaultContent")!).Items)
        {
            dcSet.Members[((Str)n).Value] = new Str("");
        }

        sup.Members["defaultContent"] = dcSet;

        // Scripts written right to left (Intl.Locale text info).
        Node sm = TreeOps.FromJson(TreeOps.Load(Path.Combine(json, "cldr-core", "scriptMetadata.json")));
        var rtl = new Obj();
        foreach (var (script, info) in ((Obj)TreeOps.Dig(sm, "scriptMetadata")!).Members)
        {
            if (TreeOps.Dig(info, "rtl") is Str { Value: "YES" })
            {
                rtl.Members[script] = new Str("");
            }
        }

        sup.Members["rtlScripts"] = rtl;

        var bcp = new Obj();
        foreach (string f in Bcp47Files)
        {
            Node n = TreeOps.FromJson(TreeOps.Load(Path.Combine(json, "cldr-bcp47", "bcp47", f + ".json")));
            bcp.Members[f] = TreeOps.Dig(n, "keyword", "u")!;
        }

        sup.Members["bcp47"] = bcp;
        sup.Members["zoneRegions"] = ZoneRegions(bcp);
        sup.Members["collationTailoring"] = Tailorings();
        return sup;
    }

    // Country of each CLDR canonical time zone id, from the IANA zone.tab (the first alias present in the table decides). Time zone
    // display names use it to tell a country's only or primary zone from its other zones.
    private Obj ZoneRegions(Obj bcp)
    {
        var tab = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(tzdata, "zone.tab")))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] cols = line.Split('	');
            tab[cols[2]] = cols[0];
        }

        var result = new Obj();
        Obj tz = (Obj)((Obj)bcp.Members["timezone"]).Members["tz"];
        foreach (var (key, value) in tz.Members)
        {
            if (key.StartsWith('_') || value is not Obj entry || !entry.Members.TryGetValue("_alias", out Node? aliasNode) || aliasNode is not Str alias)
            {
                continue;
            }

            string[] ids = alias.Value.Split(' ');
            foreach (string id in ids)
            {
                if (tab.TryGetValue(id, out string? region))
                {
                    result.Members[ids[0]] = new Str(region);
                    break;
                }
            }
        }

        return result;
    }

    // Day period rules are keyed by language; keep the languages we have locale data for.
    private static Obj KeepLanguages(Node n)
    {
        var keep = new HashSet<string>(Locales.Where(l => !l.Contains('-')).Append("root"), StringComparer.Ordinal);
        var o = new Obj();
        foreach (var (k, v) in ((Obj)n).Members)
        {
            if (keep.Contains(k))
            {
                o.Members[k] = v;
            }
        }

        return o;
    }

    // Rule text of each non-draft, non-alternate <collation> of the six locales' languages, comments removed.
    private Obj Tailorings()
    {
        var result = new Obj();
        foreach (string lang in CollationLanguages)
        {
            string path = Path.Combine(common, "collation", lang + ".xml");
            if (!File.Exists(path))
            {
                continue;
            }

            var perType = new Obj();
            var doc = XDocument.Load(path, LoadOptions.None);
            foreach (XElement c in doc.Descendants("collation"))
            {
                if (c.Attribute("alt") is not null)
                {
                    continue;
                }

                string type = (string?)c.Attribute("type") ?? "standard";

                // Of the root file only the search rules are kept; eor, emoji and the Han tailorings are not supported collations.
                if (lang == "root" && type != "search")
                {
                    continue;
                }

                string? rules = c.Element("cr")?.Value;
                if (rules is null)
                {
                    continue;
                }

                var lines = rules.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#');
                perType.Members[type] = new Str(string.Join('\n', lines));
            }

            if (perType.Members.Count > 0)
            {
                result.Members[lang] = perType;
            }
        }

        return result;
    }
}
