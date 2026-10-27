using System.Globalization;
using System.Numerics;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>One piece of a formatted duration (ECMA-402 13.3.4); <see cref="Unit"/> is the singular unit name or null.</summary>
internal readonly record struct DurationPart(string Type, string Value, string? Unit);

/// <summary>A Duration Record (ECMA-402 13.5.1): ten integers in the order years to nanoseconds.</summary>
internal readonly struct DurationRecord
{
    private readonly double[] _values;

    private DurationRecord(double[] values) => _values = values;

    /// <summary>The unit names in record order.</summary>
    internal static readonly string[] UnitNames = ["years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds"];

    /// <summary>The value of the unit at <paramref name="index"/> (0 years ... 9 nanoseconds); zero for an unset record.</summary>
    internal double this[int index] => _values is null ? 0 : _values[index];

    /// <summary>
    /// ToDurationRecord (13.5.3) after the JS layer has read the ten properties: <c>null</c> means undefined, anything else is the
    /// property's Number value. The sign of every field must agree and the totals must stay inside the limits of IsValidDuration.
    /// </summary>
    /// <exception cref="IntlException">No field is given (TypeError); a field is not an integer or the duration is not valid (RangeError).</exception>
    internal static DurationRecord Create(IReadOnlyList<double?> fields)
    {
        if (fields.Count != UnitNames.Length)
        {
            throw new ArgumentException("A duration has ten fields.", nameof(fields));
        }

        var values = new double[UnitNames.Length];
        bool any = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (fields[i] is not { } v)
            {
                continue;
            }

            any = true;
            // ECMA-402 13.5.2 ToIntegerIfIntegral: NaN, infinities and fractions are a RangeError.
            if (!double.IsFinite(v) || Math.Truncate(v) != v)
            {
                throw IntlException.Range($"Invalid duration: {UnitNames[i]} must be an integer");
            }

            values[i] = v == 0 ? 0 : v;
        }

        if (!any)
        {
            throw IntlException.Type("Invalid duration: no unit is set");
        }

        if (!IsValid(values))
        {
            throw IntlException.Range("Invalid duration");
        }

        return new DurationRecord(values);
    }

    /// <summary>The sign of the most significant non-zero field (DurationSign, 13.5.4).</summary>
    internal int Sign
    {
        get
        {
            for (int i = 0; i < UnitNames.Length; i++)
            {
                double v = this[i];
                if (v < 0)
                {
                    return -1;
                }

                if (v > 0)
                {
                    return 1;
                }
            }

            return 0;
        }
    }

    // ECMA-402 13.5.5 IsValidDuration.
    private static bool IsValid(double[] v)
    {
        int sign = 0;
        foreach (double x in v)
        {
            if (x < 0)
            {
                if (sign > 0)
                {
                    return false;
                }

                sign = -1;
            }
            else if (x > 0)
            {
                if (sign < 0)
                {
                    return false;
                }

                sign = 1;
            }
        }

        const double Limit = 4294967296.0; // 2^32
        if (Math.Abs(v[0]) >= Limit || Math.Abs(v[1]) >= Limit || Math.Abs(v[2]) >= Limit)
        {
            return false;
        }

        // The total in seconds is compared exactly, as nanoseconds, so no floating-point rounding is involved.
        BigInteger ns = ((new BigInteger(v[3]) * 86400) + (new BigInteger(v[4]) * 3600) + (new BigInteger(v[5]) * 60) + new BigInteger(v[6])) * 1_000_000_000
            + (new BigInteger(v[7]) * 1_000_000) + (new BigInteger(v[8]) * 1000) + new BigInteger(v[9]);
        return BigInteger.Abs(ns) < new BigInteger(9007199254740992.0) * 1_000_000_000;
    }
}

/// <summary>Inputs for <see cref="DurationFormatService"/> (the Intl.DurationFormat constructor options). Null means undefined.</summary>
internal sealed record DurationFormatOptions
{
    internal string? LocaleMatcher { get; init; }
    internal string? NumberingSystem { get; init; }

    /// <summary><c>style</c>: <c>long</c>, <c>short</c> (default), <c>narrow</c> or <c>digital</c>.</summary>
    internal string? Style { get; init; }

    /// <summary>Per-unit style options by option name (<c>hours</c>), and display options by name (<c>hoursDisplay</c>); absent means undefined.</summary>
    internal IReadOnlyDictionary<string, string>? UnitOptions { get; init; }

