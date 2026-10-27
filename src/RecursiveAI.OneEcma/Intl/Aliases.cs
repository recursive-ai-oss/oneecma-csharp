using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>
/// CLDR alias data for locale canonicalization (UTS #35 Part 1, "Canonical Unicode Locale Identifiers",
/// and supplemental metadata <c>alias</c>), indexed once on first use.
/// </summary>
internal static class Aliases
{
    private sealed record LangEntry(string Language, string? Script, string? Region, string[] Variants, LocaleId Replacement)
    {
        internal int Specificity => 1 + (Script is null ? 0 : 1) + (Region is null ? 0 : 1) + Variants.Length;
    }

    private static readonly Lazy<Dictionary<string, List<LangEntry>>> LangIndex = new(BuildLanguage);

    /// <summary>Script aliases keyed by lowercase source subtag.</summary>
    internal static Dictionary<string, string> Script { get; } = Load("scriptAlias", lower: true);

    /// <summary>Region aliases keyed by lowercase source subtag; the value may list several regions.</summary>
    internal static Dictionary<string, string> Territory { get; } = Load("territoryAlias", lower: true);

    /// <summary>Variant aliases keyed by source subtag.</summary>
    internal static Dictionary<string, string> Variant { get; } = Load("variantAlias", lower: true);

    /// <summary>Subdivision aliases (for <c>-u-sd-</c> and <c>-u-rg-</c> values).</summary>
    internal static Dictionary<string, string> Subdivision { get; } = Load("subdivisionAlias", lower: true);

    private static Dictionary<string, string> Load(string kind, bool lower)
    {
        DataTree sup = CldrData.Supplemental;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in sup.GetKeys("aliases", kind))
        {
            string? repl = sup.GetString("aliases", kind, key, "_replacement");
            if (repl is not null)
            {
                map[lower ? key.ToLowerInvariantAscii() : key] = repl;
            }
        }

        return map;
    }

    private static Dictionary<string, List<LangEntry>> BuildLanguage()
    {
        DataTree sup = CldrData.Supplemental;
        var index = new Dictionary<string, List<LangEntry>>(StringComparer.Ordinal);
        foreach (string key in sup.GetKeys("aliases", "languageAlias"))
        {
            string? repl = sup.GetString("aliases", "languageAlias", key, "_replacement");
            LocaleId? k = LocaleId.Parse(key);
            LocaleId? r = repl is null ? null : LocaleId.Parse(repl);
            if (k is null || r is null)
            {
                // Legacy forms such as "i-klingon" are not valid unicode_locale_ids; ECMA-402 never accepts them.
                continue;
            }

            var e = new LangEntry(k.Language, k.Script, k.Region, [.. k.Variants], r);
            if (!index.TryGetValue(e.Language, out List<LangEntry>? list))
            {
                index[e.Language] = list = [];
            }

            list.Add(e);
        }

        return index;
    }

    /// <summary>
    /// Replaces the language (together with the script, region and variants the alias key names) by the
    /// most specific matching alias (UTS #35 Annex C, step "language alias").
    /// </summary>
    internal static void ReplaceLanguageAliases(LocaleId id)
    {
        Dictionary<string, List<LangEntry>> index = LangIndex.Value;
        LangEntry? best = null;
        foreach (string lang in new[] { id.Language, "und" })
        {
            if (!index.TryGetValue(lang, out List<LangEntry>? list))
            {
                continue;
            }

            foreach (LangEntry e in list)
            {
                if (e.Language == "und" && e.Specificity == 1)
                {
                    continue;
                }

                if ((e.Script is null || e.Script == id.Script)
                    && (e.Region is null || e.Region == id.Region)
                    && e.Variants.All(id.Variants.Contains)
                    && (best is null || e.Specificity > best.Specificity))
                {
                    best = e;
                }
            }
        }

        if (best is null)
        {
            return;
        }

        LocaleId r = best.Replacement;
        if (r.Language != "und")
        {
            id.Language = r.Language;
        }

        if (best.Script is not null)
        {
            id.Script = null;
        }

        if (best.Region is not null)
        {
            id.Region = null;
        }

        foreach (string v in best.Variants)
        {
            id.Variants.Remove(v);
        }

        id.Script ??= r.Script;
        id.Region ??= r.Region;
        foreach (string v in r.Variants)
        {
            if (!id.Variants.Contains(v))
            {
                id.Variants.Add(v);
            }
        }
    }

    /// <summary>
    /// For a region alias that lists several successors (such as <c>SU</c>), picks the one the language's
    /// likely subtags name, else the first (UTS #35 Annex C, "territory alias").
    /// </summary>
    internal static string PickRegion(LocaleId id, string replacement)
    {
        string[] options = replacement.Split(' ');
        if (options.Length == 1)
        {
            return options[0];
        }

        string? region = new LocaleId { Language = id.Language, Script = id.Script }.Maximize().Region;
        return region is not null && Array.IndexOf(options, region) >= 0 ? region : options[0];
    }
}

/// <summary>
/// BCP 47 keyword type aliases from CLDR's <c>bcp47</c> data: <c>-u-ca-islamicc</c> becomes
/// <c>islamic-civil</c>, <c>-u-ks-primary</c> becomes <c>level1</c>, and so on.
/// </summary>
internal static class KeywordAliases
{
    private static readonly Dictionary<string, Dictionary<string, string>> ByKey = new(StringComparer.Ordinal);

    private static readonly string[] Files = ["calendar", "collation", "currency", "measure", "number", "timezone"];

    internal static string CanonicalType(string key, string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        if (key is "sd" or "rg")
        {
            string sd = value;
            return Aliases.Subdivision.TryGetValue(sd, out string? r) ? r.Split(' ')[0] : sd;
        }

        Dictionary<string, string> map = Aliases2(key);
        return map.TryGetValue(value, out string? c) ? c : value;
    }

    internal static string CanonicalTransformValue(string value) => value;

    private static Dictionary<string, string> Aliases2(string key)
    {
        lock (ByKey)
        {
            if (ByKey.TryGetValue(key, out Dictionary<string, string>? m))
            {
                return m;
            }

            m = new Dictionary<string, string>(StringComparer.Ordinal);
            DataTree sup = CldrData.Supplemental;
            foreach (string file in Files)
            {
                foreach (string type in sup.GetKeys("bcp47", file, key))
                {
                    if (type.StartsWith('_'))
                    {
                        continue;
                    }

                    string? alias = sup.GetString("bcp47", file, key, type, "_alias");
                    if (alias is not null)
                    {
                        foreach (string a in alias.Split(' '))
                        {
                            m[a.ToLowerInvariantAscii()] = type;
                        }
                    }

                    string? preferred = sup.GetString("bcp47", file, key, type, "_preferred");
                    if (preferred is not null)
                    {
                        m[type] = preferred;
                    }
                }
            }

            ByKey[key] = m;
            return m;
        }
    }
}
