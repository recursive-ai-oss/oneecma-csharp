using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>Inputs for <see cref="PluralRulesService"/> (the Intl.PluralRules constructor options).</summary>
internal sealed record PluralRulesOptions
{
    /// <summary><c>type</c>: <c>cardinal</c> (default) or <c>ordinal</c>.</summary>
    internal string? Type { get; init; }

    /// <summary><c>localeMatcher</c>: <c>lookup</c> or <c>best fit</c>.</summary>
    internal string? LocaleMatcher { get; init; }

    /// <summary><c>notation</c>: <c>standard</c> (default) or <c>compact</c>; compact reports the exponent as the plural operand <c>c</c>.</summary>
    internal string? Notation { get; init; }

    /// <summary><c>compactDisplay</c> (short or long); only used with compact notation.</summary>
    internal string? CompactDisplay { get; init; }

    /// <summary>The digit options that shape the number whose plural form is chosen.</summary>
    internal DigitOptionInputs Digits { get; init; } = new();
}

/// <summary>
/// Intl.PluralRules (ECMA-402 section 16): ResolvePlural and ResolvePluralRange over the CLDR plural
/// rules (UTS #35 Part 3) and plural range data.
/// </summary>
internal sealed class PluralRulesService
{
    private static readonly string[] CategoryNames = ["zero", "one", "two", "few", "many", "other"];

    private readonly string _ruleLocale;
    private readonly PluralType _type;
    private readonly DataTree _supplemental = CldrData.Supplemental;

    private readonly NumberFormatService? _compact;

    private PluralRulesService(string locale, string dataLocale, PluralType type, DigitOptions digits, NumberFormatService? compact)
    {
        _compact = compact;
        Locale = locale;
        _ruleLocale = dataLocale;
        _type = type;
        Digits = digits;
    }

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; }

    /// <summary>The resolved type, <c>cardinal</c> or <c>ordinal</c>.</summary>
    internal string Type => _type == PluralType.Cardinal ? "cardinal" : "ordinal";

    /// <summary>The resolved <c>notation</c>.</summary>
    internal string Notation { get; private init; } = "standard";

    /// <summary>The resolved <c>compactDisplay</c>, or null unless the notation is compact.</summary>
    internal string? CompactDisplay { get; private init; }

    /// <summary>The resolved digit options.</summary>
    internal DigitOptions Digits { get; }

    /// <summary>The categories the locale distinguishes, in CLDR order (zero, one, two, few, many, other).</summary>
    internal string[] PluralCategories => [.. CldrData.Plurals.Categories(_ruleLocale, _type).Select(c => CategoryNames[(int)c])];

    /// <summary>Creates the service (the Intl.PluralRules constructor, 16.1.1).</summary>
    /// <param name="locales">Requested locales, already canonicalized.</param>
    /// <param name="options">Constructor options.</param>
    /// <param name="defaultLocale">The host's default locale.</param>
    /// <exception cref="IntlException">An option is out of range.</exception>
    internal static PluralRulesService Create(IReadOnlyList<string> locales, PluralRulesOptions options, string defaultLocale)
    {
        LocaleResolver.ValidateLocaleMatcher(options.LocaleMatcher, "PluralRules");
        string type = options.Type ?? "cardinal";
        if (type is not ("cardinal" or "ordinal"))
        {
            throw IntlException.Range($"Value {type} out of range for Intl.PluralRules options property type");
        }

        ResolvedLocale r = LocaleResolver.Resolve(locales, [], new Dictionary<string, string?>(), (_, _) => [], defaultLocale);
        string notation = options.Notation ?? "standard";
        if (notation is not ("standard" or "scientific" or "engineering" or "compact"))
        {
            throw IntlException.Range($"Value {notation} out of range for Intl.PluralRules options property notation");
        }

        // Compact notation scales the number the way NumberFormat does; that service supplies the exponent.
        NumberFormatService? compact = notation == "compact"
            ? NumberFormatService.Create(locales, new NumberFormatOptions { Notation = "compact", CompactDisplay = options.CompactDisplay, Digits = options.Digits }, defaultLocale)
            : null;
        DigitOptions digits = compact?.Digits ?? DigitOptions.Resolve(options.Digits, 0, 3, compactNotation: false);
        string? compactDisplay = options.CompactDisplay ?? "short";
        return new PluralRulesService(r.Locale, r.DataLocale, type == "cardinal" ? PluralType.Cardinal : PluralType.Ordinal, digits, compact)
        {
            Notation = notation,
            CompactDisplay = notation == "compact" ? compactDisplay : null,
        };
    }

    /// <summary>The category name for a number (PluralRuleSelect after FormatNumericToString).</summary>
    internal string Select(DecimalNumber n)
    {
        if (!n.IsFinite)
        {
            return "other";
        }

        if (_compact is not null)
        {
            return CategoryNames[(int)_compact.CompactCategory(n, _ruleLocale, _type)];
        }

        RoundedDigits d = Digits.Round(n.Abs(), n.IsNegative);
        return CategoryNames[(int)SelectDigits(d, 0)];
    }

    /// <summary>
    /// The category for rounded digits and a compact exponent (the <c>c</c>/<c>e</c> operand). NumberFormat
    /// uses this to choose compact and currency-name forms.
    /// </summary>
    internal PluralCategory SelectDigits(RoundedDigits d, int compactExponent) =>
        SelectDigits(CldrData.Plurals, _ruleLocale, _type, d, compactExponent);

    internal static PluralCategory SelectDigits(PluralData data, string locale, PluralType type, RoundedDigits d, int compactExponent, bool shift = false)
    {
        // NumberFormat keeps the displayed digits (1.2 for 1.2M) and reports the exponent in the c/e operand only,
        // which is how it picks the compact pattern. PluralRules.select shifts the digits as UTS 35 defines (1200000).
        PluralOperands ops = PluralOperands.FromDigits(d.Integer, d.Fraction, compactExponent, shift);
        return data.Select(locale, type, ops);
    }

    /// <summary>ResolvePluralRange (16.5.?): the category for the range <paramref name="x"/> to <paramref name="y"/>.</summary>
    /// <exception cref="IntlException">Either is NaN (RangeError).</exception>
    internal string SelectRange(DecimalNumber x, DecimalNumber y)
    {
        if (x.IsNaN || y.IsNaN)
        {
            throw IntlException.Range("Invalid number value: NaN");
        }

        string start = Select(x);
        string end = Select(y);
        string lang = _ruleLocale.Split('-')[0];
        string? result = _supplemental.GetString("pluralRanges", _ruleLocale, $"pluralRange-start-{start}-end-{end}")
            ?? _supplemental.GetString("pluralRanges", lang, $"pluralRange-start-{start}-end-{end}");
        // Pairs the data does not list select "other" (the range formatter default).
        return result ?? "other";
    }
}
