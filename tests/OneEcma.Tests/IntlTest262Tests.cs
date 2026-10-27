using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Replays the data checks that <c>tools/intl-test262/translate.js</c> extracts from test262's intl402
/// tests (a call and the literal the test compares it with) against the Intl services. Set
/// ONEECMA_INTL_TEST262_CASES to the translator's output; otherwise the test is skipped.
/// Checks for locales the engine does not carry (ja-JP, zh-TW, ...) are skipped by design.
/// </summary>
public class IntlTest262Tests
{
    [Fact]
    public void TranslatedChecksPass()
    {
        string? path = Environment.GetEnvironmentVariable("ONEECMA_INTL_TEST262_CASES");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip("Set ONEECMA_INTL_TEST262_CASES to the output of tools/intl-test262/translate.js.");
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var expectedFailures = new HashSet<string>(StringComparer.Ordinal);
        string expFile = Path.Combine(RepoPaths.Root, "testdata", "intl", "test262-expectations.txt");
        if (File.Exists(expFile))
        {
            foreach (string line in File.ReadLines(expFile))
            {
                if (line.Length > 0 && line[0] != '#')
                {
                    expectedFailures.Add(line.Split(' ')[0]);
                }
            }
        }

        var perDir = new SortedDictionary<string, int[]>(StringComparer.Ordinal); // pass, fail, documented, skipped
        var failures = new List<string>();
        var report = new StringBuilder();
        foreach (JsonElement file in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            string name = file.GetProperty("file").GetString()!;
            string dir = string.Join('/', name.Split('/').Take(2));
            if (!perDir.TryGetValue(dir, out int[]? counts))
            {
                perDir[dir] = counts = new int[4];
            }

            int index = 0;
            foreach (JsonElement check in file.GetProperty("checks").EnumerateArray())
            {
                string id = $"{name}#{index++}";
                string? problem;
                try
                {
                    problem = Replay(check);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    problem = "engine threw " + e.GetType().Name + ": " + e.Message;
                }

                if (problem == "skip")
                {
                    counts[3]++;
                }
                else if (problem is null)
                {
                    counts[0]++;
                }
                else if (expectedFailures.Contains(name) || expectedFailures.Contains(id))
                {
                    counts[2]++;
                    report.AppendLine($"documented {id}: {problem}");
                }
                else
                {
                    counts[1]++;
                    failures.Add($"{id}: {problem}");
                    report.AppendLine($"FAIL {id}: {problem}");
                }
            }
        }

        var summary = new StringBuilder();
        foreach (var (dir, c) in perDir)
        {
            summary.AppendLine($"{dir,-40} pass {c[0],5}  fail {c[1],4}  documented {c[2],4}  skipped(locale/shape) {c[3],4}");
        }

        summary.AppendLine($"{"total",-40} pass {perDir.Values.Sum(c => c[0]),5}  fail {perDir.Values.Sum(c => c[1]),4}  documented {perDir.Values.Sum(c => c[2]),4}  skipped {perDir.Values.Sum(c => c[3]),4}");
        if (Environment.GetEnvironmentVariable("ONEECMA_INTL_REPORT") is { Length: > 0 } rep)
        {
            Directory.CreateDirectory(rep);
            File.WriteAllText(Path.Combine(rep, "test262-summary.txt"), summary.ToString());
            File.WriteAllText(Path.Combine(rep, "test262-details.txt"), report.ToString());
        }

        Assert.True(failures.Count == 0, summary + string.Join("\n", failures.Take(40)));
    }