    /// <summary><c>fractionalDigits</c>, already converted by GetNumberOption; null means undefined.</summary>
    internal int? FractionalDigits { get; init; }
}

/// <summary>The resolved style and display of one duration unit (ECMA-402 13.5.6.1).</summary>
internal readonly record struct DurationUnitOptions(string Style, string Display);

/// <summary>
/// Intl.DurationFormat (ECMA-402 section 13) on top of the NumberFormat and ListFormat services: each unit is a unit-style number,
/// hours, minutes and seconds may be joined as <c>1:02:03</c>, and the pieces are joined by the locale's unit list pattern.
/// </summary>
internal sealed class DurationFormatService
{
    // Table 20 (ECMA-402 13.1.1): styles, digital default, and the NumberFormat unit.
    private static readonly string[] CalendarStyles = ["long", "short", "narrow"];
    private static readonly string[] ClockStyles = ["long", "short", "narrow", "numeric", "2-digit"];
    private static readonly string[] SubSecondStyles = ["long", "short", "narrow", "numeric"];
    private static readonly string[][] StyleLists = [CalendarStyles, CalendarStyles, CalendarStyles, CalendarStyles, ClockStyles, ClockStyles, ClockStyles, SubSecondStyles, SubSecondStyles, SubSecondStyles];
    private static readonly string[] DigitalDefaults = ["short", "short", "short", "short", "numeric", "numeric", "numeric", "numeric", "numeric", "numeric"];
    private static readonly string[] NumberFormatUnits = ["year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond"];

    private const int Hours = 4, Minutes = 5, Seconds = 6, Milliseconds = 7, Microseconds = 8, Nanoseconds = 9;

    private readonly DurationUnitOptions[] _units = new DurationUnitOptions[10];
    private readonly Dictionary<string, NumberFormatService> _numberFormats = new(StringComparer.Ordinal);
    private ListFormatService _list = null!;
    private string _hourMinuteSeparator = ":", _minuteSecondSeparator = ":";

