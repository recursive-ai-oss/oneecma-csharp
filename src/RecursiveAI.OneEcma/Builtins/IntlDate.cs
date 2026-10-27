using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma;

internal static partial class IntlJs
{
    // ---- Intl.DateTimeFormat (11) -----------------------------------------------------------------------------------

    /// <summary>CreateDateTimeFormat (ECMA-402 11.1.2): reads the options in the observable order, then hands them to the service.</summary>
    internal static DateTimeFormatService CreateDateTimeFormat(List<string> requested, JsValue optionsArg, string required, string defaults)
    {
        var o = new Opts(CoerceOptions(optionsArg), "DateTimeFormat");
        string? matcher = o.Str("localeMatcher", "lookup", "best fit");
        string? calendar = TypeOption(o, "calendar");
        string? nu = o.Str("numberingSystem");
        if (nu is not null)
        {
            Svc(() => LocaleResolver.ValidateNumberingSystem(nu, "DateTimeFormat"));
        }

        bool? hour12 = o.Bool("hour12");
        string? hourCycle = o.Str("hourCycle", "h11", "h12", "h23", "h24");
        string? timeZone = o.Str("timeZone");
        string? weekday = o.Str("weekday", "narrow", "short", "long");
        string? era = o.Str("era", "narrow", "short", "long");
        string? year = o.Str("year", "2-digit", "numeric");
        string? month = o.Str("month", "2-digit", "numeric", "narrow", "short", "long");
        string? day = o.Str("day", "2-digit", "numeric");
        string? dayPeriod = o.Str("dayPeriod", "narrow", "short", "long");
        string? hour = o.Str("hour", "2-digit", "numeric");
        string? minute = o.Str("minute", "2-digit", "numeric");
        string? second = o.Str("second", "2-digit", "numeric");
        int? fractional = o.Num("fractionalSecondDigits", 1, 3);
        string? timeZoneName = o.Str("timeZoneName", "short", "long", "shortOffset", "longOffset", "shortGeneric", "longGeneric");
        string? formatMatcher = o.Str("formatMatcher", "basic", "best fit");
        string? dateStyle = o.Str("dateStyle", "full", "long", "medium", "short");
        string? timeStyle = o.Str("timeStyle", "full", "long", "medium", "short");

        var options = new DateTimeFormatOptions
        {
            LocaleMatcher = matcher,
            Calendar = calendar,
            NumberingSystem = nu,
            Hour12 = hour12,
            HourCycle = hourCycle,
            TimeZone = timeZone,
            Weekday = weekday,
            Era = era,
            Year = year,
            Month = month,
            Day = day,
            DayPeriod = dayPeriod,
            Hour = hour,
            Minute = minute,
            Second = second,
            FractionalSecondDigits = fractional,
            TimeZoneName = timeZoneName,
            FormatMatcher = formatMatcher,
            DateStyle = dateStyle,
            TimeStyle = timeStyle,
        };
        return Svc(() => DateTimeFormatService.Create(requested, options, DefaultLocale(), DefaultTimeZone(), Zones, required, defaults));
    }

    private static double TimeArg(JsValue v) => v.IsUndefined ? Agent.Current.Host.CurrentTimeMs() : Ops.ToNumber(v);

    private static JsValue DatePartsArray(Realm r, IEnumerable<DateTimePart> parts, bool withSource)
    {
        var list = new List<JsValue>();
        foreach (DateTimePart p in parts)
        {
            list.Add(JsValue.FromObject(withSource ? Part(r, p.Type, p.Value, ("source", p.Source)) : Part(r, p.Type, p.Value)));
        }

        return JsValue.FromObject(Ops.CreateArray(list));
    }

    private static void SetupDateTimeFormat(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "DateTimeFormat", 0, true, (t, a, nt, called) =>
        {
            JsIntl dtf = Allocate(r, nt, "DateTimeFormat");
            dtf.Data = CreateDateTimeFormat(Locales(A(a, 0)), A(a, 1), "any", "date");
            return Chain(r, dtf, "DateTimeFormat", t, called);
        }, out JsObject proto);

