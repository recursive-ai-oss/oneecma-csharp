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
            return LocaleResolver.Match(canon).Locale is not null;
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
        string[] req = Locales(c);
        string tExpect = expect.GetProperty("t").GetString()!;
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
                PluralRulesService p = PluralRulesService.Create(locales, new PluralRulesOptions { Type = o.Str("type"), Digits = IntlNumberDifferentialTests.Digits(o) }, "en-US");
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
                ListFormatService l = ListFormatService.Create(locales, o.Str("type"), o.Str("style"), "en-US");
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
                RelativeTimeFormatService r = RelativeTimeFormatService.Create(locales, o.Str("style"), o.Str("numeric"), "en-US");
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
                var dopt = new DisplayNamesOptions { Style = o.Str("style"), Type = o.Str("type"), Fallback = o.Str("fallback"), LanguageDisplay = o.Str("languageDisplay") };
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