    private DurationFormatService()
    {
    }

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; private set; } = "";

    /// <summary>The resolved numbering system (always <c>latn</c> for the supported locales).</summary>
    internal string NumberingSystem { get; private set; } = "latn";

    /// <summary>The resolved <c>style</c>.</summary>
    internal string Style { get; private set; } = "short";

    /// <summary>The resolved <c>fractionalDigits</c>, or null when undefined.</summary>
    internal int? FractionalDigits { get; private set; }

    /// <summary>
    /// The resolved style and display of a unit (0 years ... 9 nanoseconds). As in resolvedOptions, a sub-second unit shown as a
    /// fraction of the seconds reports <c>numeric</c>.
    /// </summary>
    internal DurationUnitOptions Unit(int index) => _units[index] is { Style: "fractional" } u ? u with { Style = "numeric" } : _units[index];

    /// <summary>Creates the service (13.1.1).</summary>
    /// <param name="locales">Requested locales, already canonicalized.</param>
    /// <param name="options">The constructor options.</param>
    /// <param name="defaultLocale">The host's default locale.</param>
    /// <exception cref="IntlException">An option is out of range or the unit styles are inconsistent (RangeError).</exception>
    internal static DurationFormatService Create(IReadOnlyList<string> locales, DurationFormatOptions options, string defaultLocale)
    {
        LocaleResolver.ValidateLocaleMatcher(options.LocaleMatcher, "DurationFormat");
        LocaleResolver.ValidateNumberingSystem(options.NumberingSystem, "DurationFormat");
        ResolvedLocale r = LocaleResolver.Resolve(
            locales,
            ["nu"],
            new Dictionary<string, string?> { ["nu"] = options.NumberingSystem },
            (_, _) => ["latn"],
            defaultLocale);

        var svc = new DurationFormatService
        {
            Locale = r.Locale,
            NumberingSystem = r.Keys.TryGetValue("nu", out string? nu) && nu.Length > 0 ? nu : "latn",
        };

        DataTree data = LocaleResolver.DataFor(r.DataLocale);
        bool twoDigitHours = false;
        if (data.GetString("units", "durationUnit-type-hms", "durationUnitPattern") is { } hms)
        {
            svc._hourMinuteSeparator = svc._minuteSecondSeparator = SeparatorOf(hms);
            twoDigitHours = hms.StartsWith("hh", StringComparison.Ordinal);
        }

        if (data.GetString("units", "durationUnit-type-hm", "durationUnitPattern") is { } hm)
        {
            svc._hourMinuteSeparator = SeparatorOf(hm);
        }

        if (data.GetString("units", "durationUnit-type-ms", "durationUnitPattern") is { } ms)
        {
            svc._minuteSecondSeparator = SeparatorOf(ms);
        }

        string style = options.Style ?? "short";
        if (style is not ("long" or "short" or "narrow" or "digital"))
        {
            throw IntlException.Range($"Value {style} out of range for Intl.DurationFormat options property style");
        }

        svc.Style = style;
        string prevStyle = "";
        for (int i = 0; i < 10; i++)
        {
            DurationUnitOptions u = GetUnitOptions(i, options.UnitOptions, style, prevStyle, twoDigitHours);
            svc._units[i] = u;
            if (i is >= Hours and <= Microseconds)
            {
                prevStyle = u.Style;
            }
        }

        if (options.FractionalDigits is { } fd)
        {
            if (fd is < 0 or > 9)
            {
                throw IntlException.Range($"{fd} is out of range for Intl.DurationFormat options property fractionalDigits");
            }

            svc.FractionalDigits = fd;
        }

        svc._list = ListFormatService.Create([svc.Locale], "unit", style == "digital" ? "short" : style, defaultLocale);
        return svc;
    }

    // The separator in a CLDR duration pattern such as "h:mm:ss" is the text between the first two fields.
    private static string SeparatorOf(string pattern)
    {
        int i = 0;
        while (i < pattern.Length && pattern[i] is 'h' or 'H')
        {
            i++;
        }

        int j = i;
        while (j < pattern.Length && pattern[j] is not ('h' or 'H' or 'm' or 's'))
        {
            j++;
        }

        return j > i ? pattern[i..j] : ":";
    }

    // ECMA-402 13.5.6 GetDurationUnitOptions and 13.5.6.2 ValidateDurationUnitStyle.
    private static DurationUnitOptions GetUnitOptions(int unit, IReadOnlyDictionary<string, string>? raw, string baseStyle, string prevStyle, bool twoDigitHours)
    {
        string name = DurationRecord.UnitNames[unit];
        string? style = null;
        if (raw is not null && raw.TryGetValue(name, out string? given))
        {
            if (Array.IndexOf(StyleLists[unit], given) < 0)
            {
                throw IntlException.Range($"Value {given} out of range for Intl.DurationFormat options property {name}");
            }

            style = given;
        }

        string displayDefault = "always";
        bool clock = unit is Hours or Minutes or Seconds;
        if (style is null)
        {
            if (baseStyle == "digital")
            {
                style = DigitalDefaults[unit];
                if (!clock)
                {
                    displayDefault = "auto";
                }
            }
            else if (prevStyle is "fractional" or "numeric" or "2-digit")
            {
                style = "numeric";
                if (unit is not (Minutes or Seconds))
                {
                    displayDefault = "auto";
                }
            }
            else
            {
                style = baseStyle;
                displayDefault = "auto";
            }
        }

        if (style == "numeric" && unit >= Milliseconds)
        {
            style = "fractional";
            displayDefault = "auto";
        }

        string display = displayDefault;
        if (raw is not null && raw.TryGetValue(name + "Display", out string? givenDisplay))
        {
            if (givenDisplay is not ("auto" or "always"))
            {
                throw IntlException.Range($"Value {givenDisplay} out of range for Intl.DurationFormat options property {name}Display");
            }

            display = givenDisplay;
        }

        if (display == "always" && style == "fractional")
        {
            throw IntlException.Range($"{name}Display cannot be \"always\" when {name} is shown as a fraction");
        }

        if (prevStyle == "fractional" && style != "fractional")
        {
            throw IntlException.Range($"{name} must be numeric when the unit before it is fractional");
        }

        if (prevStyle is "numeric" or "2-digit" && style is not ("fractional" or "numeric" or "2-digit"))
        {
            throw IntlException.Range($"{name} must be numeric when the unit before it is numeric");
        }

        if (unit == Hours && twoDigitHours)
        {
            style = "2-digit";
        }

        if (unit is Minutes or Seconds && prevStyle is "numeric" or "2-digit")
        {
            style = "2-digit";
        }

        return new DurationUnitOptions(style, display);
    }

    /// <summary>Formats a duration as one string (13.3.3).</summary>
    internal string Format(DurationRecord duration) => string.Concat(FormatToParts(duration).Select(p => p.Value));

    /// <summary>PartitionDurationFormatPattern (13.5.15): the duration as typed parts.</summary>
    internal List<DurationPart> FormatToParts(DurationRecord duration)
    {
        var result = new List<List<DurationPart>>();
        bool signDisplayed = true;
        for (int unit = 0; unit < 10; unit++)
        {
            DurationUnitOptions o = _units[unit];
            if (o.Style is "numeric" or "2-digit")
            {
                List<DurationPart> numeric = FormatNumericUnits(duration, unit, signDisplayed);
                if (numeric.Count > 0 && unit == Seconds && result.Count > 0)
                {
                    // Seconds shown as a number after a spelled-out unit stay with it ("5 min:07") instead of becoming a list item.
                    result[^1].Add(new DurationPart("literal", _minuteSecondSeparator, null));
                    result[^1].AddRange(numeric);
                }
                else if (numeric.Count > 0)
                {
                    result.Add(numeric);
                }

                break;
            }

            DecimalNumber value = DecimalNumber.FromBigInteger(new BigInteger(duration[unit]));
            bool stop = false;
            var nf = new NumberFormatOptions { Style = "unit", Unit = NumberFormatUnits[unit], UnitDisplay = o.Style, NumberingSystem = NumberingSystem };
            DigitOptionInputs digits = new();
            if (NextUnitFractional(unit))
            {
                value = Sum(new BigInteger(duration[unit]), ComputeFractionalDigits(duration));
                digits = FractionDigitInputs();
                stop = true;
            }

            if (o.Display == "always" || !value.IsZero)
            {
                if (signDisplayed)
                {
                    signDisplayed = false;
                    if (value.IsZero && duration.Sign < 0)
                    {
                        value = DecimalNumber.Zero(true);
                    }
                }
                else
                {
                    nf = nf with { SignDisplay = "never" };
                }

                result.Add(NumberParts(nf with { Digits = digits }, value, NumberFormatUnits[unit]));
            }

            if (stop)
            {
                break;
            }
        }

        return ListParts(result);
    }

    private DigitOptionInputs FractionDigitInputs() => FractionalDigits is { } fd
        ? new DigitOptionInputs { MaximumFractionDigits = fd, MinimumFractionDigits = fd, RoundingMode = "trunc" }
        : new DigitOptionInputs { MaximumFractionDigits = 9, MinimumFractionDigits = 0, RoundingMode = "trunc" };

    // 13.5.8 NextUnitFractional.
    private bool NextUnitFractional(int unit) => unit is Seconds or Milliseconds or Microseconds && _units[unit + 1].Style == "fractional";

    // 13.5.7 ComputeFractionalDigits: the fractional units as a fraction of the smallest unit that is not fractional.
    private (BigInteger Numerator, int Scale) ComputeFractionalDigits(DurationRecord duration)
    {
        BigInteger sum = BigInteger.Zero;
        int count = 0;
        for (int unit = Milliseconds; unit <= Nanoseconds; unit++)
        {
            if (_units[unit].Style == "fractional")
            {
                sum = (sum * 1000) + new BigInteger(duration[unit]);
                count++;
            }
        }

        return (sum, 3 * count);
    }

    // The whole number plus the fraction, exactly.
    private static DecimalNumber Sum(BigInteger whole, (BigInteger Numerator, int Scale) fraction) =>
        fraction.Scale == 0
            ? DecimalNumber.FromBigInteger(whole)
            : DecimalNumber.FromBigInteger((whole * BigInteger.Pow(10, fraction.Scale)) + fraction.Numerator).ScaleByPowerOfTen(-fraction.Scale);

    // 13.5.12 FormatNumericUnits.
    private List<DurationPart> FormatNumericUnits(DurationRecord duration, int firstNumericUnit, bool signDisplayed)
    {
        var parts = new List<DurationPart>();
        double hoursValue = duration[Hours], minutesValue = duration[Minutes];
        (BigInteger, int) fractional = (BigInteger.Zero, 0);
        if (duration[Milliseconds] != 0 || duration[Microseconds] != 0 || duration[Nanoseconds] != 0)
        {
            fractional = ComputeFractionalDigits(duration);
        }

        DecimalNumber secondsValue = Sum(new BigInteger(duration[Seconds]), fractional);
        bool hoursFormatted = firstNumericUnit == Hours && (hoursValue != 0 || _units[Hours].Display == "always");
        bool secondsFormatted = !secondsValue.IsZero || _units[Seconds].Display == "always";
        bool minutesFormatted = false;
        if (firstNumericUnit is Hours or Minutes)
        {
            minutesFormatted = (hoursFormatted && secondsFormatted) || minutesValue != 0 || _units[Minutes].Display == "always";
        }

        int sign = duration.Sign;
        if (hoursFormatted)
        {
            DecimalNumber v = DecimalNumber.FromBigInteger(new BigInteger(hoursValue));
            if (signDisplayed && v.IsZero && sign < 0)
            {
                v = DecimalNumber.Zero(true);
            }

            parts.AddRange(NumericNumber(v, _units[Hours].Style == "2-digit", signDisplayed, null, "hour"));
            signDisplayed = false;
        }

        if (minutesFormatted)
        {
            DecimalNumber v = DecimalNumber.FromBigInteger(new BigInteger(minutesValue));
            if (signDisplayed && v.IsZero && sign < 0)
            {
                v = DecimalNumber.Zero(true);
            }

            if (hoursFormatted)
            {
                parts.Add(new DurationPart("literal", _hourMinuteSeparator, null));
            }

            parts.AddRange(NumericNumber(v, _units[Minutes].Style == "2-digit", signDisplayed, null, "minute"));
            signDisplayed = false;
        }

        if (secondsFormatted)
        {
            if (signDisplayed && secondsValue.IsZero && sign < 0)
            {
                secondsValue = DecimalNumber.Zero(true);
            }

            if (minutesFormatted)
            {
                parts.Add(new DurationPart("literal", _minuteSecondSeparator, null));
            }

            parts.AddRange(NumericNumber(secondsValue, _units[Seconds].Style == "2-digit", signDisplayed, FractionDigitInputs(), "second"));
        }

        return parts;
    }

    private List<DurationPart> NumericNumber(DecimalNumber value, bool twoDigit, bool signDisplayed, DigitOptionInputs? fraction, string unit)
    {
        DigitOptionInputs digits = fraction ?? new DigitOptionInputs();
        if (twoDigit)
        {
            digits = digits with { MinimumIntegerDigits = 2 };
        }

        var nf = new NumberFormatOptions
        {
            NumberingSystem = NumberingSystem,
            UseGroupingFlag = false,
            SignDisplay = signDisplayed ? null : "never",
            Digits = digits,
        };
        return NumberParts(nf, value, unit);
    }

    private List<DurationPart> NumberParts(NumberFormatOptions options, DecimalNumber value, string unit)
    {
        string key = string.Join("|", options.Style, options.Unit, options.UnitDisplay, options.SignDisplay, options.UseGroupingFlag, options.Digits.MinimumIntegerDigits, options.Digits.MinimumFractionDigits, options.Digits.MaximumFractionDigits, options.Digits.RoundingMode);
        if (!_numberFormats.TryGetValue(key, out NumberFormatService? nf))
        {
            nf = NumberFormatService.Create([Locale], options, Locale);
            _numberFormats[key] = nf;
        }

        var parts = new List<DurationPart>();
        foreach (NumberPart p in nf.FormatToParts(value))
        {
            parts.Add(new DurationPart(p.Type, p.Value, unit));
        }

        return parts;
    }

    // 13.5.14 ListFormatParts.
    private List<DurationPart> ListParts(List<List<DurationPart>> items)
    {
        var flat = new List<DurationPart>();
        if (items.Count == 0)
        {
            return flat;
        }

        var strings = new List<string>(items.Count);
        foreach (List<DurationPart> item in items)
        {
            strings.Add(string.Concat(item.Select(p => p.Value)));
        }

        int next = 0;
        foreach (ListPart lp in _list.FormatToParts(strings))
        {
            if (lp.Type == "element")
            {
                flat.AddRange(items[next++]);
            }
            else
            {
                flat.Add(new DurationPart("literal", lp.Value, null));
            }
        }

        return flat;
    }
}