    private static string[] Locales(JsonElement check)
    {
        JsonElement l = check.GetProperty("loc");
        return l.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.String => [l.GetString()!],
            _ => [.. l.EnumerateArray().Select(x => x.GetString()!)],
        };
    }

    // The locale tags must reach data the engine has: bare languages of the six, or exact regional tags.
    private static bool Supported(string[] requested)
    {
        if (requested.Length == 0)
        {
            return true;
        }

        try
        {
            List<string> canon = LocaleResolver.CanonicalizeLocaleList(requested);
            string? found = LocaleResolver.Match(canon).Locale;
            if (found is null)
            {
                return false;
            }

            // A tag that names a region must find that region's data (a bare language is served by any of its locales).
            LocaleId? first = LocaleId.Parse(LocaleResolver.SplitUnicodeExtension(canon[0]).WithoutExtension);
            return first?.Region is null || string.Equals(first.ToString(), found, StringComparison.Ordinal);
        }
        catch (IntlException)
        {
            return true; // invalid tags are errors in every locale set
        }
    }

    private static string? Replay(JsonElement c)
    {
        string svc = c.GetProperty("svc").GetString()!;
        string method = c.GetProperty("method").GetString()!;
        JsonElement expect = c.GetProperty("expect");
        string tExpect = expect.GetProperty("t").GetString()!;
        // Abrupt completions from user-defined getters and non-string locale arguments belong to the JS-facing layer.
        if ((tExpect == "err" && expect.GetProperty("v").GetString() == "Test262Error")
            || (c.GetProperty("loc").ValueKind == JsonValueKind.Object))
        {
            return "skip";
        }

        string[] req = Locales(c);
        if (tExpect != "err" && !Supported(req))
        {
            return "skip";
        }

        try
        {
            string? actual = Evaluate(svc, method, c, req, out bool unsupportedShape);
            if (unsupportedShape)
            {
                return "skip";
            }

            if (tExpect == "err")
            {
                return $"expected {expect.GetProperty("v").GetString()}, got {actual ?? "undefined"}";
            }

            string expected = tExpect switch
            {
                "s" => expect.GetProperty("v").GetString()!,
                "n" => expect.GetProperty("v").GetString()!,
                "b" => expect.GetProperty("v").GetBoolean() ? "true" : "false",
                "u" => "undefined",
                "arr" => string.Join(",", expect.GetProperty("v").EnumerateArray().Select(x => x.GetString())),
                _ => "?",
            };
            if (svc == "dtf" && actual is not null)
            {
                (actual, expected) = (Spaces(actual), Spaces(expected));
            }

            return actual == expected || (actual is null && expected == "undefined") ? null : $"expected \"{expected}\" got \"{actual}\"";
        }
        catch (IntlException e) when (tExpect == "err")
        {
            string kind = e.Kind == IntlErrorKind.Range ? "RangeError" : "TypeError";
            return kind == expect.GetProperty("v").GetString() ? null : $"expected {expect.GetProperty("v").GetString()}, threw {kind}";
        }
    }

    private static DecimalNumber Arg(JsonElement args, int i) =>
        args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= i ? DecimalNumber.NaN : IntlCorpus.Value(args[i].GetString()!);

    private static readonly BclTimeZoneSource Zones = new();

    // The reference's format() prints U+0020 where its formatToParts keeps the narrow no-break space of the CLDR patterns, and the
    // expected strings were recorded from format(); the two spellings are compared as equal.
    private static string Spaces(string s) => s.Replace('\u202f', ' ');

    // GetNumberOption (ECMA-402 9.2.?): ToNumber, a RangeError outside the range, floor.
    private static int? NumberOption(JsonElement o, string name, int min, int max)
    {
        if (!o.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }

        double d = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.TryParse(v.GetString(), NumberStyles.Float, NumberFormatInfo.InvariantInfo, out double parsed) ? parsed : double.NaN;
        if (double.IsNaN(d) || d < min || d > max)
        {
            throw IntlException.Range($"{name} value is out of range.");
        }

        return (int)Math.Floor(d);
    }

    private static string? Evaluate(string svc, string method, JsonElement c, string[] req, out bool unsupportedShape)
    {
        unsupportedShape = false;
        JsonElement o = c.GetProperty("opts");
        JsonElement args = c.TryGetProperty("args", out JsonElement a) ? a : default;
        List<string> locales = LocaleResolver.CanonicalizeLocaleList(req);
        string Inv(int n) => n.ToString(NumberFormatInfo.InvariantInfo);
        switch (svc)
        {
            case "nf":
            {
                NumberFormatService f = NumberFormatService.Create(locales, IntlNumberDifferentialTests.NfOptions(o), "en-US");
                switch (method)
                {
                    case "ctor":
                        return null;
                    case "format":
                        return f.Format(Arg(args, 0));
                    case "formatRange":
                        return f.FormatRange(Arg(args, 0), Arg(args, 1));
                    case "resolvedOptions":
                        DigitOptions d = f.Digits;
                        switch (c.GetProperty("prop").GetString())
                        {
                            case "locale": return f.Locale;
                            case "numberingSystem": return f.NumberingSystem;
                            case "style": return f.Style;
                            case "currency": return f.Currency;
                            case "currencyDisplay": return f.CurrencyDisplay;
                            case "currencySign": return f.CurrencySign;
                            case "unit": return f.Unit;
                            case "unitDisplay": return f.UnitDisplay;
                            case "notation": return f.Notation;
                            case "compactDisplay": return f.Notation == "compact" ? f.CompactDisplay : null;
                            case "signDisplay": return f.SignDisplay;
                            case "useGrouping": return f.UseGrouping;
                            case "minimumIntegerDigits": return Inv(d.MinimumIntegerDigits);
                            case "minimumFractionDigits": return d.UsesFractionDigits ? Inv(d.MinimumFractionDigits) : null;
                            case "maximumFractionDigits": return d.UsesFractionDigits ? Inv(d.MaximumFractionDigits) : null;
                            case "minimumSignificantDigits": return d.UsesSignificantDigits ? Inv(d.MinimumSignificantDigits) : null;
                            case "maximumSignificantDigits": return d.UsesSignificantDigits ? Inv(d.MaximumSignificantDigits) : null;
                            case "roundingIncrement": return Inv(d.RoundingIncrement);
                            case "roundingMode": return d.RoundingModeName;
                            case "roundingPriority": return d.RoundingPriority;
                            case "trailingZeroDisplay": return d.TrailingZeroDisplay;
                        }

                        break;
                }

                break;
            }

            case "pr":
            {
                PluralRulesService p = PluralRulesService.Create(locales, new PluralRulesOptions { LocaleMatcher = o.Str("localeMatcher"), Notation = o.Str("notation"), Type = o.Str("type"), Digits = IntlNumberDifferentialTests.Digits(o) }, "en-US");
                switch (method)
                {
                    case "ctor":
                        return null;
                    case "select":
                        return p.Select(Arg(args, 0));
                    case "selectRange":
                        return p.SelectRange(Arg(args, 0), Arg(args, 1));
                    case "resolvedOptions":
                        string prop = c.GetProperty("prop").GetString()!;
                        if (prop == "locale")
                        {
                            return p.Locale;
                        }

                        if (prop == "type")
                        {
                            return p.Type;
                        }

                        break;
                }

                break;
            }

            case "lf":
            {
                ListFormatService l = ListFormatService.Create(locales, o.Str("type"), o.Str("style"), "en-US", o.Str("localeMatcher"));
                switch (method)
                {
                    case "ctor":
                        return null;
                    case "format":
                        if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.Object)
                        {
                            return l.Format([.. args[0].GetProperty("a").EnumerateArray().Select(x => x.GetString()!)]);
                        }

                        break;
                    case "resolvedOptions":
                        return c.GetProperty("prop").GetString() switch { "locale" => l.Locale, "type" => l.Type, "style" => l.Style, _ => null };
                }

                break;
            }

            case "rtf":
            {
                RelativeTimeFormatService r = RelativeTimeFormatService.Create(locales, o.Str("style"), o.Str("numeric"), "en-US", o.Str("localeMatcher"), o.Str("numberingSystem"));
                switch (method)
                {
                    case "ctor":
                        return null;
                    case "format":
                        if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.String)
                        {
                            return r.Format(Arg(args, 0), args[1].GetString()![2..]);
                        }

                        break;
                    case "resolvedOptions":
                        return c.GetProperty("prop").GetString() switch { "locale" => r.Locale, "style" => r.Style, "numeric" => r.Numeric, "numberingSystem" => RelativeTimeFormatService.NumberingSystem, _ => null };
                }

                break;
            }

            case "dn":
            {
                var dopt = new DisplayNamesOptions { LocaleMatcher = o.Str("localeMatcher"), Style = o.Str("style"), Type = o.Str("type"), Fallback = o.Str("fallback"), LanguageDisplay = o.Str("languageDisplay") };
                DisplayNamesService n = DisplayNamesService.Create(locales, dopt, "en-US");
                switch (method)
                {
                    case "ctor":
                        return null;
                    case "of":
                        if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.String)
                        {
                            return n.Of(args[0].GetString()![2..]);
                        }

                        break;
                    case "resolvedOptions":
                        return c.GetProperty("prop").GetString() switch { "locale" => n.Locale, "style" => n.Style, "type" => n.Type, "fallback" => n.Fallback, "languageDisplay" => n.LanguageDisplay, _ => null };
                }

                break;
            }

            case "dtf":
            {
                // The option layer converts fractionalSecondDigits like GetNumberOption (ECMA-402 9.2.? with range 1 to 3).
                DateTimeFormatOptions dopt = IntlDateTimeDifferentialTests.Options(o) with { FractionalSecondDigits = NumberOption(o, "fractionalSecondDigits", 1, 3) };
                DateTimeFormatService f = DateTimeFormatService.Create(locales, dopt, "en-US", "UTC", Zones);
                double Time(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i && args[i].ValueKind == JsonValueKind.String && args[i].GetString() is { Length: > 2 } t && t[1] == ':' && t[0] != 's' && double.TryParse(t[2..], NumberStyles.Float, NumberFormatInfo.InvariantInfo, out double ms) ? ms : double.NaN;
                // formatRange(undefined, y) is a TypeError (ECMA-402 11.3.? step 4); a missing argument reaches the service as undefined.
                void RequireDates()
                {
                    if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2)
                    {
                        throw IntlException.Type("startDate and endDate are required");
                    }
                }

                switch (method)
                {
                    case "ctor":
                        return null;
                    case "format":
                        return Spaces(f.Format(Time(0)));
                    case "formatRange":
                        RequireDates();
                        return Spaces(f.FormatRange(Time(0), Time(1)));
                    case "formatToParts":
                    case "formatRangeToParts":
                    {
                        if (method == "formatRangeToParts")
                        {
                            RequireDates();
                        }

                        List<DateTimePart> parts = method == "formatToParts" ? f.FormatToParts(Time(0)) : f.FormatRangeToParts(Time(0), Time(1));
                        DateTimePart part = parts[c.GetProperty("part").GetInt32()];
                        return c.GetProperty("prop").GetString() switch { "type" => part.Type, "value" => part.Value, "source" => part.Source, _ => null };
                    }

                    case "resolvedOptions":
                        return c.GetProperty("prop").GetString() switch
                        {
                            "locale" => f.Locale,
                            "calendar" => DateTimeFormatService.Calendar,
                            "numberingSystem" => DateTimeFormatService.NumberingSystem,
                            "timeZone" => f.TimeZone,
                            "hourCycle" => f.HourCycle,
                            "weekday" => f.Weekday,
                            "era" => f.Era,
                            "year" => f.Year,
                            "month" => f.Month,
                            "day" => f.Day,
                            "dayPeriod" => f.DayPeriod,
                            "hour" => f.Hour,
                            "minute" => f.Minute,
                            "second" => f.Second,
                            "fractionalSecondDigits" => f.FractionalSecondDigits is { } n ? Inv(n) : null,
                            "timeZoneName" => f.TimeZoneName,
                            "dateStyle" => f.DateStyle,
                            "timeStyle" => f.TimeStyle,
                            _ => null,
                        };
                }

                break;
            }

            case "df":
            {
                DurationFormatService f = DurationFormatService.Create(locales, IntlDateTimeDifferentialTests.DurationOptions(o) with { FractionalDigits = NumberOption(o, "fractionalDigits", 0, 9) }, "en-US");
                DurationRecord? Duration()
                {
                    if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() == 0)
                    {
                        return null;
                    }

                    // ToDurationRecord (13.5.3): a string is a RangeError, any other non-object a TypeError.
                    if (args[0].ValueKind == JsonValueKind.String)
                    {
                        throw args[0].GetString()!.StartsWith("s:", StringComparison.Ordinal) ? IntlException.Range("Invalid duration") : IntlException.Type("Invalid duration");
                    }

                    if (args[0].ValueKind != JsonValueKind.Object || !args[0].TryGetProperty("o", out JsonElement fields))
                    {
                        throw IntlException.Type("Invalid duration");
                    }

                    var values = new double?[10];
                    foreach (JsonProperty p in fields.EnumerateObject())
                    {
                        int i = Array.IndexOf(DurationRecord.UnitNames, p.Name);
                        string text = p.Value.GetString()![2..];
                        if (i >= 0)
                        {
                            values[i] = double.TryParse(text, NumberStyles.Float, NumberFormatInfo.InvariantInfo, out double d) ? d : double.NaN;
                        }
                    }

                    return DurationRecord.Create(values);
                }

                switch (method)
                {
                    case "ctor":
                        return null;
                    case "format":
                        return Duration() is { } d1 ? f.Format(d1) : null;
                    case "formatToParts":
                        if (Duration() is { } d2)
                        {
                            DurationPart part = f.FormatToParts(d2)[c.GetProperty("part").GetInt32()];
                            return c.GetProperty("prop").GetString() switch { "type" => part.Type, "value" => part.Value, "unit" => part.Unit, _ => null };
                        }

                        break;
                    case "resolvedOptions":
                    {
                        string prop = c.GetProperty("prop").GetString()!;
                        switch (prop)
                        {
                            case "locale": return f.Locale;
                            case "numberingSystem": return f.NumberingSystem;
                            case "style": return f.Style;
                            case "fractionalDigits": return f.FractionalDigits is { } fd ? Inv(fd) : null;
                        }

                        bool display = prop.EndsWith("Display", StringComparison.Ordinal);
                        int unit = Array.IndexOf(DurationRecord.UnitNames, display ? prop[..^7] : prop);
                        if (unit >= 0)
                        {
                            return display ? f.Unit(unit).Display : f.Unit(unit).Style;
                        }

                        break;
                    }
                }

                break;
            }

            case "canon":
            {
                List<string> all = LocaleResolver.CanonicalizeLocaleList(req);
                return c.TryGetProperty("index", out JsonElement idx) ? all[idx.GetInt32()] : string.Join(",", all);
            }
        }

        unsupportedShape = true;
        return null;
    }
}
