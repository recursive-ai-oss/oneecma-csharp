using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>Inputs for <see cref="DisplayNamesService"/> (Intl.DisplayNames constructor options).</summary>
internal sealed record DisplayNamesOptions
{
    /// <summary><c>style</c>: <c>long</c> (default), <c>short</c>, <c>narrow</c>.</summary>
    internal string? Style { get; init; }

    /// <summary><c>localeMatcher</c>: <c>lookup</c> or <c>best fit</c>.</summary>
    internal string? LocaleMatcher { get; init; }

    /// <summary><c>type</c>: <c>language</c>, <c>region</c>, <c>script</c>, <c>currency</c>, <c>calendar</c>, <c>dateTimeField</c>; required.</summary>
    internal string? Type { get; init; }

    /// <summary><c>fallback</c>: <c>code</c> (default) or <c>none</c>.</summary>
    internal string? Fallback { get; init; }

    /// <summary><c>languageDisplay</c>: <c>dialect</c> (default) or <c>standard</c>; only for the language type.</summary>
    internal string? LanguageDisplay { get; init; }
}

/// <summary>Intl.DisplayNames (ECMA-402 section 12) over CLDR display name data.</summary>
internal sealed class DisplayNamesService
{
    private static readonly string[] FieldNames =
        ["era", "year", "quarter", "month", "weekOfYear", "weekday", "day", "dayPeriod", "hour", "minute", "second", "timeZoneName"];

    private static readonly string[] FieldKeys =
        ["era", "year", "quarter", "month", "week", "weekday", "day", "dayperiod", "hour", "minute", "second", "zone"];

    private readonly DataTree _data;

