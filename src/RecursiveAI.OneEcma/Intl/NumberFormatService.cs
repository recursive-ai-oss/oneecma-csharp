using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>
/// Intl.NumberFormat (ECMA-402 section 15, including the v3 features): option resolution, PartitionNumberPattern,
/// FormatNumericToString and range formatting, over CLDR number, currency and unit data.
/// </summary>
internal sealed partial class NumberFormatService
{
    private static readonly string[] CategoryNames = ["zero", "one", "two", "few", "many", "other"];

    /// <summary>The sanctioned simple units of ECMA-402 (table "Sanctioned simple unit identifiers"), sorted.</summary>
    internal static readonly string[] SanctionedUnits =
    [
        "acre", "bit", "byte", "celsius", "centimeter", "day", "degree", "fahrenheit", "fluid-ounce", "foot", "gallon",
        "gigabit", "gigabyte", "gram", "hectare", "hour", "inch", "kilobit", "kilobyte", "kilogram", "kilometer", "liter",
        "megabit", "megabyte", "meter", "microsecond", "mile", "mile-scandinavian", "milliliter", "millimeter",
        "millisecond", "minute", "month", "nanosecond", "ounce", "percent", "petabyte", "pound", "second", "stone",
        "terabit", "terabyte", "week", "yard", "year",
    ];

    private readonly DataTree _data;
    private readonly string _dataLocale;
    private readonly NumberSymbols _symbols;
    private readonly NumberPattern _basePattern;
    private readonly NumberPattern _percentPattern;
    private readonly string _groupingMode; // "false", "auto", "always" or "min2"
    private readonly Layout?[] _layouts = new Layout?[6];
    private readonly string? _currencySymbolText;
    private readonly string _unitWidth;
    private readonly string _numberNs;

    private sealed record Layout(Tok[] Prefix, Tok[] Suffix, Tok[]? NegPrefix, Tok[]? NegSuffix);

    private sealed record NumberSymbols(string Decimal, string Group, string Plus, string Minus, string Percent, string Exponential, string Infinity, string NaN, string Approximately, int MinGrouping);

    private NumberFormatService(string locale, string dataLocale, NumberFormatOptions o, DigitOptions digits)
    {
        Locale = locale;
        _dataLocale = dataLocale;
        _data = LocaleResolver.DataFor(dataLocale);
        Digits = digits;
        NumberingSystem = "latn";
        _numberNs = "-numberSystem-latn";
        Style = o.Style ?? "decimal";
        Notation = o.Notation ?? "standard";
        CompactDisplay = o.CompactDisplay ?? "short";
        SignDisplay = o.SignDisplay ?? "auto";
        string def = Notation == "compact" ? "min2" : "auto";
        string ug = o.UseGroupingFlag is { } flag ? (flag ? "always" : "false") : o.UseGrouping is null or "true" or "false" ? def : o.UseGrouping;
        UseGrouping = ug;
        _groupingMode = ug;
        _symbols = LoadSymbols();
        Currency = o.Currency;
        CurrencyDisplay = Style == "currency" ? o.CurrencyDisplay ?? "symbol" : null;
        CurrencySign = Style == "currency" ? o.CurrencySign ?? "standard" : null;
        Unit = o.Unit;
        UnitDisplay = Style == "unit" ? o.UnitDisplay ?? "short" : null;
        _unitWidth = UnitDisplay ?? "short";
        if (Style == "currency")
        {
            _currencySymbolText = CurrencyDisplay switch
            {
                "code" => Currency,
                "narrowSymbol" => CurrencyText(Currency!, "symbol-alt-narrow") ?? CurrencyText(Currency!, "symbol") ?? Currency,
                "name" => null,
                _ => CurrencyText(Currency!, "symbol") ?? Currency,
            };
        }

        string patternKey = Style switch
        {
            "percent" => "percentFormats",
            "currency" when CurrencyDisplay != "name" => "currencyFormats",
            _ => "decimalFormats",
        };
        string standardName = Style == "currency" && CurrencyDisplay != "name" && CurrencySign == "accounting" ? "accounting" : "standard";
        string? pat = _data.GetString("numbers", patternKey + _numberNs, standardName);
        _basePattern = NumberPattern.Parse(pat ?? "#,##0.###");
        _percentPattern = NumberPattern.Parse(_data.GetString("numbers", "percentFormats" + _numberNs, "standard") ?? "#,##0%");
        if (Notation is "scientific" or "engineering")
        {
            _basePattern = MergeSci(_basePattern);
        }
    }

