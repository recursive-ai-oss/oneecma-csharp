using System.Globalization;
using System.Text;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>One piece of a formatted date (ECMA-402 <c>formatToParts</c> element); <see cref="Source"/> is set for range formatting.</summary>
internal readonly record struct DateTimePart(string Type, string Value, string? Source = null);

/// <summary>Inputs for <see cref="DateTimeFormatService"/> (the Intl.DateTimeFormat constructor options). Null means undefined.</summary>
internal sealed record DateTimeFormatOptions
{
    internal string? LocaleMatcher { get; init; }
    internal string? Calendar { get; init; }
    internal string? NumberingSystem { get; init; }

    /// <summary><c>hour12</c>; when set, <see cref="HourCycle"/> is ignored (ECMA-402 11.1.2).</summary>
    internal bool? Hour12 { get; init; }

    internal string? HourCycle { get; init; }
    internal string? TimeZone { get; init; }
    internal string? Weekday { get; init; }
    internal string? Era { get; init; }
    internal string? Year { get; init; }
    internal string? Month { get; init; }
    internal string? Day { get; init; }
    internal string? DayPeriod { get; init; }
    internal string? Hour { get; init; }
    internal string? Minute { get; init; }
    internal string? Second { get; init; }

    /// <summary>The integer 1 to 3, already converted from the option value.</summary>
    internal int? FractionalSecondDigits { get; init; }

    internal string? TimeZoneName { get; init; }
    internal string? FormatMatcher { get; init; }
    internal string? DateStyle { get; init; }
    internal string? TimeStyle { get; init; }
}

/// <summary>
/// Intl.DateTimeFormat (ECMA-402 section 11) over CLDR patterns for the Gregorian calendar and Latin digits. The instant is
/// epoch milliseconds, the zone offset comes from an <see cref="ITimeZoneSource"/>; nothing reads the machine clock or zone.
/// </summary>
internal sealed partial class DateTimeFormatService
{
    private readonly ITimeZoneSource _zones;
    private DataTree _data = null!;
    private List<PatternToken> _tokens = [];
    private bool _hasMinute, _hasSecond;
    private string _dataLocale = "";

    private DateTimeFormatService(ITimeZoneSource zones) => _zones = zones;

    /// <summary>The resolved locale tag.</summary>
    internal string Locale { get; private set; } = "";

    /// <summary>Always <c>gregory</c>.</summary>
    internal static string Calendar => "gregory";

    /// <summary>Always <c>latn</c>.</summary>
    internal static string NumberingSystem => "latn";

    /// <summary>The canonical time zone id (an IANA name, <c>UTC</c> or an offset such as <c>+05:30</c>).</summary>
    internal string TimeZone { get; private set; } = "UTC";

    /// <summary>The resolved <c>hourCycle</c> (<c>h11</c>, <c>h12</c>, <c>h23</c>, <c>h24</c>); null when the format shows no hour.</summary>
    internal string? HourCycle { get; private set; }

    /// <summary>True for 12-hour cycles; null when <see cref="HourCycle"/> is null.</summary>
    internal bool? Hour12 => HourCycle is null ? null : HourCycle is "h11" or "h12";

    internal string? Weekday { get; private set; }
    internal string? Era { get; private set; }
    internal string? Year { get; private set; }
    internal string? Month { get; private set; }
    internal string? Day { get; private set; }
    internal string? DayPeriod { get; private set; }
    internal string? Hour { get; private set; }
    internal string? Minute { get; private set; }
    internal string? Second { get; private set; }
    internal int? FractionalSecondDigits { get; private set; }
    internal string? TimeZoneName { get; private set; }
    internal string? DateStyle { get; private set; }
    internal string? TimeStyle { get; private set; }

    /// <summary>The CLDR pattern in use, for example <c>MMM d, y</c>.</summary>
    internal string Pattern { get; private set; } = "";

    // ---------------------------------------------------------------- formatting

    /// <summary>Formats an instant (epoch milliseconds) as one string.</summary>
    /// <exception cref="IntlException">The value is not a finite time value within the Date range (RangeError).</exception>
    internal string Format(double epochMs) => Join(FormatToParts(epochMs));

    /// <summary>Formats an instant as typed parts (ECMA-402 11.5.5 PartitionDateTimePattern).</summary>
    /// <exception cref="IntlException">The value is not a finite time value within the Date range (RangeError).</exception>
    internal List<DateTimePart> FormatToParts(double epochMs)
    {
        double t = CheckTime(epochMs);
        return FormatWith(_tokens, t, Fields(t), null);
    }

    internal static string Join(List<DateTimePart> parts)
    {
        var sb = new StringBuilder();
        foreach (DateTimePart p in parts)
        {
            sb.Append(p.Value);
        }

        return sb.ToString();
    }

    internal static double CheckTime(double epochMs)
    {
        if (!double.IsFinite(epochMs) || Math.Abs(epochMs) > 8.64e15)
        {
            throw IntlException.Range("Invalid time value");
        }

        return Math.Truncate(epochMs) + 0.0;
    }

    /// <summary>The calendar fields of an instant in the format's time zone.</summary>
    internal readonly record struct LocalFields(long Year, int Month, int Day, int Weekday, int Hour, int Minute, int Second, int Millisecond, TimeZoneOffset Offset, double Instant)
    {
        /// <summary>Year of era: 1 BC is year 0 of the proleptic Gregorian calendar.</summary>
        internal long YearOfEra => Year > 0 ? Year : 1 - Year;

        internal bool IsAd => Year > 0;
    }