    private DisplayNamesService(string locale, string dataLocale, string style, string type, string fallback, string? languageDisplay)
    {
        Locale = locale;
        Style = style;
        Type = type;
        Fallback = fallback;
        LanguageDisplay = languageDisplay;
        _data = LocaleResolver.DataFor(dataLocale);
    }

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; }

    /// <summary>The resolved style.</summary>
    internal string Style { get; }

    /// <summary>The resolved type.</summary>
    internal string Type { get; }

    /// <summary>The resolved fallback.</summary>
    internal string Fallback { get; }

    /// <summary>The resolved language display, only for type <c>language</c>.</summary>
    internal string? LanguageDisplay { get; }

    /// <summary>Creates the service (12.1.1).</summary>
    /// <exception cref="IntlException">A missing type (TypeError) or an option out of range (RangeError).</exception>
    internal static DisplayNamesService Create(IReadOnlyList<string> locales, DisplayNamesOptions o, string defaultLocale)
    {
        LocaleResolver.ValidateLocaleMatcher(o.LocaleMatcher, "DisplayNames");
        string style = o.Style ?? "long";
        if (style is not ("long" or "short" or "narrow"))
        {
            throw IntlException.Range($"Value {style} out of range for Intl.DisplayNames options property style");
        }

        ResolvedLocale r = LocaleResolver.Resolve(locales, [], new Dictionary<string, string?>(), (_, _) => [], defaultLocale);
        string type = o.Type ?? throw IntlException.Type("invalid_argument");
        if (type is not ("language" or "region" or "script" or "currency" or "calendar" or "dateTimeField"))
        {
            throw IntlException.Range($"Value {type} out of range for Intl.DisplayNames options property type");
        }

        string fallback = o.Fallback ?? "code";
        if (fallback is not ("code" or "none"))
        {
            throw IntlException.Range($"Value {fallback} out of range for Intl.DisplayNames options property fallback");
        }

        string ld = o.LanguageDisplay ?? "dialect";
        if (ld is not ("dialect" or "standard"))
        {
            throw IntlException.Range($"Value {ld} out of range for Intl.DisplayNames options property languageDisplay");
        }

        return new DisplayNamesService(r.Locale, r.DataLocale, style, type, fallback, type == "language" ? ld : null);
    }

    /// <summary>
    /// Of (12.4.? ): the display name of <paramref name="code"/>, or null (JavaScript undefined) when there
    /// is none and the fallback is <c>none</c>.
    /// </summary>
    /// <exception cref="IntlException">The code is not valid for the type (RangeError).</exception>
    internal string? Of(string code)
    {
        switch (Type)
        {
            case "language":
                return OfLanguage(code);
            case "region":
            {
                if (!LocaleId.IsRegionSubtag(code))
                {
                    throw IntlException.Range("invalid_argument");
                }

                string region = code.ToUpperInvariantAscii();
                return Pick(RegionName(region), region);
            }

            case "script":
            {
                if (!LocaleId.IsScriptSubtag(code))
                {
                    throw IntlException.Range("invalid_argument");
                }

                string script = code[..1].ToUpperInvariantAscii() + code[1..].ToLowerInvariantAscii();
                return Pick(ScriptName(script), script);
            }

            case "currency":
            {
                if (code.Length != 3 || !code.All(c => (uint)((c | 0x20) - 'a') <= 25))
                {
                    throw IntlException.Range("invalid_argument");
                }

                string cur = code.ToUpperInvariantAscii();
                string? n = _data.GetString("currencies", cur, "displayName");
                return Pick(n is null or DataTree.EmptyMarker ? null : n, cur);
            }

            case "calendar":
                return OfCalendar(code);
            default:
                return OfField(code);
        }
    }

    private string? Pick(string? name, string code) => name ?? (Fallback == "code" ? code : null);

    private string? Alt(string table, string key)
    {
        if (Style != "long")
        {
            string? shortName = _data.GetString(table, key + "-alt-short");
            if (shortName is not null)
            {
                return shortName;
            }
        }

        return _data.GetString(table, key);
    }

    private string? RegionName(string region) => Alt("territories", region);

    private string? ScriptName(string script) => Alt("scripts", script);

    private string? OfCalendar(string code)
    {
        // type = alphanum{3,8} (-alphanum{3,8})*
        string[] subtags = code.Split('-');
        if (code.Length == 0 || subtags.Any(s => s.Length is < 3 or > 8 || !s.All(c => char.IsAsciiLetterOrDigit(c))))
        {
            throw IntlException.Range("invalid_argument");
        }

        string canonical = KeywordAliases.CanonicalType("ca", code.ToLowerInvariantAscii());
        string? name = _data.GetString("displayNames", "types", "calendar", canonical);
        if (name is null)
        {
            // CLDR names a few calendars by their long BCP 47 alias (gregory is "gregorian").
            string? alias = CldrData.Supplemental.GetString("bcp47", "calendar", "ca", canonical, "_alias");
            if (alias is not null)
            {
                name = _data.GetString("displayNames", "types", "calendar", alias.Split(' ')[0]);
            }
        }

        return Pick(name, canonical);
    }

    private string? OfField(string code)
    {
        int i = Array.IndexOf(FieldNames, code);
        if (i < 0)
        {
            throw IntlException.Range("invalid_argument");
        }

        string key = FieldKeys[i];
        string field = Style == "long" ? key : key + "-" + Style;
        string? name = _data.GetString("dateFields", field, "displayName")
            ?? (Style == "narrow" ? _data.GetString("dateFields", key + "-short", "displayName") : null)
            ?? _data.GetString("dateFields", key, "displayName");
        return Pick(name, code);
    }

    private string? OfLanguage(string code)
    {
        LocaleId id = LocaleId.Parse(code) ?? throw IntlException.Range("invalid_argument");
        if (id.HasUnicodeExtension || id.HasTransformExtension || id.OtherExtensions.Count > 0 || id.PrivateUse is not null)
        {
            throw IntlException.Range("invalid_argument");
        }

        id.Canonicalize();
        string canonical = id.ToString();
        bool dialect = LanguageDisplay == "dialect";

        string? whole = null;
        if (id.Language != "und")
        {
            if (dialect && (id.Script is not null || id.Region is not null))
            {
                whole = Alt("languages", id.Language + (id.Script is null ? "" : "-" + id.Script) + (id.Region is null ? "" : "-" + id.Region));
            }

            if (whole is not null && id.Variants.Count == 0)
            {
                return whole;
            }
        }

        string? language = id.Language == "und" ? null : whole is not null ? whole : Alt("languages", id.Language);
        if (language is null && id.Language != "und" && Fallback == "none" && id.Script is null && id.Region is null && id.Variants.Count == 0)
        {
            return null;
        }

        if (language is null && id.Language == "und")
        {
            if (Fallback == "none")
            {
                return null;
            }

            language = "root";
        }

        var extra = new List<string>();
        bool missing = language is null;
        language ??= id.Language;
        if (whole is null)
        {
            if (id.Script is not null)
            {
                string? s = ScriptName(id.Script);
                missing |= s is null;
                extra.Add(s ?? id.Script);
            }

            if (id.Region is not null)
            {
                string? r = RegionName(id.Region);
                missing |= r is null;
                extra.Add(r ?? id.Region);
            }
        }

        if (id.Variants.Count == 1)
        {
            string? v = _data.GetString("variants", id.Variants[0].ToUpperInvariantAscii());
            missing |= v is null;
            extra.Add(v ?? id.Variants[0].ToUpperInvariantAscii());
        }
        else if (id.Variants.Count > 1)
        {
            // Several variants have no combined name; the codes are joined as the data layer does.
            missing = true;
            extra.Add(string.Join('_', id.Variants.Select(v => v.ToUpperInvariantAscii())));
        }

        if (missing && Fallback == "none")
        {
            return null;
        }

        if (extra.Count == 0)
        {
            return missing ? canonical : language;
        }

        string separator = _data.GetString("displayNames", "localeDisplayPattern", "localeSeparator") ?? "{0}, {1}";
        string pattern = _data.GetString("displayNames", "localeDisplayPattern", "localePattern") ?? "{0} ({1})";
        string joined = extra[0];
        for (int i = 1; i < extra.Count; i++)
        {
            joined = separator.Replace("{0}", joined, StringComparison.Ordinal).Replace("{1}", extra[i], StringComparison.Ordinal);
        }

        return pattern.Replace("{0}", language, StringComparison.Ordinal).Replace("{1}", joined, StringComparison.Ordinal);
    }
}
