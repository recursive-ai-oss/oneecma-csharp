using System.Globalization;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

internal sealed partial class DateTimeFormatService
{
    /// <summary>
    /// CreateDateTimeFormat (ECMA-402 11.1.2). Options have already been read and converted by the caller; values that are not in
    /// their allowed set raise <see cref="IntlException"/> here.
    /// </summary>
    /// <param name="locales">Requested locales, canonicalized.</param>
    /// <param name="options">The option record.</param>
    /// <param name="defaultLocale">The host's default locale.</param>
    /// <param name="defaultTimeZone">The host's default time zone, used when <c>timeZone</c> is undefined.</param>
    /// <param name="zones">Where offsets come from.</param>
    /// <param name="required"><c>date</c>, <c>time</c> or <c>any</c>: which components make the options non-default.</param>
    /// <param name="defaults"><c>date</c>, <c>time</c> or <c>all</c>: which components are added when none were asked for.</param>
    /// <exception cref="IntlException">An option is out of range (RangeError) or conflicts with another (TypeError).</exception>
    internal static DateTimeFormatService Create(
        IReadOnlyList<string> locales,
        DateTimeFormatOptions options,
        string defaultLocale,
        string defaultTimeZone,
        ITimeZoneSource zones,
        string required = "any",
        string defaults = "date")
    {
        LocaleResolver.ValidateLocaleMatcher(options.LocaleMatcher, "DateTimeFormat");
        if (options.Calendar is { } cal && !IsTypeSequence(cal))
        {
            throw IntlException.Range($"Invalid calendar : {cal}");
        }

        LocaleResolver.ValidateNumberingSystem(options.NumberingSystem, "DateTimeFormat");
        Check("hourCycle", options.HourCycle, "h11", "h12", "h23", "h24");

        var optionKeys = new Dictionary<string, string?>
        {
            ["ca"] = options.Calendar,
            ["nu"] = options.NumberingSystem,
            ["hc"] = options.Hour12 is null ? options.HourCycle : null,
        };
        ResolvedLocale r = LocaleResolver.Resolve(locales, ["ca", "hc", "nu"], optionKeys, (_, key) => key switch
        {
            "ca" => ["gregory"],
            "nu" => ["latn"],
            _ => ["", "h11", "h12", "h23", "h24"],
        }, defaultLocale);
        string localeTag = r.Locale;
        if (options.Hour12 is not null)
        {
            // An hour12 option overrides the hc keyword of the locale, which then does not show in the resolved locale.
            localeTag = LocaleResolver.Resolve(locales, ["ca", "nu"], optionKeys, (_, key) => key == "ca" ? ["gregory"] : ["latn"], defaultLocale).Locale;
        }

        var svc = new DateTimeFormatService(zones)
        {
            Locale = localeTag,
            _dataLocale = r.DataLocale,
            _data = CldrData.GetLocale(r.DataLocale),
        };
        string? hcKey = r.Keys.TryGetValue("hc", out string? h) && h.Length > 0 ? h : null;

        // The resolved time zone keeps the spelling of the identifier asked for (a link name stays a link name); offsets and names
        // are looked up under the canonical identifier.
        string zoneInput = options.TimeZone ?? defaultTimeZone;
        string? identifier = TimeZoneData.Identify(zoneInput);
        if (identifier is null)
        {
            if (options.TimeZone is not null)
            {
                throw IntlException.Range($"Invalid time zone specified: {zoneInput}");
            }

            identifier = "UTC";
        }

        svc.TimeZone = identifier;
        svc._zoneKey = TimeZoneData.Canonicalize(identifier) ?? "UTC";

        Check("weekday", options.Weekday, "narrow", "short", "long");
        Check("era", options.Era, "narrow", "short", "long");
        Check("year", options.Year, "2-digit", "numeric");
        Check("month", options.Month, "2-digit", "numeric", "narrow", "short", "long");
        Check("day", options.Day, "2-digit", "numeric");
        Check("dayPeriod", options.DayPeriod, "narrow", "short", "long");
        Check("hour", options.Hour, "2-digit", "numeric");
        Check("minute", options.Minute, "2-digit", "numeric");
        Check("second", options.Second, "2-digit", "numeric");
        if (options.FractionalSecondDigits is { } fsd && fsd is < 1 or > 3)
        {
            throw IntlException.Range($"fractionalSecondDigits value is out of range.");
        }

        Check("timeZoneName", options.TimeZoneName, "short", "long", "shortOffset", "longOffset", "shortGeneric", "longGeneric");
        Check("formatMatcher", options.FormatMatcher, "basic", "best fit");
        Check("dateStyle", options.DateStyle, "full", "long", "medium", "short");
        Check("timeStyle", options.TimeStyle, "full", "long", "medium", "short");

        string hcDefault = HourCycleOfPattern(svc._data.GetString("gregorian", "timeFormats", "short") ?? "HH:mm");
        string? hc = options.Hour12 switch
        {
            true => hcKey is "h11" or "h12" ? hcKey : "h12",
            false => hcKey is "h23" or "h24" ? hcKey : "h23",
            null => hcKey ?? hcDefault,
        };
        bool hcRequested = options.Hour12 is not null || hcKey is not null;

        if (options.DateStyle is not null || options.TimeStyle is not null)
        {
            string? conflict = Conflict(options);
            if (conflict is not null)
            {
                throw IntlException.Type("Invalid option : option");
            }

            if ((required == "date" && options.TimeStyle is not null) || (required == "time" && options.DateStyle is not null))
            {
                throw IntlException.Type($"Invalid option : {(required == "date" ? "timeStyle" : "dateStyle")}");
            }

            svc.DateStyle = options.DateStyle;
            svc.TimeStyle = options.TimeStyle;
            svc.BuildStyled(options.DateStyle, options.TimeStyle, hc, hcRequested);
            return svc;
        }

        string? weekday = options.Weekday, era = options.Era, year = options.Year, month = options.Month, day = options.Day;
        string? dayPeriod = options.DayPeriod, hour = options.Hour, minute = options.Minute, second = options.Second;
        int? fraction = options.FractionalSecondDigits;
        bool needDefaults = true;
        if (required is "date" or "any" && (weekday, year, month, day) != (null, null, null, null))
        {
            needDefaults = false;
        }

        if (required is "time" or "any" && (dayPeriod, hour, minute, second, fraction) != (null, null, null, null, null))
        {
            needDefaults = false;
        }

        if (needDefaults && defaults is "date" or "all")
        {
            year = month = day = "numeric";
        }

        if (needDefaults && defaults is "time" or "all")
        {
            hour = minute = second = "numeric";
        }

        // Build the skeleton the options describe (ECMA-402 11.1.2 and UTS #35 skeleton letters).
        char hourLetter = hc switch { "h11" => 'K', "h12" => 'h', "h24" => 'k', _ => 'H' };
        var sk = new System.Text.StringBuilder();
        sk.Append(weekday switch { "narrow" => "EEEEE", "short" => "EEE", "long" => "EEEE", _ => "" });
        sk.Append(era switch { "narrow" => "GGGGG", "short" => "G", "long" => "GGGG", _ => "" });
        sk.Append(year switch { "2-digit" => "yy", "numeric" => "y", _ => "" });
        sk.Append(month switch { "2-digit" => "MM", "numeric" => "M", "narrow" => "MMMMM", "short" => "MMM", "long" => "MMMM", _ => "" });
        sk.Append(day switch { "2-digit" => "dd", "numeric" => "d", _ => "" });
        // With a 23- or 24-hour clock the reference engine ignores dayPeriod when an hour is shown.
        if (hour is not null && hc is "h23" or "h24")
        {
            dayPeriod = null;
        }

        sk.Append(dayPeriod switch { "narrow" => "BBBBB", "short" => "B", "long" => "BBBB", _ => "" });
        sk.Append(hour switch { "2-digit" => new string(hourLetter, 2), "numeric" => hourLetter.ToString(), _ => "" });
        sk.Append(minute switch { "2-digit" => "mm", "numeric" => "m", _ => "" });
        // Fractional digits need a second to follow: the reference engine adds one when a minute is shown without it.
        sk.Append(second switch { "2-digit" => "ss", "numeric" => "s", _ => fraction is not null && minute is not null ? "s" : "" });
        sk.Append(fraction is { } fd ? new string('S', fd) : "");
        sk.Append(options.TimeZoneName switch { "short" => "z", "long" => "zzzz", "shortOffset" => "O", "longOffset" => "OOOO", "shortGeneric" => "v", "longGeneric" => "vvvv", _ => "" });

        string pattern = DateTimePatternGenerator.For(r.DataLocale).GetBestPattern(sk.ToString());
        svc.SetPattern(ReplaceHourLetter(pattern, hourLetter, hour is not null));
        svc._skeleton = sk.ToString();
        svc._hourLetter = hourLetter;
        svc.ResolveFromPattern();

        // A range whose dates differ in a field the format does not show gets that field added (year, then month).
        svc._widen = (addYear, addMonth, addDay) => Create(locales, options with
        {
            Year = addYear && year is null ? "numeric" : options.Year,
            Month = addMonth && month is null ? "numeric" : options.Month,
            Day = addDay && day is null ? "numeric" : options.Day,
        }, defaultLocale, defaultTimeZone, zones, required, defaults);
        return svc;
    }