        r.Getter(proto, "format", "format", (t, a, nt) =>
        {
            JsIntl dtf = Unwrap(r, t, "DateTimeFormat", "format");
            dtf.Bound ??= new NativeFunction(r, string.Empty, 1, (t2, a2, nt2) =>
            {
                double x = TimeArg(A(a2, 0));
                return Ops.Str(Svc(() => ((DateTimeFormatService)dtf.Data!).Format(x)));
            });
            return JsValue.FromObject(dtf.Bound);
        });
        r.Method(proto, "formatToParts", 1, (t, a, nt) =>
        {
            var dtf = (DateTimeFormatService)This(t, "DateTimeFormat", "formatToParts").Data!;
            double x = TimeArg(A(a, 0));
            return DatePartsArray(r, Svc(() => dtf.FormatToParts(x)), false);
        });
        r.Method(proto, "formatRange", 2, (t, a, nt) =>
        {
            var dtf = (DateTimeFormatService)This(t, "DateTimeFormat", "formatRange").Data!;
            (double x, double y) = DateRangeArgs(A(a, 0), A(a, 1));
            return Ops.Str(Svc(() => dtf.FormatRange(x, y)));
        });
        r.Method(proto, "formatRangeToParts", 2, (t, a, nt) =>
        {
            var dtf = (DateTimeFormatService)This(t, "DateTimeFormat", "formatRangeToParts").Data!;
            (double x, double y) = DateRangeArgs(A(a, 0), A(a, 1));
            return DatePartsArray(r, Svc(() => dtf.FormatRangeToParts(x, y)), true);
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var d = (DateTimeFormatService)Unwrap(r, t, "DateTimeFormat", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", d.Locale);
            Put(o, "calendar", DateTimeFormatService.Calendar);
            Put(o, "numberingSystem", DateTimeFormatService.NumberingSystem);
            Put(o, "timeZone", d.TimeZone);
            Put(o, "hourCycle", d.HourCycle);
            Put(o, "hour12", d.Hour12);
            bool styled = d.DateStyle is not null || d.TimeStyle is not null;
            if (!styled)
            {
                Put(o, "weekday", d.Weekday);
                Put(o, "era", d.Era);
                Put(o, "year", d.Year);
                Put(o, "month", d.Month);
                Put(o, "day", d.Day);
                Put(o, "dayPeriod", d.DayPeriod);
                Put(o, "hour", d.Hour);
                Put(o, "minute", d.Minute);
                Put(o, "second", d.Second);
                Put(o, "fractionalSecondDigits", d.FractionalSecondDigits);
                Put(o, "timeZoneName", d.TimeZoneName);
            }

            Put(o, "dateStyle", d.DateStyle);
            Put(o, "timeStyle", d.TimeStyle);
            return JsValue.FromObject(o);
        });
    }

    private static (double, double) DateRangeArgs(JsValue start, JsValue end)
    {
        if (start.IsUndefined || end.IsUndefined)
        {
            throw Errors.Type("Invalid time value");
        }

        double x = Ops.ToNumber(start);
        double y = Ops.ToNumber(end);
        return (x, y);
    }

    // ---- Intl.DurationFormat (13) -----------------------------------------------------------------------------------

    private static readonly string[] LongShortNarrow = ["long", "short", "narrow"];