    internal LocalFields Fields(double t)
    {
        TimeZoneOffset off;
        if (TimeZoneData.IsOffsetId(TimeZone))
        {
            off = new TimeZoneOffset(TimeZoneData.OffsetOf(TimeZone), false);
        }
        else if (!_zones.TryGetOffset(TimeZone, t, out off))
        {
            throw IntlException.Range($"Invalid time zone specified: {TimeZone}");
        }

        double local = t + off.OffsetMs;
        double dayD = Math.Floor(local / 86400000.0);
        int msInDay = (int)(local - (dayD * 86400000.0));
        long days = (long)dayD;
        // Days to civil date in the proleptic Gregorian calendar.
        long z = days + 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - (era * 146097);
        long yoe = (doe - (doe / 1460) + (doe / 36524) - (doe / 146096)) / 365;
        long y = yoe + (era * 400);
        long doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        long mp = ((5 * doy) + 2) / 153;
        int d = (int)(doy - (((153 * mp) + 2) / 5) + 1);
        int m = (int)(mp < 10 ? mp + 3 : mp - 9);
        if (m <= 2)
        {
            y++;
        }

        int weekday = (int)(((days % 7) + 11) % 7);   // 1970-01-01 was a Thursday (4)
        return new LocalFields(y, m, d, weekday, msInDay / 3600000, msInDay / 60000 % 60, msInDay / 1000 % 60, msInDay % 1000, off, t);
    }

    internal List<DateTimePart> FormatWith(List<PatternToken> tokens, double t, LocalFields f, string? source)
    {
        var parts = new List<DateTimePart>(tokens.Count);
        foreach (PatternToken tok in tokens)
        {
            if (!tok.IsField)
            {
                parts.Add(new DateTimePart("literal", tok.Literal, source));
                continue;
            }

            (string type, string value) = Field(tok, f);
            if (value.Length > 0)
            {
                parts.Add(new DateTimePart(type, value, source));
            }
        }

        return parts;
    }

    private static string Pad(long n, int width)
    {
        string s = n.ToString(CultureInfo.InvariantCulture);
        return s.Length >= width ? s : new string('0', width - s.Length) + s;
    }

    private (string Type, string Value) Field(PatternToken tok, LocalFields f)
    {
        int n = tok.Count;
        DateNames names = DateNames.For(_dataLocale, _data);
        switch (tok.Letter)
        {
            case 'G':
                return ("era", names.Era(n, f.IsAd));
            case 'y':
            case 'Y':
            case 'u':
            case 'r':
                if (tok.Letter == 'u')
                {
                    return ("year", Pad(f.Year, n));
                }

                return ("year", n == 2 ? Pad(f.YearOfEra % 100, 2) : Pad(f.YearOfEra, n));
            case 'M':
            case 'L':
                return ("month", n <= 2 ? Pad(f.Month, n) : names.Month(f.Month, n, tok.Letter == 'L'));
            case 'd':
                return ("day", Pad(f.Day, n));
            case 'E':
                return ("weekday", names.Weekday(f.Weekday, n, false));
            case 'c':
            case 'e':
                return ("weekday", n <= 2 ? Pad(f.Weekday == 0 ? 7 : f.Weekday, n) : names.Weekday(f.Weekday, n, tok.Letter == 'c'));
            case 'a':
                return ("dayPeriod", names.AmPm(f.Hour >= 12, n));
            case 'b':
                return ("dayPeriod", names.AmPmNoonMidnight(f.Hour, f.Minute, f.Second, n));
            case 'B':
                return ("dayPeriod", names.Flexible(f.Hour, _hasMinute ? f.Minute : 0, _hasSecond ? f.Second : 0, n));
            case 'h':
                return ("hour", Pad(f.Hour % 12 == 0 ? 12 : f.Hour % 12, n));
            case 'H':
                return ("hour", Pad(f.Hour, n));
            case 'K':
                return ("hour", Pad(f.Hour % 12, n));
            case 'k':
                return ("hour", Pad(f.Hour == 0 ? 24 : f.Hour, n));
            case 'm':
                return ("minute", Pad(f.Minute, n));
            case 's':
                return ("second", Pad(f.Second, n));
            case 'S':
            {
                // Digits are truncated, never rounded (ECMA-402 11.5.5 step on fractionalSecondDigits).
                string ms = Pad(f.Millisecond, 3);
                return ("fractionalSecond", n <= 3 ? ms[..n] : ms + new string('0', n - 3));
            }

            case 'z':
                return ("timeZoneName", ZoneName(n >= 4 ? ZoneNameStyle.Long : ZoneNameStyle.Short, f));
            case 'v':
                return ("timeZoneName", ZoneName(n >= 4 ? ZoneNameStyle.LongGeneric : ZoneNameStyle.ShortGeneric, f));
            case 'O':
                return ("timeZoneName", ZoneName(n >= 4 ? ZoneNameStyle.LongOffset : ZoneNameStyle.ShortOffset, f));
            case 'Z':
            case 'V':
            case 'X':
            case 'x':
                return ("timeZoneName", ZoneName(ZoneNameStyle.LongOffset, f));
            default:
                return ("unknown", new string(tok.Letter, n));
        }
    }

    private string ZoneName(ZoneNameStyle style, LocalFields f) =>
        TimeZoneNames.Format(_data, TimeZone, f.Instant, f.Offset, style, t => _zones.TryGetOffset(TimeZone, t, out TimeZoneOffset o) ? o : null);
}
