using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>A part of a relative time string; numeric parts carry the unit.</summary>
internal readonly record struct RelativeTimePart(string Type, string Value, string? Unit);

/// <summary>Intl.RelativeTimeFormat (ECMA-402 section 17) over CLDR date field patterns.</summary>
internal sealed class RelativeTimeFormatService
{
    private static readonly string[] Units = ["second", "minute", "hour", "day", "week", "month", "quarter", "year"];

    private readonly DataTree _data;
    private readonly NumberFormatService _numbers;
    private readonly PluralRulesService _plural;

    private RelativeTimeFormatService(string locale, string dataLocale, string style, string numeric)
    {
        Locale = locale;
        Style = style;
        Numeric = numeric;
        _data = LocaleResolver.DataFor(dataLocale);
        _numbers = NumberFormatService.Create([locale], new NumberFormatOptions(), dataLocale);
        _plural = PluralRulesService.Create([locale], new PluralRulesOptions(), dataLocale);
    }

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; }

    /// <summary>The resolved style: <c>long</c>, <c>short</c> or <c>narrow</c>.</summary>
    internal string Style { get; }

    /// <summary>The resolved numeric option: <c>always</c> or <c>auto</c>.</summary>
    internal string Numeric { get; }

    /// <summary>The resolved numbering system (always <c>latn</c>).</summary>
    internal static string NumberingSystem => "latn";

    /// <summary>Creates the service (17.1.1).</summary>
    /// <exception cref="IntlException">An option is out of range.</exception>
    internal static RelativeTimeFormatService Create(IReadOnlyList<string> locales, string? style, string? numeric, string defaultLocale)
    {
        style ??= "long";
        numeric ??= "always";
        if (style is not ("long" or "short" or "narrow"))
        {
            throw IntlException.Range($"Value {style} out of range for Intl.RelativeTimeFormat options property style");
        }

        if (numeric is not ("always" or "auto"))
        {
            throw IntlException.Range($"Value {numeric} out of range for Intl.RelativeTimeFormat options property numeric");
        }

        ResolvedLocale r = LocaleResolver.Resolve(locales, ["nu"], new Dictionary<string, string?>(), (_, _) => ["latn"], defaultLocale);
        return new RelativeTimeFormatService(r.Locale, r.DataLocale, style, numeric);
    }

    /// <summary>SingularRelativeTimeUnit (17.5.?): accepts <c>day</c> and <c>days</c> alike.</summary>
    /// <exception cref="IntlException">Not a time unit (RangeError).</exception>
    internal static string SingularUnit(string unit)
    {
        string u = unit.EndsWith('s') ? unit[..^1] : unit;
        return Array.IndexOf(Units, u) >= 0 ? u : throw IntlException.Range($"Invalid unit argument for format() '{unit}'");
    }

    /// <summary>The formatted string for <paramref name="value"/> units.</summary>
    /// <exception cref="IntlException">The value is not finite or the unit is invalid (RangeError).</exception>
    internal string Format(DecimalNumber value, string unit) => string.Concat(FormatToParts(value, unit).Select(p => p.Value));

    /// <summary>PartitionRelativeTimePattern (17.5.?).</summary>
    /// <exception cref="IntlException">The value is not finite or the unit is invalid (RangeError).</exception>
    internal List<RelativeTimePart> FormatToParts(DecimalNumber value, string unit)
    {
        string u = SingularUnit(unit);
        if (!value.IsFinite)
        {
            throw IntlException.Range("Value need to be finite number for Intl.RelativeTimeFormat.prototype.format()");
        }

        string field = Style == "long" ? u : u + "-" + Style;
        if (Numeric == "auto")
        {
            // Values within one percent of -2, -1, 0, 1 or 2 count as those offsets, as the reference formatter does.
            double offset = value.ToDouble();
            string key = "relative-type-" + (value.IsZero ? "0" : value.ToString());
            if (offset > -2.1 && offset < 2.1)
            {
                double x100 = offset * 100.0;
                int near = (int)(x100 < 0 ? x100 - 0.5 : x100 + 0.5);
                if (near is -200 or -100 or 0 or 100 or 200)
                {
                    key = "relative-type-" + (near / 100).ToString(System.Globalization.NumberFormatInfo.InvariantInfo);
                }
            }

            string? text = _data.GetString("dateFields", field, key) ?? (Style == "narrow" ? _data.GetString("dateFields", u + "-short", key) : null);
            if (text is not null)
            {
                return [new RelativeTimePart("literal", text, null)];
            }
        }

        bool past = value.IsNegative; // negative zero counts as past (17.5.? step "if value is -0")
        string tense = past ? "past" : "future";
        DecimalNumber abs = value.Abs();
        // Plural category of the number as it will be displayed.
        List<NumberPart> numberParts = _numbers.FormatToParts(abs);
        string cat = PluralCategoryName(abs);
        string? pattern = _data.GetString("dateFields", field, "relativeTime-type-" + tense, "relativeTimePattern-count-" + cat)
            ?? _data.GetString("dateFields", field, "relativeTime-type-" + tense, "relativeTimePattern-count-other");
        if (pattern is null && Style != "long")
        {
            string alt = Style == "narrow" ? u + "-short" : u;
            pattern = _data.GetString("dateFields", alt, "relativeTime-type-" + tense, "relativeTimePattern-count-" + cat)
                ?? _data.GetString("dateFields", alt, "relativeTime-type-" + tense, "relativeTimePattern-count-other");
        }

        pattern ??= "{0}";
        var parts = new List<RelativeTimePart>();
        int at = pattern.IndexOf("{0}", StringComparison.Ordinal);
        if (at > 0)
        {
            parts.Add(new RelativeTimePart("literal", pattern[..at], null));
        }

        foreach (NumberPart p in numberParts)
        {
            parts.Add(new RelativeTimePart(p.Type, p.Value, u));
        }

        if (at + 3 < pattern.Length)
        {
            parts.Add(new RelativeTimePart("literal", pattern[(at + 3)..], null));
        }

        return parts;
    }

    private string PluralCategoryName(DecimalNumber abs) => _plural.Select(abs);
}