    private static void SetupDurationFormat(Realm r, JsObject intl)
    {
        MakeCtor(r, intl, "DurationFormat", 0, false, (t, a, nt, called) =>
        {
            JsIntl df = Allocate(r, nt, "DurationFormat");
            List<string> requested = Locales(A(a, 0));
            var o = new Opts(GetOptionsObject(A(a, 1)), "DurationFormat");
            string? matcher = o.Str("localeMatcher", "lookup", "best fit");
            string? nu = o.Str("numberingSystem");
            if (nu is not null)
            {
                Svc(() => LocaleResolver.ValidateNumberingSystem(nu, "DurationFormat"));
            }

            string? style = o.Str("style", "long", "short", "narrow", "digital");
            var unitOptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string unit in DurationRecord.UnitNames)
            {
                string[] styles = unit switch
                {
                    "years" or "months" or "weeks" or "days" => LongShortNarrow,
                    "hours" or "minutes" or "seconds" => ["long", "short", "narrow", "numeric", "2-digit"],
                    _ => ["long", "short", "narrow", "numeric"],
                };
                if (o.Str(unit, styles) is { } s)
                {
                    unitOptions[unit] = s;
                }

                if (o.Str(unit + "Display", "auto", "always") is { } d)
                {
                    unitOptions[unit + "Display"] = d;
                }
            }

            int? fractionalDigits = o.Num("fractionalDigits", 0, 9);
            var options = new DurationFormatOptions
            {
                LocaleMatcher = matcher,
                NumberingSystem = nu,
                Style = style,
                UnitOptions = unitOptions,
                FractionalDigits = fractionalDigits,
            };
            df.Data = Svc(() => DurationFormatService.Create(requested, options, DefaultLocale()));
            return JsValue.FromObject(df);
        }, out JsObject proto);

        r.Method(proto, "format", 1, (t, a, nt) =>
        {
            var df = (DurationFormatService)This(t, "DurationFormat", "format").Data!;
            DurationRecord rec = ToDurationRecord(A(a, 0));
            return Ops.Str(Svc(() => df.Format(rec)));
        });
        r.Method(proto, "formatToParts", 1, (t, a, nt) =>
        {
            var df = (DurationFormatService)This(t, "DurationFormat", "formatToParts").Data!;
            DurationRecord rec = ToDurationRecord(A(a, 0));
            var parts = new List<JsValue>();
            foreach (DurationPart p in Svc(() => df.FormatToParts(rec)))
            {
                parts.Add(JsValue.FromObject(Part(r, p.Type, p.Value, ("unit", p.Unit))));
            }

            return JsValue.FromObject(Ops.CreateArray(parts));
        });
        r.Method(proto, "resolvedOptions", 0, (t, a, nt) =>
        {
            var df = (DurationFormatService)This(t, "DurationFormat", "resolvedOptions").Data!;
            var o = r.NewObject();
            Put(o, "locale", df.Locale);
            Put(o, "numberingSystem", df.NumberingSystem);
            Put(o, "style", df.Style);
            for (int i = 0; i < DurationRecord.UnitNames.Length; i++)
            {
                DurationUnitOptions u = df.Unit(i);
                Put(o, DurationRecord.UnitNames[i], u.Style);
                Put(o, DurationRecord.UnitNames[i] + "Display", u.Display);
            }

            Put(o, "fractionalDigits", df.FractionalDigits);
            return JsValue.FromObject(o);
        });
    }

    /// <summary>ToDurationRecord (ECMA-402 13.5.3): ten properties read in unit order, each an integral Number.</summary>
    private static DurationRecord ToDurationRecord(JsValue duration)
    {
        if (duration.Ref is not JsObject obj)
        {
            throw duration.IsString ? Errors.Range("Invalid duration: a string is not a duration") : Errors.Type("Invalid duration: not an object");
        }

        var fields = new double?[DurationRecord.UnitNames.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            JsValue v = obj.Get(DurationRecord.UnitNames[i]);
            if (v.IsUndefined)
            {
                continue;
            }

            double d = Ops.ToNumber(v);
            if (!double.IsFinite(d) || Math.Truncate(d) != d)
            {
                throw Errors.Range($"Invalid duration: {DurationRecord.UnitNames[i]} must be an integer");
            }

            fields[i] = d;
        }

        return Svc(() => DurationRecord.Create(fields));
    }
}
