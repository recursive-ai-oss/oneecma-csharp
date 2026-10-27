using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma;

/// <summary>The broken-down fields of a time value.</summary>
internal readonly record struct DateFields(long Year, int Month, int Date, int WeekDay, int Hour, int Minute, int Second, int Millisecond);

/// <summary>
/// The time value abstract operations of ECMA-262 21.4.1, as exact double arithmetic over the full +/-8.64e15 range, and the
/// local-time operations built on the host's zone offsets (21.4.1.24 to 21.4.1.26).
/// </summary>
internal static class DateMath
{
    internal const double MsPerDay = 86400000;
    internal const double MaxTime = 8.64e15;

    // One Gregorian cycle is 400 years = 146097 days, a whole number of weeks, so calendar and weekday repeat exactly.
    private const double CycleMs = 146097 * MsPerDay;

    // The zone sources cover years 1 to 9999; later instants are mapped back by whole cycles (rules do not change that far out).
    private const double FoldAbove = 253402300799000.0 - (2 * CycleMs);

    // ---- 21.4.1.3 to 21.4.1.13: days, years, months ---------------------------------------------------

    /// <summary>Day(t) (21.4.1.3).</summary>
    internal static double Day(double t) => Math.Floor(t / MsPerDay);

    /// <summary>TimeWithinDay(t) (21.4.1.3).</summary>
    internal static double TimeWithinDay(double t)
    {
        double r = t % MsPerDay;
        return r < 0 ? r + MsPerDay : r;
    }

    /// <summary>Days since 1970-01-01 of a proleptic Gregorian date (month 1 to 12).</summary>
    internal static long DaysFromCivil(long y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - (era * 400);
        long doy = (((153 * (m + (m > 2 ? -3 : 9))) + 2) / 5) + d - 1;
        long doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
        return (era * 146097) + doe - 719468;
    }

    private static void CivilFromDays(long z, out long y, out int m, out int d)
    {
        z += 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - (era * 146097);
        long yoe = (doe - (doe / 1460) + (doe / 36524) - (doe / 146096)) / 365;
        y = yoe + (era * 400);
        long doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        long mp = ((5 * doy) + 2) / 153;
        d = (int)(doy - (((153 * mp) + 2) / 5) + 1);
        m = (int)(mp < 10 ? mp + 3 : mp - 9);
        y += m <= 2 ? 1 : 0;
    }

    /// <summary>YearFromTime, MonthFromTime, DateFromTime, WeekDay, HourFromTime and the rest (21.4.1.4 to 21.4.1.13) for a finite time value.</summary>
    internal static DateFields Decompose(double t)
    {
        double day = Day(t);
        double within = TimeWithinDay(t);
        CivilFromDays((long)day, out long y, out int m, out int d);
        long w = (long)within;
        int weekday = (int)(((((long)day + 4) % 7) + 7) % 7);
        return new DateFields(y, m - 1, d, weekday, (int)(w / 3600000), (int)(w / 60000 % 60), (int)(w / 1000 % 60), (int)(w % 1000));
    }

    // ---- 21.4.1.27 to 21.4.1.31: MakeTime, MakeDay, MakeDate, MakeFullYear, TimeClip -----------------------

    private static double Trunc(double v) => Math.Truncate(v) + 0.0;

    /// <summary>MakeTime(hour, min, sec, ms) (21.4.1.27).</summary>
    internal static double MakeTime(double hour, double min, double sec, double ms)
    {
        if (!double.IsFinite(hour) || !double.IsFinite(min) || !double.IsFinite(sec) || !double.IsFinite(ms))
        {
            return double.NaN;
        }

        return (Trunc(hour) * 3600000) + (Trunc(min) * 60000) + (Trunc(sec) * 1000) + Trunc(ms);
    }

    /// <summary>MakeDay(year, month, date) (21.4.1.28).</summary>
    internal static double MakeDay(double year, double month, double date)
    {
        if (!double.IsFinite(year) || !double.IsFinite(month) || !double.IsFinite(date))
        {
            return double.NaN;
        }

        double y = Trunc(year), m = Trunc(month), dt = Trunc(date);
        double ym = y + Math.Floor(m / 12);
        if (!double.IsFinite(ym) || Math.Abs(ym) > 400000)
        {
            // No time value in range has such a year.
            return double.NaN;
        }

        int mn = (int)(((m % 12) + 12) % 12);
        double days = DaysFromCivil((long)ym, mn + 1, 1);
        return days + dt - 1;
    }

    /// <summary>MakeDate(day, time) (21.4.1.29).</summary>
    internal static double MakeDate(double day, double time)
    {
        if (!double.IsFinite(day) || !double.IsFinite(time))
        {
            return double.NaN;
        }

        double tv = (day * MsPerDay) + time;
        return double.IsFinite(tv) ? tv : double.NaN;
    }

    /// <summary>MakeFullYear(year) (21.4.1.30): two-digit years count from 1900.</summary>
    internal static double MakeFullYear(double year)
    {
        if (double.IsNaN(year))
        {
            return double.NaN;
        }

        double truncated = Trunc(year);
        return truncated is >= 0 and <= 99 ? 1900 + truncated : truncated;
    }

    /// <summary>TimeClip(time) (21.4.1.31).</summary>
    internal static double TimeClip(double time)
    {
        if (!double.IsFinite(time) || Math.Abs(time) > MaxTime)
        {
            return double.NaN;
        }

        return Trunc(time);
    }

    // ---- 21.4.1.24 to 21.4.1.26: local time through the host ------------------------------------------

    private static TimeZoneOffset OffsetAt(IJsHost host, double epochMs)
    {
        // The sources answer for years 1 to 9999; far-future instants take the offset of the same moment in an equivalent cycle.
        while (epochMs > FoldAbove)
        {
            epochMs -= CycleMs;
        }

        return host.TryGetTimeZoneOffset(host.TimeZoneId, epochMs, out int ms, out bool dst) ? new TimeZoneOffset(ms, dst) : new TimeZoneOffset(0, false);
    }

    /// <summary>The offset in milliseconds of the host zone at the UTC instant <paramref name="t"/>.</summary>
    internal static double OffsetMs(IJsHost host, double t) => OffsetAt(host, t).OffsetMs;

    /// <summary>The host zone's offset record at the UTC instant <paramref name="t"/>.</summary>
    internal static TimeZoneOffset ZoneOffset(IJsHost host, double t) => OffsetAt(host, t);

    /// <summary>LocalTime(t) (21.4.1.26): t plus the offset of the host zone at t.</summary>
    internal static double LocalTime(IJsHost host, double t) => t + OffsetMs(host, t);

    /// <summary>
    /// UTC(t) (21.4.1.26) for a local time value. When the local time occurs twice (clocks set back) the earlier instant is
    /// used; when it never occurs (clocks set forward) it is read with the offset from before the transition (GetUTCEpochNanoseconds).
    /// </summary>
    internal static double UtcFromLocal(IJsHost host, double t)
    {
        if (!double.IsFinite(t))
        {
            return double.NaN;
        }

        // Transitions are further apart than a day, so the offsets a day either side bracket the one in force.
        double before = OffsetMs(host, t - MsPerDay);
        double after = OffsetMs(host, t + MsPerDay);
        double best = double.NaN;
        foreach (double o in before == after ? [before] : new[] { before, after })
        {
            double u = t - o;
            if (OffsetMs(host, u) == o && (double.IsNaN(best) || u < best))
            {
                best = u;
            }
        }

        return double.IsNaN(best) ? t - before : best;
    }
}
