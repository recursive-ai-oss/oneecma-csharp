using System.Text;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma;

/// <summary>A Date instance: an ordinary object with a [[DateValue]] internal slot (21.4.5).</summary>
internal sealed class JsDate : JsObject
{
    internal JsDate(JsObject? proto, double tv)
        : base(proto)
    {
        Tv = tv;
    }

    /// <summary>The time value: milliseconds since the epoch, or NaN for an invalid date.</summary>
    internal double Tv;

    internal override string ClassName => "Date";
}

/// <summary>Adapts the host's zone lookup to the Intl services.</summary>
internal sealed class HostZoneSource(IJsHost host) : ITimeZoneSource
{
    public bool TryGetOffset(string zoneId, double epochMs, out TimeZoneOffset offset)
    {
        bool known = host.TryGetTimeZoneOffset(zoneId, epochMs, out int ms, out bool dst);
        offset = new TimeZoneOffset(ms, dst);
        return known;
    }
}

internal static partial class Builtins
{
    private static readonly string[] DayAbbrev = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] MonthAbbrev = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static JsDate ThisDate(JsValue t) =>
        t.Ref as JsDate ?? throw Errors.Type("this is not a Date object.");

    private static string Pad(long v, int width)
    {
        // v is never negative here; digits are written by hand so no culture is involved.
        var digits = new StringBuilder();
        do
        {
            digits.Insert(0, (char)('0' + (int)(v % 10)));
            v /= 10;
        }
        while (v > 0);
        return digits.Length >= width ? digits.ToString() : new string('0', width - digits.Length) + digits;
    }

    private static string YearString(long y) => (y < 0 ? "-" : "") + Pad(Math.Abs(y), 4);

    /// <summary>DateString(tv) (21.4.4.41.2), for a local time value.</summary>
    private static string DateString(DateFields f) => $"{DayAbbrev[f.WeekDay]} {MonthAbbrev[f.Month]} {Pad(f.Date, 2)} {YearString(f.Year)}";

    private static string TimeString(DateFields f) => $"{Pad(f.Hour, 2)}:{Pad(f.Minute, 2)}:{Pad(f.Second, 2)} GMT";

    /// <summary>TimeZoneString(tv) (21.4.4.41.3): the offset and the zone's long English name from the CLDR data.</summary>
    private static string TimeZoneString(IJsHost host, double tv)
    {
        TimeZoneOffset off = DateMath.ZoneOffset(host, tv);
        long minutes = (long)Math.Round(Math.Abs(off.OffsetMs) / 60000.0, MidpointRounding.AwayFromZero);
        string result = (off.OffsetMs >= 0 ? "+" : "-") + Pad(minutes / 60, 2) + Pad(minutes % 60, 2);
        string zone = TimeZoneData.Canonicalize(host.TimeZoneId) ?? "UTC";
        string name = TimeZoneNames.Format(
            Intl.Data.CldrData.GetLocale("en"), zone, Math.Clamp(tv, 0, 253402297200000), off, ZoneNameStyle.Long,
            t => host.TryGetTimeZoneOffset(host.TimeZoneId, t, out int ms, out bool dst) ? new TimeZoneOffset(ms, dst) : null);
        return result + " (" + name + ")";
    }

    /// <summary>ToDateString(tv) (21.4.4.41.4).</summary>
    private static string ToDateStringFull(IJsHost host, double tv)
    {
        if (double.IsNaN(tv))
        {
            return "Invalid Date";
        }

        DateFields f = DateMath.Decompose(DateMath.LocalTime(host, tv));
        return DateString(f) + " " + TimeString(f) + TimeZoneString(host, tv);
    }

    private static string IsoString(double tv)
    {
        DateFields f = DateMath.Decompose(tv);
        string year = f.Year is >= 0 and <= 9999 ? Pad(f.Year, 4) : (f.Year < 0 ? "-" : "+") + Pad(Math.Abs(f.Year), 6);
        return $"{year}-{Pad(f.Month + 1, 2)}-{Pad(f.Date, 2)}T{Pad(f.Hour, 2)}:{Pad(f.Minute, 2)}:{Pad(f.Second, 2)}.{Pad(f.Millisecond, 3)}Z";
    }

    private static string UtcString(double tv)
    {
        DateFields f = DateMath.Decompose(tv);
        return $"{DayAbbrev[f.WeekDay]}, {Pad(f.Date, 2)} {MonthAbbrev[f.Month]} {YearString(f.Year)} {Pad(f.Hour, 2)}:{Pad(f.Minute, 2)}:{Pad(f.Second, 2)} GMT";
    }

    /// <summary>The numeric fields of a Date(...) or Date.UTC(...) call with at least a year: MakeFullYear applied, then MakeDay and MakeTime (21.4.2.1, 21.4.3.4).</summary>
    private static double TimeFromComponents(ReadOnlySpan<JsValue> a)
    {
        double y = Ops.ToNumber(A(a, 0));
        double m = a.Length > 1 ? Ops.ToNumber(a[1]) : 0;
        double dt = a.Length > 2 ? Ops.ToNumber(a[2]) : 1;
        double h = a.Length > 3 ? Ops.ToNumber(a[3]) : 0;
        double min = a.Length > 4 ? Ops.ToNumber(a[4]) : 0;
        double s = a.Length > 5 ? Ops.ToNumber(a[5]) : 0;
        double milli = a.Length > 6 ? Ops.ToNumber(a[6]) : 0;
        return DateMath.MakeDate(DateMath.MakeDay(DateMath.MakeFullYear(y), m, dt), DateMath.MakeTime(h, min, s, milli));
    }

    private static void SetupDate(Realm r)
    {
        var proto = r.NewObject();
        r.Intrinsics["%Date.prototype%"] = proto;
        IJsHost Host() => r.Agent.Host;

        NativeFunction ctor = r.Ctor("Date", 7, (t, a, nt) =>
        {
            IJsHost host = Host();
            if (nt is null)
            {
                // 21.4.2.1 step 1: called as a function, the result is the string for the current time.
                return Ops.Str(ToDateStringFull(host, DateMath.TimeClip(host.CurrentTimeMs())));
            }

            double tv;
            if (a.Length == 0)
            {
                tv = host.CurrentTimeMs();
            }
            else if (a.Length == 1)
            {
                if (a[0].Ref is JsDate other)
                {
                    tv = other.Tv;
                }
                else
                {
                    JsValue v = Ops.ToPrimitive(a[0]);
                    tv = v.IsString || v.Ref is Rope ? DateParser.Parse(Ops.ToStr(v), host) : DateMath.TimeClip(Ops.ToNumber(v));
                }

                tv = DateMath.TimeClip(tv);
            }
            else
            {
                tv = DateMath.TimeClip(DateMath.UtcFromLocal(host, TimeFromComponents(a)));
            }

            JsObject p = Ops.GetPrototypeFromConstructor(nt, x => x.Intrinsics["%Date.prototype%"]);
            return JsValue.FromObject(new JsDate(p, tv));
        }, proto);

        r.Method(ctor, "now", 0, (t, a, nt) => Ops.Num(DateMath.TimeClip(Host().CurrentTimeMs())));
        r.Method(ctor, "parse", 1, (t, a, nt) => Ops.Num(DateMath.TimeClip(DateParser.Parse(Ops.ToStr(A(a, 0)), Host()))));
        r.Method(ctor, "UTC", 7, (t, a, nt) => Ops.Num(DateMath.TimeClip(TimeFromComponents(a))));

        // ---- getters (21.4.4.2 to 21.4.4.20) ----
        void Getter(string name, bool local, Func<DateFields, double> pick) =>
            r.Method(proto, name, 0, (t, a, nt) =>
            {
                double tv = ThisDate(t).Tv;
                if (double.IsNaN(tv))
                {
                    return Ops.Num(double.NaN);
                }

                return Ops.Num(pick(DateMath.Decompose(local ? DateMath.LocalTime(Host(), tv) : tv)));
            });

        foreach (bool local in new[] { true, false })
        {
            string u = local ? "get" : "getUTC";
            Getter(u + "Date", local, f => f.Date);
            Getter(u + "Day", local, f => f.WeekDay);
            Getter(u + "FullYear", local, f => f.Year);
            Getter(u + "Hours", local, f => f.Hour);
            Getter(u + "Milliseconds", local, f => f.Millisecond);
            Getter(u + "Minutes", local, f => f.Minute);
            Getter(u + "Month", local, f => f.Month);
            Getter(u + "Seconds", local, f => f.Second);
        }

        r.Method(proto, "getTime", 0, (t, a, nt) => Ops.Num(ThisDate(t).Tv));
        r.Method(proto, "valueOf", 0, (t, a, nt) => Ops.Num(ThisDate(t).Tv));
        r.Method(proto, "getTimezoneOffset", 0, (t, a, nt) =>
        {
            double tv = ThisDate(t).Tv;
            return Ops.Num(double.IsNaN(tv) ? double.NaN : (tv - DateMath.LocalTime(Host(), tv)) / 60000);
        });

        // ---- setters (21.4.4.20 to 21.4.4.37) ----
        // `first` indexes the field order year, month, date, hour, minute, second, millisecond.
        void Setter(string name, int length, int first, bool local) =>
            r.Method(proto, name, length, (t, a, nt) =>
            {
                JsDate d = ThisDate(t);
                double tv = d.Tv;
                var vals = new double[length];
                vals[0] = Ops.ToNumber(A(a, 0));
                for (int k = 1; k < length && k < a.Length; k++)
                {
                    vals[k] = Ops.ToNumber(a[k]);
                }

                int given = Math.Max(1, Math.Min(a.Length, length));
                IJsHost host = Host();
                if (double.IsNaN(tv))
                {
                    if (first != 0)
                    {
                        return Ops.Num(double.NaN);
                    }

                    tv = 0; // setFullYear and setUTCFullYear start from +0 (no LocalTime)
                }
                else if (local)
                {
                    tv = DateMath.LocalTime(host, tv);
                }

                DateFields f = DateMath.Decompose(tv);
                double[] p = [f.Year, f.Month, f.Date, f.Hour, f.Minute, f.Second, f.Millisecond];
                for (int k = 0; k < given; k++)
                {
                    p[first + k] = vals[k];
                }

                double date = DateMath.MakeDate(DateMath.MakeDay(p[0], p[1], p[2]), DateMath.MakeTime(p[3], p[4], p[5], p[6]));
                double v = DateMath.TimeClip(local ? DateMath.UtcFromLocal(host, date) : date);
                d.Tv = v;
                return Ops.Num(v);
            });

        foreach (bool local in new[] { true, false })
        {
            string u = local ? "set" : "setUTC";
            Setter(u + "Milliseconds", 1, 6, local);
            Setter(u + "Seconds", 2, 5, local);
            Setter(u + "Minutes", 3, 4, local);
            Setter(u + "Hours", 4, 3, local);
            Setter(u + "Date", 1, 2, local);
            Setter(u + "Month", 2, 1, local);
            Setter(u + "FullYear", 3, 0, local);
        }

        r.Method(proto, "setTime", 1, (t, a, nt) =>
        {
            JsDate d = ThisDate(t);
            return Ops.Num(d.Tv = DateMath.TimeClip(Ops.ToNumber(A(a, 0))));
        });

        // ---- string conversions ----
        r.Method(proto, "toString", 0, (t, a, nt) => Ops.Str(ToDateStringFull(Host(), ThisDate(t).Tv)));
        r.Method(proto, "toDateString", 0, (t, a, nt) =>
        {
            double tv = ThisDate(t).Tv;
            return Ops.Str(double.IsNaN(tv) ? "Invalid Date" : DateString(DateMath.Decompose(DateMath.LocalTime(Host(), tv))));
        });
        r.Method(proto, "toTimeString", 0, (t, a, nt) =>
        {
            double tv = ThisDate(t).Tv;
            return Ops.Str(double.IsNaN(tv) ? "Invalid Date" : TimeString(DateMath.Decompose(DateMath.LocalTime(Host(), tv))) + TimeZoneString(Host(), tv));
        });
        NativeFunction utcString = r.Method(proto, "toUTCString", 0, (t, a, nt) =>
        {
            double tv = ThisDate(t).Tv;
            return Ops.Str(double.IsNaN(tv) ? "Invalid Date" : UtcString(tv));
        });
        // Annex B.2.3.3: toGMTString is the same function object as toUTCString.
        proto.DefineDirect("toGMTString", JsValue.FromObject(utcString), PropFlags.Hidden);
        r.Method(proto, "toISOString", 0, (t, a, nt) =>
        {
            double tv = ThisDate(t).Tv;
            return double.IsNaN(tv) ? throw Errors.Range("Invalid time value") : Ops.Str(IsoString(tv));
        });
        r.Method(proto, "toJSON", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            JsValue tv = Ops.ToPrimitive(JsValue.FromObject(o), "number");
            if (tv.IsNumber && !double.IsFinite(tv.Num))
            {
                return JsValue.Null;
            }

            return Ops.Invoke(JsValue.FromObject(o), new PropertyKey("toISOString"), default);
        });
        r.Method(proto, WellKnown.ToPrimitive, "[Symbol.toPrimitive]", 1, (t, a, nt) =>
        {
            if (t.Ref is not JsObject o)
            {
                throw Errors.Type("Date.prototype[Symbol.toPrimitive] called on non-object");
            }

            JsValue hint = A(a, 0);
            string h = hint.IsString || hint.Ref is Rope ? Ops.ToStr(hint) : "";
            if (h is not ("string" or "default" or "number"))
            {
                throw Errors.Type("Invalid hint: " + (hint.IsObject ? Ops.DisplayValue(hint) : Ops.ToStr(hint)));
            }

            return Ops.OrdinaryToPrimitive(o, h == "number" ? "number" : "string");
        }, PropFlags.Configurable);

        r.Method(proto, "toLocaleString", 0, (t, a, nt) => LocaleDate(Host(), ThisDate(t).Tv, a, "any", "all"));
        r.Method(proto, "toLocaleDateString", 0, (t, a, nt) => LocaleDate(Host(), ThisDate(t).Tv, a, "date", "date"));
        r.Method(proto, "toLocaleTimeString", 0, (t, a, nt) => LocaleDate(Host(), ThisDate(t).Tv, a, "time", "time"));

        // ---- Annex B.2.3 ----
        r.Method(proto, "getYear", 0, (t, a, nt) =>
        {
            double tv = ThisDate(t).Tv;
            return Ops.Num(double.IsNaN(tv) ? double.NaN : DateMath.Decompose(DateMath.LocalTime(Host(), tv)).Year - 1900);
        });
        r.Method(proto, "setYear", 1, (t, a, nt) =>
        {
            JsDate d = ThisDate(t);
            double tv = d.Tv;
            double y = Ops.ToNumber(A(a, 0));
            tv = double.IsNaN(tv) ? 0 : DateMath.LocalTime(Host(), tv);
            double yyyy = DateMath.MakeFullYear(y);
            DateFields f = DateMath.Decompose(tv);
            double day = DateMath.MakeDay(yyyy, f.Month, f.Date);
            double date = DateMath.MakeDate(day, DateMath.TimeWithinDay(tv));
            return Ops.Num(d.Tv = DateMath.TimeClip(DateMath.UtcFromLocal(Host(), date)));
        });
    }

    // ---- toLocale*String through the Intl date-time service (ECMA-402 19.3.1 and friends) ----

    private static string? OptString(JsObject o, string name)
    {
        JsValue v = o.Get(name);
        return v.IsUndefined ? null : Ops.ToStr(v);
    }

    private static JsValue LocaleDate(IJsHost host, double tv, ReadOnlySpan<JsValue> a, string required, string defaults)
    {
        if (double.IsNaN(tv))
        {
            return Ops.Str("Invalid Date");
        }

        try
        {
            var locales = new List<string>();
            JsValue l = A(a, 0);
            if (l.IsString || l.Ref is Rope)
            {
                locales.Add(Ops.ToStr(l));
            }
            else if (l.Ref is JsObject lo)
            {
                foreach (JsValue item in Ops.ListFromArrayLike(JsValue.FromObject(lo)))
                {
                    locales.Add(Ops.ToStr(item));
                }
            }
            else if (!l.IsUndefined)
            {
                throw Errors.Type("Incorrect locale information provided");
            }

            var opts = new DateTimeFormatOptions();
            if (A(a, 1).Ref is JsObject o)
            {
                opts = new DateTimeFormatOptions
                {
                    LocaleMatcher = OptString(o, "localeMatcher"),
                    Calendar = OptString(o, "calendar"),
                    NumberingSystem = OptString(o, "numberingSystem"),
                    Hour12 = o.Get("hour12") is { IsUndefined: false } h12 ? Ops.ToBoolean(h12) : null,
                    HourCycle = OptString(o, "hourCycle"),
                    TimeZone = OptString(o, "timeZone"),
                    Weekday = OptString(o, "weekday"),
                    Era = OptString(o, "era"),
                    Year = OptString(o, "year"),
                    Month = OptString(o, "month"),
                    Day = OptString(o, "day"),
                    DayPeriod = OptString(o, "dayPeriod"),
                    Hour = OptString(o, "hour"),
                    Minute = OptString(o, "minute"),
                    Second = OptString(o, "second"),
                    FractionalSecondDigits = o.Get("fractionalSecondDigits") is { IsUndefined: false } fd ? (int)Math.Clamp(Ops.ToNumber(fd), 0, 9) : null,
                    TimeZoneName = OptString(o, "timeZoneName"),
                    FormatMatcher = OptString(o, "formatMatcher"),
                    DateStyle = OptString(o, "dateStyle"),
                    TimeStyle = OptString(o, "timeStyle"),
                };
            }
            else if (!A(a, 1).IsUndefined && !A(a, 1).IsNull)
            {
                // ToObject on a primitive gives no readable options.
            }
            else if (A(a, 1).IsNull)
            {
                throw Errors.Type("Cannot convert undefined or null to object");
            }

            DateTimeFormatService svc = DateTimeFormatService.Create(
                LocaleResolver.CanonicalizeLocaleList(locales), opts, host.DefaultLocale, host.TimeZoneId, new HostZoneSource(host), required, defaults);
            // Engines print U+0020 where the CLDR time patterns have U+202F, and code compares these strings, so follow them.
            return Ops.Str(svc.Format(tv).Replace('\u202f', ' '));
        }
        catch (IntlException e)
        {
            throw e.Kind == IntlErrorKind.Range ? Errors.Range(e.Message) : Errors.Type(e.Message);
        }
    }
}
