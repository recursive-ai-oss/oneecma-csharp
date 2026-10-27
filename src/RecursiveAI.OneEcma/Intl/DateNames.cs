using System.Collections.Concurrent;
using System.Globalization;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Intl;

/// <summary>
/// The names a Gregorian pattern needs for one locale: months, weekdays, eras, am/pm and flexible day periods
/// (CLDR <c>ca-gregorian</c> and the supplemental day period rules). Loaded once per locale.
/// </summary>
internal sealed class DateNames
{
    private static readonly ConcurrentDictionary<string, DateNames> Cache = new(StringComparer.Ordinal);
    private static readonly string[] DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] Widths = ["abbreviated", "wide", "narrow", "short"];

    private readonly string[][] _months = new string[2 * 3][];     // [standalone * 3 + width], width: abbreviated, wide, narrow
    private readonly string[][] _days = new string[2 * 4][];       // [standalone * 4 + width], width: abbreviated, wide, narrow, short
    private readonly string[][] _eras = new string[3][];           // abbreviated, wide, narrow; index 0 = BC, 1 = AD
    private readonly Dictionary<string, string>[] _periods = new Dictionary<string, string>[3];   // format: abbreviated, wide, narrow
    private readonly List<(string Name, int From, int Before)> _rules = [];
    private readonly bool _hasNoon;

    private DateNames(DataTree d)
    {
        for (int s = 0; s < 2; s++)
        {
            string ctx = s == 0 ? "format" : "stand-alone";
            for (int w = 0; w < 3; w++)
            {
                var months = new string[12];
                for (int m = 0; m < 12; m++)
                {
                    months[m] = d.GetString("gregorian", "months", ctx, Widths[w], (m + 1).ToString(NumberFormatInfo.InvariantInfo)) ?? "";
                }

                _months[(s * 3) + w] = months;
            }

            for (int w = 0; w < 4; w++)
            {
                var days = new string[7];
                for (int i = 0; i < 7; i++)
                {
                    days[i] = d.GetString("gregorian", "days", ctx, Widths[w], DayKeys[i]) ?? "";
                }

                _days[(s * 4) + w] = days;
            }
        }

        string[] eraKeys = ["eraAbbr", "eraNames", "eraNarrow"];
        for (int i = 0; i < 3; i++)
        {
            _eras[i] = [d.GetString("gregorian", "eras", eraKeys[i], "0") ?? "", d.GetString("gregorian", "eras", eraKeys[i], "1") ?? ""];
        }

        for (int w = 0; w < 3; w++)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in d.GetKeys("gregorian", "dayPeriods", "format", Widths[w]))
            {
                if (d.GetString("gregorian", "dayPeriods", "format", Widths[w], key) is { } v)
                {
                    map[key] = v;
                }
            }

            _periods[w] = map;
        }

        // Flexible day period rules: by language, e.g. "en" or "es" (the regional data locale falls back to its language).
        DataTree sup = CldrData.Supplemental;
        string lang = d.Id.Split('-')[0];
        string? rulesKey = sup.GetKeys("dayPeriodRules").Contains(d.Id.Replace('-', '_')) ? d.Id.Replace('-', '_') : sup.GetKeys("dayPeriodRules").Contains(lang) ? lang : null;
        if (rulesKey is not null)
        {
            foreach (string period in sup.GetKeys("dayPeriodRules", rulesKey))
            {
                if (period == "noon")
                {
                    _hasNoon = true;
                }
                else if (sup.GetString("dayPeriodRules", rulesKey, period, "_from") is { } from && sup.GetString("dayPeriodRules", rulesKey, period, "_before") is { } before)
                {
                    _rules.Add((period, Minutes(from), Minutes(before)));
                }
            }
        }
    }

    private static int Minutes(string hhmm) => (int.Parse(hhmm[..2], NumberFormatInfo.InvariantInfo) * 60) + int.Parse(hhmm[3..5], NumberFormatInfo.InvariantInfo);

    internal static DateNames For(string dataLocale, DataTree data) => Cache.GetOrAdd(dataLocale, _ => new DateNames(data));

    private static int WidthIndex(int count) => count switch { <= 3 => 0, 4 => 1, _ => 2 };

    internal string Era(int count, bool ad) => _eras[WidthIndex(count)][ad ? 1 : 0];

    internal string Month(int month, int count, bool standalone) => _months[((standalone ? 1 : 0) * 3) + WidthIndex(count)][month - 1];

    internal string Weekday(int weekday, int count, bool standalone) =>
        _days[((standalone ? 1 : 0) * 4) + (count == 6 ? 3 : WidthIndex(count))][weekday];

    internal string AmPm(bool pm, int count) => Period(pm ? "pm" : "am", count) ?? "";

    private string? Period(string key, int count) => _periods[WidthIndex(count)].TryGetValue(key, out string? v) ? v : null;

    /// <summary>Pattern letter <c>b</c>: am, pm, and noon or midnight at exactly 12:00 and 00:00.</summary>
    internal string AmPmNoonMidnight(int hour, int minute, int second, int count)
    {
        if (minute == 0 && second == 0)
        {
            if (hour == 12 && Period("noon", count) is { } noon)
            {
                return noon;
            }

            if (hour == 0 && Period("midnight", count) is { } midnight)
            {
                return midnight;
            }
        }

        return AmPm(hour >= 12, count);
    }

    /// <summary>
    /// Pattern letter <c>B</c>: the flexible day period (morning, afternoon, ...) of the locale's rules (UTS #35, Day Periods).
    /// Noon is used at exactly 12:00; midnight is not used, the hour 0 belongs to the night.
    /// </summary>
    internal string Flexible(int hour, int minute, int second, int count)
    {
        if (hour == 12 && minute == 0 && second == 0 && _hasNoon && Period("noon", count) is { } noon)
        {
            return noon;
        }

        int at = (hour * 60) + minute;
        foreach (var (name, from, before) in _rules)
        {
            bool inside = from < before ? at >= from && at < before : at >= from || at < before;
            if (inside && Period(name, count) is { } v)
            {
                return v;
            }
        }

        return AmPm(hour >= 12, count);
    }
}
