using System.Text.Json;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>A zone source that answers from the offsets recorded next to each corpus row, so the test does not depend on the host's tzdata.</summary>
internal sealed class RecordedZoneSource : ITimeZoneSource
{
    private readonly Dictionary<(string, double), TimeZoneOffset> _map = [];

    internal RecordedZoneSource(JsonElement row)
    {
        if (!row.TryGetProperty("z", out JsonElement z) || !row.TryGetProperty("o", out JsonElement o) || o.Str("timeZone") is not { } zone)
        {
            return;
        }

        string canonical = TimeZoneData.Canonicalize(zone) ?? zone;
        foreach (JsonProperty p in z.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                _map[(canonical, double.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture))] = new TimeZoneOffset((int)p.Value[0].GetDouble(), p.Value[1].GetDouble() != 0);
            }
        }
    }

    public bool TryGetOffset(string zoneId, double epochMs, out TimeZoneOffset offset) => _map.TryGetValue((zoneId, epochMs), out offset);
}

/// <summary>DateTimeFormat, formatRange and DurationFormat against the recorded Node output.</summary>
public class IntlDateTimeDifferentialTests
{
    internal static DateTimeFormatOptions Options(JsonElement o) => new()
    {
        Calendar = o.Str("calendar"),
        NumberingSystem = o.Str("numberingSystem"),
        Hour12 = o.TryGetProperty("hour12", out JsonElement h12) && h12.ValueKind is JsonValueKind.True or JsonValueKind.False ? h12.GetBoolean() : null,
        HourCycle = o.Str("hourCycle"),
        TimeZone = o.Str("timeZone"),
        Weekday = o.Str("weekday"),
        Era = o.Str("era"),
        Year = o.Str("year"),
        Month = o.Str("month"),
        Day = o.Str("day"),
        DayPeriod = o.Str("dayPeriod"),
        Hour = o.Str("hour"),
        Minute = o.Str("minute"),
        Second = o.Str("second"),
        FractionalSecondDigits = o.Int("fractionalSecondDigits"),
        TimeZoneName = o.Str("timeZoneName"),
        FormatMatcher = o.Str("formatMatcher"),
        LocaleMatcher = o.Str("localeMatcher"),
        DateStyle = o.Str("dateStyle"),
        TimeStyle = o.Str("timeStyle"),
    };

    private static DateTimeFormatService Make(JsonElement row)
    {
        JsonElement o = row.GetProperty("o");
        return DateTimeFormatService.Create([row.GetProperty("l").GetString()!], Options(o), "en-US", "UTC", new RecordedZoneSource(row));
    }