    private static string? Conflict(DateTimeFormatOptions o)
    {
        if (o.Weekday is not null) return "weekday";
        if (o.Era is not null) return "era";
        if (o.Year is not null) return "year";
        if (o.Month is not null) return "month";
        if (o.Day is not null) return "day";
        if (o.DayPeriod is not null) return "dayPeriod";
        if (o.Hour is not null) return "hour";
        if (o.Minute is not null) return "minute";
        if (o.Second is not null) return "second";
        if (o.FractionalSecondDigits is not null) return "fractionalSecondDigits";
        if (o.TimeZoneName is not null) return "timeZoneName";
        return null;
    }

    private static void Check(string name, string? value, params string[] allowed)
    {
        if (value is not null && Array.IndexOf(allowed, value) < 0)
        {
            throw IntlException.Range($"Value {value} out of range for Intl.DateTimeFormat options property {name}");
        }
    }

    /// <summary>Whether a string is a sequence of 3 to 8 character alphanumeric subtags (the Unicode locale <c>type</c> production).</summary>
    private static bool IsTypeSequence(string s)
    {
        foreach (string part in s.Split('-'))
        {
            if (part.Length is < 3 or > 8 || !part.All(char.IsAsciiLetterOrDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static string HourCycleOfPattern(string pattern)
    {
        foreach (PatternToken t in PatternToken.Parse(pattern))
        {
            switch (t.Letter)
            {
                case 'h': return "h12";
                case 'H': return "h23";
                case 'K': return "h11";
                case 'k': return "h24";
            }
        }

        return "h23";
    }

    /// <summary>Replaces the hour letter of a pattern by the one the hour cycle needs (<c>K</c> and <c>k</c> have no CLDR patterns of their own).</summary>
    private static string ReplaceHourLetter(string pattern, char letter, bool hasHour)
    {
        if (!hasHour)
        {
            return pattern;
        }

        var sb = new System.Text.StringBuilder();
        foreach (PatternToken t in PatternToken.Parse(pattern))
        {
            if (!t.IsField)
            {
                PatternToken.AppendLiteral(sb, t.Literal);
            }
            else
            {
                sb.Append(t.Letter is 'h' or 'H' or 'K' or 'k' ? letter : t.Letter, t.Count);
            }
        }

        return sb.ToString();
    }

    private void SetPattern(string pattern)
    {
        Pattern = pattern;
        _tokens = PatternToken.Parse(pattern);
        _hasMinute = _tokens.Any(t => t.Letter == 'm');
        _hasSecond = _tokens.Any(t => t.Letter == 's');
    }

    private void BuildStyled(string? dateStyle, string? timeStyle, string hc, bool hcRequested)
    {
        string? datePattern = dateStyle is null ? null : _data.GetString("gregorian", "dateFormats", dateStyle);
        string? timePattern = timeStyle is null ? null : _data.GetString("gregorian", "timeFormats", timeStyle);
        char hourLetter = hc switch { "h11" => 'K', "h12" => 'h', "h24" => 'k', _ => 'H' };
        if (timePattern is not null)
        {
            string current = HourCycleOfPattern(timePattern);
            if (hcRequested && current != hc)
            {
                // The hour cycle differs from the locale's: ask for the same fields with the other cycle.
                string skeleton = DateTimePatternGenerator.SkeletonOf(timePattern);
                var sk = new System.Text.StringBuilder();
                foreach (PatternToken t in PatternToken.Parse(skeleton))
                {
                    if (t.Letter == 'a')
                    {
                        continue;
                    }

                    sk.Append(t.Letter is 'h' or 'H' or 'K' or 'k' ? hourLetter : t.Letter, t.Count);
                }

                timePattern = ReplaceHourLetter(DateTimePatternGenerator.For(_dataLocale).GetBestPattern(sk.ToString()), hourLetter, true);
            }
            else
            {
                hc = current;
            }

            HourCycle = hc;
        }

        if (datePattern is not null && timePattern is not null)
        {
            int style = dateStyle switch { "full" => 0, "long" => 1, "medium" => 2, _ => 3 };
            string glue = DateTimePatternGenerator.For(_dataLocale).GlueFor(style);
            SetPattern(DateTimePatternGenerator.Glue(glue, timePattern, datePattern));
            _datePart = datePattern;
            _timePart = timePattern;
            _glue = DateTimePatternGenerator.For(_dataLocale).RangeGlueFor(style);
        }
        else
        {
            SetPattern(datePattern ?? timePattern ?? "");
        }
    }

    // The resolved component options are read back from the pattern text by looking for the longest known field string, quoted
    // literal text included (a Spanish "de" counts as a day field), which is what the reference engine does.
    private static readonly (string Needle, string Value)[] WeekdayItems = [("EEEEE", "narrow"), ("EEEE", "long"), ("EEE", "short"), ("ccccc", "narrow"), ("cccc", "long"), ("ccc", "short"), ("EE", "short"), ("E", "short")];
    private static readonly (string, string)[] EraItems = [("GGGGG", "narrow"), ("GGGG", "long"), ("GGG", "short"), ("GG", "short"), ("G", "short")];
    private static readonly (string, string)[] YearItems = [("yy", "2-digit"), ("y", "numeric")];
    private static readonly (string, string)[] MonthItems = [("MMMMM", "narrow"), ("MMMM", "long"), ("MMM", "short"), ("MM", "2-digit"), ("M", "numeric"), ("LLLLL", "narrow"), ("LLLL", "long"), ("LLL", "short"), ("LL", "2-digit"), ("L", "numeric")];
    private static readonly (string, string)[] DayItems = [("dd", "2-digit"), ("d", "numeric")];
    private static readonly (string, string)[] DayPeriodItems = [("BBBBB", "narrow"), ("bbbbb", "narrow"), ("BBBB", "long"), ("bbbb", "long"), ("B", "short"), ("b", "short")];
    private static readonly (string, string)[] HourItems = [("HH", "2-digit"), ("hh", "2-digit"), ("H", "numeric"), ("h", "numeric"), ("kk", "2-digit"), ("KK", "2-digit"), ("k", "numeric"), ("K", "numeric")];
    private static readonly (string, string)[] MinuteItems = [("mm", "2-digit"), ("m", "numeric")];
    private static readonly (string, string)[] SecondItems = [("ss", "2-digit"), ("s", "numeric")];
    private static readonly (string, string)[] ZoneItems = [("zzzz", "long"), ("z", "short"), ("OOOO", "longOffset"), ("O", "shortOffset"), ("vvvv", "longGeneric"), ("v", "shortGeneric")];

    private static string? Scan(string pattern, (string Needle, string Value)[] items)
    {
        foreach (var (needle, value) in items)
        {
            if (pattern.Contains(needle, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Reads the resolved component options back from the chosen pattern (ECMA-402 11.1.2: the pattern, not the request, decides).</summary>
    private void ResolveFromPattern()
    {
        Weekday = Scan(Pattern, WeekdayItems);
        Era = Scan(Pattern, EraItems);
        Year = Scan(Pattern, YearItems);
        Month = Scan(Pattern, MonthItems);
        Day = Scan(Pattern, DayItems);
        DayPeriod = Scan(Pattern, DayPeriodItems);
        Hour = Scan(Pattern, HourItems);
        Minute = Scan(Pattern, MinuteItems);
        Second = Scan(Pattern, SecondItems);
        TimeZoneName = Scan(Pattern, ZoneItems);
        int fraction = 0;
        foreach (PatternToken t in _tokens)
        {
            if (t.Letter == 'S')
            {
                fraction = t.Count;
            }
            else if (t.Letter is 'h' or 'H' or 'K' or 'k')
            {
                HourCycle = t.Letter switch { 'h' => "h12", 'K' => "h11", 'k' => "h24", _ => "h23" };
            }
        }

        FractionalSecondDigits = fraction > 0 ? fraction : null;
    }
}