    // Scientific notation keeps the style's affixes (currency, percent) but not grouping.
    private static NumberPattern MergeSci(NumberPattern p) => p;

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; }

    /// <summary>The resolved numbering system (always <c>latn</c>).</summary>
    internal string NumberingSystem { get; }

    /// <summary>The resolved style.</summary>
    internal string Style { get; }

    /// <summary>The resolved currency code (uppercase), or null.</summary>
    internal string? Currency { get; }

    /// <summary>The resolved currency display, for the currency style.</summary>
    internal string? CurrencyDisplay { get; }

    /// <summary>The resolved currency sign, for the currency style.</summary>
    internal string? CurrencySign { get; }

    /// <summary>The unit, for the unit style.</summary>
    internal string? Unit { get; }

    /// <summary>The unit display, for the unit style.</summary>
    internal string? UnitDisplay { get; }

    /// <summary>The resolved notation.</summary>
    internal string Notation { get; }

    /// <summary>The resolved compact display (reported only for compact notation).</summary>
    internal string CompactDisplay { get; }

    /// <summary>The resolved sign display.</summary>
    internal string SignDisplay { get; }

    /// <summary>The resolved <c>useGrouping</c>: <c>auto</c>, <c>always</c>, <c>min2</c> or <c>false</c>.</summary>
    internal string UseGrouping { get; }

    /// <summary>The resolved digit options.</summary>
    internal DigitOptions Digits { get; }

    /// <summary>
    /// Creates a formatter (the Intl.NumberFormat constructor, 15.1.2 InitializeNumberFormat).
    /// </summary>
    /// <param name="locales">Requested locales, already canonicalized.</param>
    /// <param name="options">The constructor options.</param>
    /// <param name="defaultLocale">The host's default locale.</param>
    /// <exception cref="IntlException">An option is invalid (RangeError) or missing (TypeError).</exception>
    internal static NumberFormatService Create(IReadOnlyList<string> locales, NumberFormatOptions options, string defaultLocale)
    {
        ResolvedLocale r = LocaleResolver.Resolve(
            locales,
            ["nu"],
            new Dictionary<string, string?> { ["nu"] = options.NumberingSystem },
            (_, _) => ["latn"],
            defaultLocale);

        string style = Choice(options.Style, "style", "decimal", "percent", "currency", "unit");
        string? currency = options.Currency;
        if (currency is not null)
        {
            if (currency.Length != 3 || !currency.All(c => (uint)((c | 0x20) - 'a') <= 25))
            {
                throw IntlException.Range($"Invalid currency code : {currency}");
            }

            currency = currency.ToUpperInvariantAscii();
        }
        else if (style == "currency")
        {
            throw IntlException.Type("Currency code is required with currency style.");
        }

        if (style == "currency")
        {
            Choice(options.CurrencyDisplay, "currencyDisplay", "code", "symbol", "narrowSymbol", "name");
            Choice(options.CurrencySign, "currencySign", "standard", "accounting");
        }

        if (options.Unit is not null && !IsWellFormedUnit(options.Unit))
        {
            throw IntlException.Range($"Invalid unit argument for Intl.NumberFormat() '{options.Unit}'");
        }

        if (style == "unit")
        {
            if (options.Unit is null)
            {
                throw IntlException.Type("Unit is required with unit style.");
            }

            Choice(options.UnitDisplay, "unitDisplay", "short", "narrow", "long");
        }

        string notation = Choice(options.Notation, "notation", "standard", "scientific", "engineering", "compact");
        Choice(options.CompactDisplay, "compactDisplay", "short", "long");
        Choice(options.SignDisplay, "signDisplay", "auto", "never", "always", "exceptZero", "negative");
        if (options.UseGrouping is not null)
        {
            Choice(options.UseGrouping, "useGrouping", "min2", "auto", "always", "true", "false");
        }

        int mnfd, mxfd;
        if (style == "currency")
        {
            int cd = CurrencyDigits(currency!);
            (mnfd, mxfd) = notation == "standard" ? (cd, cd) : (0, 3);
        }
        else
        {
            (mnfd, mxfd) = (0, style == "percent" ? 0 : 3);
        }

        DigitOptions digits = DigitOptions.Resolve(options.Digits, mnfd, mxfd, notation == "compact");
        NumberFormatOptions resolved = options with { Currency = currency, Style = style, Notation = notation };
        return new NumberFormatService(r.Locale, r.DataLocale, resolved, digits);
    }

    private static string Choice(string? value, string name, params string[] allowed)
    {
        value ??= allowed[0];
        if (Array.IndexOf(allowed, value) < 0)
        {
            throw IntlException.Range($"Value {value} out of range for Intl.NumberFormat options property {name}");
        }

        return value;
    }

    /// <summary>IsWellFormedUnitIdentifier (15.? ): a sanctioned simple unit, or two joined by <c>-per-</c>.</summary>
    internal static bool IsWellFormedUnit(string unit)
    {
        if (Array.BinarySearch(SanctionedUnits, unit, StringComparer.Ordinal) >= 0)
        {
            return true;
        }

        int i = unit.IndexOf("-per-", StringComparison.Ordinal);
        if (i <= 0 || unit.IndexOf("-per-", i + 1, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        return Array.BinarySearch(SanctionedUnits, unit[..i], StringComparer.Ordinal) >= 0
            && Array.BinarySearch(SanctionedUnits, unit[(i + 5)..], StringComparer.Ordinal) >= 0;
    }

    /// <summary>The number of fraction digits CLDR gives a currency (ISO 4217 minor units; 2 when unlisted).</summary>
    internal static int CurrencyDigits(string code)
    {
        DataTree sup = CldrData.Supplemental;
        string? d = sup.GetString("currencyData", "fractions", code, "_digits") ?? sup.GetString("currencyData", "fractions", "DEFAULT", "_digits");
        return d is null ? 2 : int.Parse(d, NumberFormatInfo.InvariantInfo);
    }

    private NumberSymbols LoadSymbols()
    {
        string sym = "symbols" + _numberNs;
        string G(string k, string fallback)
        {
            string? v = _data.GetString("numbers", sym, k);
            return v is null or DataTree.EmptyMarker ? fallback : v;
        }

        string mg = _data.GetString("numbers", "minimumGroupingDigits") ?? "1";
        return new NumberSymbols(
            G("decimal", "."), G("group", ","), G("plusSign", "+"), G("minusSign", "-"), G("percentSign", "%"),
            G("exponential", "E"), G("infinity", "∞"), G("nan", "NaN"), G("approximatelySign", "~"),
            int.Parse(mg, NumberFormatInfo.InvariantInfo));
    }

    private string? CurrencyText(string code, string field)
    {
        string? v = _data.GetString("currencies", code, field);
        return v is null or DataTree.EmptyMarker ? null : v;
    }

    private string CurrencyName(string code, PluralCategory cat)
    {
        string c = CategoryNames[(int)cat];
        return CurrencyText(code, "displayName-count-" + c)
            ?? CurrencyText(code, "displayName-count-other")
            ?? CurrencyText(code, "displayName")
            ?? code;
    }

    // ---- layouts: the affix tokens around the number for each plural category ----

    private Layout GetLayout(PluralCategory cat)
    {
        Layout? l = _layouts[(int)cat];
        if (l is not null)
        {
            return l;
        }

        l = BuildLayout(cat);
        _layouts[(int)cat] = l;
        return l;
    }

    private Layout BuildLayout(PluralCategory cat)
    {
        if (Style == "unit" && Unit == "percent" && _unitWidth != "long")
        {
            // The percent unit takes the percent number pattern of the locale ("5 %" in French), its sign typed as a unit.
            Layout percent = new(_percentPattern.Prefix, _percentPattern.Suffix, null, null);
            return new Layout(PercentAsUnit(percent.Prefix), PercentAsUnit(percent.Suffix), null, null);
        }

        if (Style == "unit" || (Style == "percent" && Notation == "compact"))
        {
            // Compact percent takes the short percent unit pattern, as ICU does ("123 %" with an ordinary space in French).
            string pattern = Style == "percent" ? UnitPattern("percent", "short", cat) : UnitPattern(Unit!, _unitWidth, cat);
            int at = pattern.IndexOf("{0}", StringComparison.Ordinal);
            return new Layout(UnitTokens(pattern[..at]), UnitTokens(pattern[(at + 3)..]), null, null);
        }

        if (Style == "currency" && CurrencyDisplay == "name")
        {
            string key = "unitPattern-count-" + CategoryNames[(int)cat];
            string pattern = _data.GetString("numbers", "currencyFormats" + _numberNs, key)
                ?? _data.GetString("numbers", "currencyFormats" + _numberNs, "unitPattern-count-other")
                ?? "{0} {1}";
            int at = pattern.IndexOf("{0}", StringComparison.Ordinal);
            string name = CurrencyName(Currency!, cat);
            Tok[] Side(string text)
            {
                var list = new List<Tok>();
                int p = text.IndexOf("{1}", StringComparison.Ordinal);
                if (p < 0)
                {
                    list.AddRange(NumberPattern.TokenizeAffix(text));
                }
                else
                {
                    list.AddRange(NumberPattern.TokenizeAffix(text[..p]));
                    list.Add(new Tok(TokKind.Currency, "3:" + name));
                    list.AddRange(NumberPattern.TokenizeAffix(text[(p + 3)..]));
                }

                return [.. list];
            }

            return new Layout(Side(pattern[..at]), Side(pattern[(at + 3)..]), null, null);
        }

        return new Layout(_basePattern.Prefix, _basePattern.Suffix, _basePattern.NegativePrefix, _basePattern.NegativeSuffix);
    }

    private Tok[] PercentAsUnit(Tok[] toks) =>
        [.. toks.Select(t => t.Kind == TokKind.Percent ? new Tok(TokKind.Unit, _symbols.Percent) : t)];

    // Unit names are plain text: a '-' or '%' in them is not a sign or a percent placeholder.
    private static Tok[] UnitTokens(string text) => text.Length == 0 ? [] : [new Tok(TokKind.Unit, text)];

    private static Tok[] AsKind(Tok[] toks, TokKind kind)
    {
        var r = new Tok[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            r[i] = toks[i].Kind == TokKind.Literal ? new Tok(kind, toks[i].Text) : toks[i];
        }

        return r;
    }

    // ---- units ----

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> UnitKeys = new(StringComparer.Ordinal);

    private Dictionary<string, string> UnitKeyIndex()
    {
        return UnitKeys.GetOrAdd(_data.Id, _ =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in _data.GetKeys("units", "long"))
            {
                int dash = key.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0 && !key.StartsWith("10", StringComparison.Ordinal) && !key.StartsWith("1024", StringComparison.Ordinal)
                    && !key.StartsWith("power", StringComparison.Ordinal))
                {
                    map.TryAdd(key[(dash + 1)..], key);
                }
            }

            return map;
        });
    }

    private string UnitPattern(string unit, string width, PluralCategory cat)
    {
        string c = CategoryNames[(int)cat];
        string? Direct(string id, string count)
        {
            if (!UnitKeyIndex().TryGetValue(id, out string? key))
            {
                return null;
            }

            return _data.GetString("units", width, key, "unitPattern-count-" + count)
                ?? _data.GetString("units", width, key, "unitPattern-count-other");
        }

        string? direct = Direct(unit, c);
        if (direct is not null)
        {
            return direct;
        }

        int per = unit.IndexOf("-per-", StringComparison.Ordinal);
        if (per < 0)
        {
            return "{0} " + unit;
        }

        string numerator = unit[..per], denominator = unit[(per + 5)..];
        string numPattern = Direct(numerator, c) ?? "{0} " + numerator;
        if (UnitKeyIndex().TryGetValue(denominator, out string? denKey))
        {
            string? perUnit = _data.GetString("units", width, denKey, "perUnitPattern");
            if (perUnit is not null)
            {
                return perUnit.Replace("{0}", numPattern, StringComparison.Ordinal);
            }
        }

        string compound = _data.GetString("units", width, "per", "compoundUnitPattern") ?? "{0}/{1}";
        string denSingular = (Direct(denominator, "one") ?? "{0} " + denominator).Replace("{0}", "", StringComparison.Ordinal).Trim();
        return compound.Replace("{1}", denSingular, StringComparison.Ordinal).Replace("{0}", numPattern, StringComparison.Ordinal);
    }

    // ---- compact tables ----

    private sealed class CompactTable
    {
        internal int[] Magnitudes = [];
        internal Dictionary<(int, string), NumberPattern> Patterns = new();
    }

    private static readonly ConcurrentDictionary<string, CompactTable> CompactTables = new(StringComparer.Ordinal);

    private CompactTable GetCompactTable()
    {
        bool currency = Style == "currency" && CurrencyDisplay != "name";
        string display = CompactDisplay;
        string id = $"{_data.Id}|{display}|{currency}";
        return CompactTables.GetOrAdd(id, _ =>
        {
            var table = new CompactTable();
            string[] path = currency
                ? ["numbers", "currencyFormats" + _numberNs, "short", "standard"]
                : ["numbers", "decimalFormats" + _numberNs, display, "decimalFormat"];
            var mags = new SortedSet<int>();
            foreach (string key in _data.GetKeys(path))
            {
                int dash = key.IndexOf("-count-", StringComparison.Ordinal);
                if (dash < 0 || key.Contains("-alt-", StringComparison.Ordinal))
                {
                    continue;
                }

                string cat = key[(dash + 7)..];
                string? text = _data.GetString([.. path, key]);
                if (text is null)
                {
                    continue;
                }

                int mag = dash - 1;
                mags.Add(mag);
                table.Patterns[(mag, cat)] = NumberPattern.Parse(text);
            }

            table.Magnitudes = [.. mags];
            return table;
        });
    }

    private static string CategoryName(PluralCategory c) => CategoryNames[(int)c];

    private static NumberPattern? CompactPattern(CompactTable t, int key, PluralCategory cat, RoundedDigits? digits = null)
    {
        // An explicit "count-1" or "count-0" pattern wins for exactly that value (UTS #35, Compact Number Formats).
        if (digits is { } d && d.Fraction.Length == 0)
        {
            string exact = d.Integer.TrimStart('0');
            string name = exact.Length == 0 ? "0" : exact == "1" ? "1" : "";
            if (name.Length > 0 && t.Patterns.TryGetValue((key, name), out NumberPattern? ex))
            {
                return ex;
            }
        }

        return t.Patterns.TryGetValue((key, CategoryName(cat)), out NumberPattern? p) ? p
            : t.Patterns.TryGetValue((key, "other"), out p) ? p : null;
    }

    // Largest table key <= magnitude, or -1 when the number is below the first compact threshold.
    private static int CompactKey(CompactTable t, int magnitude)
    {
        int best = -1;
        foreach (int m in t.Magnitudes)
        {
            if (m <= magnitude)
            {
                best = m;
            }
        }

        return best;
    }

    private int ComputeExponentForMagnitude(int magnitude, CompactTable? compact, out int key)
    {
        key = -1;
        switch (Notation)
        {
            case "scientific":
                return magnitude;
            case "engineering":
                return (int)Math.Floor(magnitude / 3.0) * 3;
            case "compact":
                key = CompactKey(compact!, magnitude);
                if (key < 0)
                {
                    return 0;
                }

                NumberPattern? p = CompactPattern(compact!, key, PluralCategory.Other);
                if (p is null || p.IsBare)
                {
                    key = -1;
                    return 0;
                }

                return key - Math.Max(p.ZeroCount - 1, 0);
            default:
                return 0;
        }
    }

    // ---- formatting ----

    /// <summary>Formats a value to a string (FormatNumeric, 15.5.? ).</summary>
    internal string Format(DecimalNumber x)
    {
        var parts = new List<NumberPart>(8);
        Partition(x, parts, null);
        return Concat(parts);
    }

    /// <summary>Formats a Number value.</summary>
    internal string Format(double x) => Format(DecimalNumber.FromDouble(x));

    /// <summary>The parts of a formatted value (FormatNumericToParts).</summary>
    internal List<NumberPart> FormatToParts(DecimalNumber x)
    {
        var parts = new List<NumberPart>(8);
        Partition(x, parts, null);
        return parts;
    }

    internal static string Concat(List<NumberPart> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0].Value;
        }

        int n = 0;
        foreach (NumberPart p in parts)
        {
            n += p.Value.Length;
        }

        return string.Create(n, parts, static (span, ps) =>
        {
            int at = 0;
            foreach (NumberPart p in ps)
            {
                p.Value.AsSpan().CopyTo(span[at..]);
                at += p.Value.Length;
            }
        });
    }

    private enum SignKind
    {
        None,
        Minus,
        Plus,
    }

    /// <summary>The scaled and rounded number: digits, compact exponent and the compact table key used.</summary>
    private readonly record struct Scaled(RoundedDigits Digits, int Exponent, int CompactKey);

    private Scaled ScaleAndRound(DecimalNumber abs, bool negative)
    {
        if (Notation == "standard" || abs.IsZero)
        {
            return new Scaled(Digits.Round(abs, negative), 0, -1);
        }

        CompactTable? table = Notation == "compact" ? GetCompactTable() : null;
        int magnitude = abs.Magnitude;
        int exp = ComputeExponentForMagnitude(magnitude, table, out int key);
        RoundedDigits r = Digits.Round(abs.ScaleByPowerOfTen(-exp), negative);
        if (!r.IsZero && r.Magnitude != magnitude - exp)
        {
            exp = ComputeExponentForMagnitude(magnitude + 1, table, out key);
            r = Digits.Round(abs.ScaleByPowerOfTen(-exp), negative);
        }

        return new Scaled(r, exp, key);
    }

    private SignKind Sign(bool negative, bool isNaN, bool isZero)
    {
        switch (SignDisplay)
        {
            case "never":
                return SignKind.None;
            case "always":
                return negative ? SignKind.Minus : SignKind.Plus;
            case "exceptZero":
                return isNaN || isZero ? SignKind.None : negative ? SignKind.Minus : SignKind.Plus;
            case "negative":
                return negative && !isZero ? SignKind.Minus : SignKind.None;
            default:
                return negative ? SignKind.Minus : SignKind.None;
        }
    }

    // The category of the displayed digits with the compact exponent as the c/e operand only (picks the compact pattern).
    private PluralCategory PluralOfDigits(RoundedDigits d, int exponent) =>
        PluralRulesService.SelectDigits(CldrData.Plurals, _dataLocale, PluralType.Cardinal, d, Notation == "compact" ? exponent : 0);

    // The category of the whole value (1.2M counts as 1200000), which picks unit and currency names.
    private PluralCategory PluralOfValue(RoundedDigits d, int exponent)
    {
        PluralOperands ops = PluralOperands.FromDigits(d.Integer, d.Fraction, Notation == "compact" ? exponent : 0);
        return CldrData.Plurals.Select(_dataLocale, PluralType.Cardinal, ops);
    }

    /// <summary>
    /// The parts of one formatted value in layers, outermost first: unit or currency-name text, then the
    /// sign, pattern and compact affixes ("middle"), then the digits. Range formatting collapses layers
    /// that two values share.
    /// </summary>
    private sealed class Pieces
    {
        internal readonly List<NumberPart> OuterPrefix = [];
        internal readonly List<NumberPart> MiddlePrefix = [];
        internal readonly List<NumberPart> Number = [];
        internal readonly List<NumberPart> MiddleSuffix = [];
        internal readonly List<NumberPart> OuterSuffix = [];

        internal void AppendTo(List<NumberPart> parts, string? source)
        {
            foreach (List<NumberPart> list in new[] { OuterPrefix, MiddlePrefix, Number, MiddleSuffix, OuterSuffix })
            {
                foreach (NumberPart p in list)
                {
                    AddPart(parts, p, source);
                }
            }
        }
    }

    private static void AddPart(List<NumberPart> parts, NumberPart p, string? source)
    {
        if (p.Type == "literal")
        {
            AddLiteral(parts, p.Value, source);
        }
        else
        {
            parts.Add(p with { Source = source });
        }
    }

    private void Partition(DecimalNumber x, List<NumberPart> parts, string? source) => Build(x).AppendTo(parts, source);

    /// <summary>PartitionNumberPattern (15.5.?): the layered parts of <paramref name="x"/>.</summary>
    private Pieces Build(DecimalNumber x)
    {
        bool negative = x.IsNegative && !x.IsNaN;
        DecimalNumber abs = x.Abs();
        Scaled scaled = default;
        bool special = !x.IsFinite;
        PluralCategory cat = PluralCategory.Other, compactCat = PluralCategory.Other;
        if (!special)
        {
            if (Style == "percent")
            {
                abs = abs.ScaleByPowerOfTen(2);
            }

            scaled = ScaleAndRound(abs, negative);
            cat = PluralOfValue(scaled.Digits, scaled.Exponent);
            compactCat = PluralOfDigits(scaled.Digits, scaled.Exponent);
        }

        bool isZero = !special && scaled.Digits.IsZero;
        SignKind sign = Sign(negative, x.IsNaN, isZero);
        Layout layout = GetLayout(cat);
        var pieces = new Pieces();

        Tok[] prefix = layout.Prefix, suffix = layout.Suffix;
        bool explicitNegative = false;
        if (sign == SignKind.Minus && layout.NegPrefix is not null)
        {
            prefix = layout.NegPrefix;
            suffix = layout.NegSuffix ?? [];
            explicitNegative = true;
        }

        NumberPattern? compact = null;
        if (!special && Notation == "compact" && scaled.CompactKey >= 0)
        {
            compact = CompactPattern(GetCompactTable(), scaled.CompactKey, compactCat, scaled.Digits);
        }

        // Currency compact patterns carry the currency themselves.
        Tok[] cPrefix = compact is null ? [] : AsKind(compact.Prefix, TokKind.Compact);
        Tok[] cSuffix = compact is null ? [] : AsKind(compact.Suffix, TokKind.Compact);
        if (compact is not null && Style == "currency" && CurrencyDisplay != "name")
        {
            prefix = [];
            suffix = [];
        }

        // Unit and currency-name text wraps everything else; other affixes sit next to the digits.
        bool outerLayout = Style == "unit" || (Style == "percent" && Notation == "compact") || (Style == "currency" && CurrencyDisplay == "name");
        if (!explicitNegative)
        {
            if (sign == SignKind.Minus)
            {
                pieces.MiddlePrefix.Add(new NumberPart("minusSign", _symbols.Minus));
            }
            else if (sign == SignKind.Plus)
            {
                pieces.MiddlePrefix.Add(new NumberPart("plusSign", _symbols.Plus));
            }
        }

        EmitAffix(prefix, outerLayout ? pieces.OuterPrefix : pieces.MiddlePrefix, null);
        EmitAffix(cPrefix, pieces.MiddlePrefix, null);
        if (x.IsNaN)
        {
            pieces.Number.Add(new NumberPart("nan", _symbols.NaN));
        }
        else if (x.IsInfinity)
        {
            pieces.Number.Add(new NumberPart("infinity", _symbols.Infinity));
        }
        else if (compact is null || compact.ZeroCount > 0)
        {
            EmitNumber(scaled, pieces.Number, null);
        }

        EmitAffix(cSuffix, pieces.MiddleSuffix, null);
        EmitAffix(suffix, outerLayout ? pieces.OuterSuffix : pieces.MiddleSuffix, null);
        if (Style == "currency")
        {
            ApplyCurrencySpacing(pieces);
        }

        return pieces;
    }

    private void EmitAffix(Tok[] toks, List<NumberPart> parts, string? source)
    {
        foreach (Tok t in toks)
        {
            switch (t.Kind)
            {
                case TokKind.Literal:
                    AddLiteral(parts, t.Text, source);
                    break;
                case TokKind.Percent:
                    parts.Add(new NumberPart(Style == "percent" && Notation == "compact" ? "unit" : "percentSign", _symbols.Percent, source));
                    break;
                case TokKind.Minus:
                    parts.Add(new NumberPart("minusSign", _symbols.Minus, source));
                    break;
                case TokKind.Plus:
                    parts.Add(new NumberPart("plusSign", _symbols.Plus, source));
                    break;
                case TokKind.Currency:
                    string text = t.Text.StartsWith("3:", StringComparison.Ordinal) ? t.Text[2..]
                        : t.Text == "2" ? Currency! : _currencySymbolText ?? Currency!;
                    parts.Add(new NumberPart("currency", text, source));
                    break;
                case TokKind.Unit:
                    AddSplit(parts, t.Text, "unit", source);
                    break;
                case TokKind.Compact:
                    AddSplit(parts, t.Text, "compact", source);
                    break;
            }
        }
    }

    // Leading and trailing white space of unit and compact text is a literal; the rest is the named part.
    private static void AddSplit(List<NumberPart> parts, string text, string type, string? source)
    {
        int a = 0, b = text.Length;
        while (a < b && char.IsWhiteSpace(text[a]))
        {
            a++;
        }

        while (b > a && char.IsWhiteSpace(text[b - 1]))
        {
            b--;
        }

        if (a > 0)
        {
            AddLiteral(parts, text[..a], source);
        }

        if (b > a)
        {
            parts.Add(new NumberPart(type, text[a..b], source));
        }

        if (b < text.Length)
        {
            AddLiteral(parts, text[b..], source);
        }
    }

    private static void AddLiteral(List<NumberPart> parts, string text, string? source)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (parts.Count > 0 && parts[^1].Type == "literal" && parts[^1].Source == source)
        {
            parts[^1] = parts[^1] with { Value = parts[^1].Value + text };
            return;
        }

        parts.Add(new NumberPart("literal", text, source));
    }

    private void EmitNumber(Scaled scaled, List<NumberPart> parts, string? source)
    {
        RoundedDigits d = scaled.Digits;
        string integer = d.Integer.TrimStart('0');
        int minInt = Digits.MinimumIntegerDigits;
        if (integer.Length < minInt)
        {
            integer = new string('0', minInt - integer.Length) + integer;
        }

        if (integer.Length == 0)
        {
            integer = "0";
        }

        EmitInteger(integer, parts, source);
        if (d.Fraction.Length > 0)
        {
            parts.Add(new NumberPart("decimal", _symbols.Decimal, source));
            parts.Add(new NumberPart("fraction", d.Fraction, source));
        }

        if (Notation is "scientific" or "engineering")
        {
            parts.Add(new NumberPart("exponentSeparator", _symbols.Exponential, source));
            int e = scaled.Exponent;
            if (e < 0)
            {
                parts.Add(new NumberPart("exponentMinusSign", _symbols.Minus, source));
                e = -e;
            }

            parts.Add(new NumberPart("exponentInteger", e.ToString(NumberFormatInfo.InvariantInfo), source));
        }
    }

    private void EmitInteger(string integer, List<NumberPart> parts, string? source)
    {
        int primary = _basePattern.PrimaryGroup == 0 ? 3 : _basePattern.PrimaryGroup;
        int secondary = _basePattern.SecondaryGroup == 0 ? primary : _basePattern.SecondaryGroup;
        int minGroup = _groupingMode switch
        {
            "false" => int.MaxValue,
            "always" => 1,
            "min2" => 2,
            _ => _symbols.MinGrouping,
        };
        if (_groupingMode == "false" || integer.Length < primary + minGroup)
        {
            parts.Add(new NumberPart("integer", integer, source));
            return;
        }

        // Group boundaries counted from the right: first `primary` digits, then `secondary` each.
        var cuts = new List<int>();
        int pos = integer.Length - primary;
        while (pos > 0)
        {
            cuts.Add(pos);
            pos -= secondary;
        }

        cuts.Reverse();
        int prev = 0;
        foreach (int c in cuts)
        {
            parts.Add(new NumberPart("integer", integer[prev..c], source));
            parts.Add(new NumberPart("group", _symbols.Group, source));
            prev = c;
        }

        parts.Add(new NumberPart("integer", integer[prev..], source));
    }

    // CLDR currencySpacing: insert a no-break space between a letter-ending currency and an adjoining digit.
    private void ApplyCurrencySpacing(Pieces p)
    {
        if (p.Number.Count == 0 || p.Number[0].Type != "integer")
        {
            return;
        }

        if (p.MiddlePrefix.Count > 0 && p.MiddlePrefix[^1] is { Type: "currency" } before && NotSymbolOrSeparator(before.Value[^1]))
        {
            p.MiddlePrefix.Add(new NumberPart("literal", SpacingChar("afterCurrency")));
        }

        if (p.MiddleSuffix.Count > 0 && IsNumberEnd(p.Number[^1].Type) && p.MiddleSuffix[0] is { Type: "currency" } after && NotSymbolOrSeparator(after.Value[0]))
        {
            p.MiddleSuffix.Insert(0, new NumberPart("literal", SpacingChar("beforeCurrency")));
        }
    }

    private static bool IsNumberEnd(string type) => type is "integer" or "fraction" or "exponentInteger";

    private string SpacingChar(string which) =>
        _data.GetString("numbers", "currencyFormats" + _numberNs, "currencySpacing", which, "insertBetween") ?? "\u00A0";

    private static bool NotSymbolOrSeparator(char c)
    {
        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is not (UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol
            or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }
}

internal static class AsciiCase
{
    /// <summary>ASCII-only uppercase (culture-free).</summary>
    internal static string ToUpperInvariantAscii(this string s)
    {
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = src[i];
                span[i] = (uint)(c - 'a') <= 'z' - 'a' ? (char)(c & ~0x20) : c;
            }
        });
    }
}