    private static string Resolved(DateTimeFormatService s)
    {
        var d = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["hourCycle"] = s.HourCycle,
            ["hour12"] = s.Hour12 is { } b ? (b ? "true" : "false") : null,
            ["weekday"] = s.Weekday,
            ["era"] = s.Era,
            ["year"] = s.Year,
            ["month"] = s.Month,
            ["day"] = s.Day,
            ["dayPeriod"] = s.DayPeriod,
            ["hour"] = s.Hour,
            ["minute"] = s.Minute,
            ["second"] = s.Second,
            ["fractionalSecondDigits"] = s.FractionalSecondDigits?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["timeZoneName"] = s.TimeZoneName,
            ["dateStyle"] = s.DateStyle,
            ["timeStyle"] = s.TimeStyle,
        };
        return string.Join(";", d.Where(kv => kv.Value is not null).Select(kv => kv.Key + "=" + kv.Value));
    }

    private static string Resolved(JsonElement r)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty p in r.EnumerateObject())
        {
            d[p.Name] = p.Value.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => p.Value.GetRawText(), _ => p.Value.GetString()! };
        }

        return string.Join(";", d.Select(kv => kv.Key + "=" + kv.Value));
    }

    private static void AssertClean(string name, (int Total, int Matched, int Documented, List<string> Failures) r) =>
        Assert.True(r.Failures.Count == 0, $"{name}: {r.Failures.Count} of {r.Total} rows differ ({r.Matched} match, {r.Documented} documented):\n" + string.Join("\n", r.Failures.Take(25)));

    [Fact]
    public void DateTimeFormatMatchesNode()
    {
        AssertClean("date-time-format", IntlCorpus.Run("date-time-format", IntlCorpus.Rows("date-time-format.jsonl"), row =>
        {
            string kind = row.GetProperty("s").GetString()!;
            switch (kind)
            {
                case "tzsv":
                {
                    string[] expected = [.. row.GetProperty("p").EnumerateArray().Select(x => x.GetString()!)];
                    string[] actual = IntlEnumeration.SupportedValuesOf("timeZone");
                    string[] missing = [.. expected.Except(actual)], extra = [.. actual.Except(expected)];
                    return missing.Length + extra.Length == 0 ? null : $"missing {string.Join(",", missing.Take(8))} extra {string.Join(",", extra.Take(8))} ({expected.Length} vs {actual.Length})";
                }

                case "tzc":
                {
                    string? actual = TimeZoneData.Canonicalize(row.GetProperty("o").GetProperty("timeZone").GetString()!);
                    if (row.TryGetProperty("e", out _))
                    {
                        return actual is null ? null : $"expected RangeError, got {actual}";
                    }

                    string expected = row.GetProperty("p").GetString()!;
                    return actual == expected ? null : $"expected {expected} got {actual}";
                }

                case "dtfv":
                {
                    DateTimeFormatService s = DateTimeFormatService.Create(["en-US"], new DateTimeFormatOptions(), "en-US", "UTC", new RecordedZoneSource(row));
                    double x = row.GetProperty("v").GetString() switch { "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity, "8.64e15+1" => 8.64e15 + 2, _ => -8.64e15 - 2 };
                    try
                    {
                        s.Format(x);
                    }
                    catch (IntlException)
                    {
                        return null;
                    }

                    return "expected RangeError";
                }

                case "dtfo":
                {
                    DateTimeFormatService s;
                    try
                    {
                        s = Make(row);
                    }
                    catch (IntlException x) when (row.TryGetProperty("e", out JsonElement e))
                    {
                        string cls = x.Kind == IntlErrorKind.Range ? "RangeError" : "TypeError";
                        if (cls != e.GetString())
                        {
                            return $"threw {cls}, expected {e.GetString()}";
                        }

                        string? m = row.Str("m");
                        return m is null || m == x.Message ? null : $"message '{x.Message}' expected '{m}'";
                    }

                    if (row.TryGetProperty("e", out JsonElement exp))
                    {
                        return $"expected {exp.GetString()}";
                    }

                    string a = Resolved(s), b = Resolved(row.GetProperty("r"));
                    return a == b ? null : $"resolved {a} expected {b}";
                }

                default:
                {
                    DateTimeFormatService s = Make(row);
                    double t = row.GetProperty("v").GetDouble();
                    string actual = IntlCorpus.PartsText(s.FormatToParts(t).Select(p => (p.Type, p.Value)));
                    string expected = IntlCorpus.PartsText(row.GetProperty("p"));
                    if (actual != expected)
                    {
                        return $"[{s.Pattern}] expected {expected} got {actual}";
                    }

                    string ra = Resolved(s), rb = Resolved(row.GetProperty("r"));
                    return ra == rb ? null : $"[{s.Pattern}] resolved {ra} expected {rb}";
                }
            }
        }));
    }

    [Fact]
    public void DateTimeRangeMatchesNode()
    {
        AssertClean("date-time-range", IntlCorpus.Run("date-time-range", IntlCorpus.Rows("date-time-range.jsonl"), row =>
        {
            DateTimeFormatService s = Make(row);
            JsonElement v = row.GetProperty("v");
            string actual = string.Join("|", s.FormatRangeToParts(v[0].GetDouble(), v[1].GetDouble()).Select(p => p.Type + ":" + p.Value + ":" + p.Source));
            string expected = string.Join("|", row.GetProperty("p").EnumerateArray().Select(p => p[0].GetString() + ":" + p[1].GetString() + ":" + p[2].GetString()));
            return actual == expected ? null : $"[{s.Pattern}] expected {expected} got {actual}";
        }));
    }
}
