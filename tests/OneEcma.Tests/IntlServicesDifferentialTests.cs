using System.Text.Json;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>PluralRules, ListFormat, RelativeTimeFormat, DisplayNames and locale services against recorded Node output.</summary>
public class IntlServicesDifferentialTests
{
    private static void AssertClean(string name, (int Total, int Matched, int Documented, List<string> Failures) r) =>
        Assert.True(r.Failures.Count == 0, $"{name}: {r.Failures.Count} of {r.Total} rows differ ({r.Matched} match, {r.Documented} documented):\n" + string.Join("\n", r.Failures.Take(25)));

    private static string? ErrorOrNull(JsonElement row, Action act)
    {
        // Rows that expect an error: the action must throw it.
        string expected = row.GetProperty("e").GetString()!;
        try
        {
            act();
        }
        catch (IntlException x)
        {
            return IntlCorpus.ExpectedError(row, x.Kind == IntlErrorKind.Range ? "RangeError" : "TypeError");
        }

        return $"expected {expected}, got a value";
    }

    [Fact]
    public void PluralRulesMatchNode()
    {
        AssertClean("plural-rules", IntlCorpus.Run("plural-rules", IntlCorpus.Rows("plural-rules.jsonl"), row =>
        {
            string locale = row.GetProperty("l").GetString()!;
            JsonElement o = row.GetProperty("o");
            var options = new PluralRulesOptions { Type = o.Str("type"), Digits = IntlNumberDifferentialTests.Digits(o) };
            string kind = row.GetProperty("s").GetString()!;
            if (row.TryGetProperty("e", out _) && kind == "prc")
            {
                return ErrorOrNull(row, () => PluralRulesService.Create([locale], options, "en-US"));
            }

            PluralRulesService pr = PluralRulesService.Create([locale], options, "en-US");
            switch (kind)
            {
                case "prc":
                {
                    // Node lists the categories in no fixed order; compare as sets.
                    string expected = string.Join(",", row.GetProperty("p").EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal));
                    string actual = string.Join(",", pr.PluralCategories.Order(StringComparer.Ordinal));
                    return expected == actual ? null : $"expected {expected} got {actual}";
                }

                case "pr":
                {
                    string actual = pr.Select(IntlCorpus.Value(row.GetProperty("v").GetString()!));
                    string expected = row.GetProperty("p").GetString()!;
                    return expected == actual ? null : $"expected {expected} got {actual}";
                }

                default:
                {
                    string[] v = row.GetProperty("v").GetString()!.Split(' ');
                    string expected = row.GetProperty("p").GetString()!;
                    string actual;
                    try
                    {
                        actual = pr.SelectRange(IntlCorpus.Value(v[0]), IntlCorpus.Value(v[1]));
                    }
                    catch (IntlException)
                    {
                        actual = "RangeError";
                    }

                    return expected == actual ? null : $"expected {expected} got {actual}";
                }
            }
        }));
    }

    [Fact]
    public void ListFormatMatchesNode()
    {
        AssertClean("list-format", IntlCorpus.Run("list-format", IntlCorpus.Rows("list-format.jsonl"), row =>
        {
            string locale = row.GetProperty("l").GetString()!;
            JsonElement o = row.GetProperty("o");
            if (row.GetProperty("s").GetString() == "lfo")
            {
                if (row.TryGetProperty("e", out _))
                {
                    return ErrorOrNull(row, () => ListFormatService.Create([locale], o.Str("type"), o.Str("style"), "en-US"));
                }

                string t = ListFormatService.Create([locale], o.Str("type"), o.Str("style"), "en-US").Type;
                return t == row.GetProperty("p").GetString() ? null : "type " + t;
            }

            ListFormatService f = ListFormatService.Create([locale], o.Str("type"), o.Str("style"), "en-US");
            string[] list = JsonSerializer.Deserialize<string[]>(row.GetProperty("v").GetString()!)!;
            string actual = string.Join("|", f.FormatToParts(list).Select(p => p.Type + ":" + p.Value));
            if (row.TryGetProperty("x", out _))
            {
                // Rows that probe conjunction rewrites (Spanish "e"/"u"): only the whole string is recorded.
                actual = string.Concat(f.FormatToParts(list).Select(p => p.Value));
                string exp = row.GetProperty("p")[0][1].GetString()!;
                return actual == exp ? null : $"expected {exp} got {actual}";
            }

            string expected = IntlCorpus.PartsText(row.GetProperty("p"));
            return actual == expected ? null : $"expected {expected} got {actual}";
        }));
    }

    [Fact]
    public void RelativeTimeFormatMatchesNode()
    {
        AssertClean("relative-time-format", IntlCorpus.Run("relative-time-format", IntlCorpus.Rows("relative-time-format.jsonl"), row =>
        {
            string locale = row.GetProperty("l").GetString()!;
            JsonElement o = row.GetProperty("o");
            if (row.GetProperty("s").GetString() == "rtfo")
            {
                return ErrorOrNull(row, () => RelativeTimeFormatService.Create([locale], o.Str("style"), o.Str("numeric"), "en-US"));
            }

            RelativeTimeFormatService f = RelativeTimeFormatService.Create([locale], o.Str("style"), o.Str("numeric"), "en-US");
            string unit = row.GetProperty("u").GetString()!;
            DecimalNumber v = IntlCorpus.Value(row.GetProperty("v").GetString()!);
            if (row.TryGetProperty("e", out _))
            {
                return ErrorOrNull(row, () => f.FormatToParts(v, unit));
            }

            string actual = string.Join("|", f.FormatToParts(v, unit).Select(p => p.Type + ":" + p.Value + ":" + p.Unit));
            string expected = string.Join("|", row.GetProperty("p").EnumerateArray().Select(p => p[0].GetString() + ":" + p[1].GetString() + ":" + (p[2].ValueKind == JsonValueKind.Null ? "" : p[2].GetString())));
            return actual == expected ? null : $"expected {expected} got {actual}";
        }));
    }

    [Fact]
    public void DisplayNamesMatchNode()
    {
        AssertClean("display-names", IntlCorpus.Run("display-names", IntlCorpus.Rows("display-names.jsonl"), row =>
        {
            string locale = row.GetProperty("l").GetString()!;
            JsonElement o = row.GetProperty("o");
            var options = new DisplayNamesOptions { Style = o.Str("style"), Type = o.Str("type"), Fallback = o.Str("fallback"), LanguageDisplay = o.Str("languageDisplay") };
            if (row.GetProperty("s").GetString() == "dno")
            {
                return ErrorOrNull(row, () => DisplayNamesService.Create([locale], options, "en-US"));
            }

            DisplayNamesService f = DisplayNamesService.Create([locale], options, "en-US");
            string code = row.GetProperty("v").GetString()!;
            if (row.TryGetProperty("e", out _))
            {
                return ErrorOrNull(row, () => f.Of(code));
            }

            string? actual = f.Of(code);
            string? expected = row.GetProperty("p").ValueKind == JsonValueKind.Null ? null : row.GetProperty("p").GetString();
            return actual == expected ? null : $"expected {expected ?? "undefined"} got {actual ?? "undefined"}";
        }));
    }

    [Fact]
    public void LocaleServicesMatchNode()
    {
        AssertClean("locale", IntlCorpus.Run("locale", IntlCorpus.Rows("locale.jsonl"), row =>
        {
            string kind = row.GetProperty("s").GetString()!;
            string v = row.GetProperty("v").GetString()!;
            switch (kind)
            {
                case "canon":
                {
                    if (row.TryGetProperty("e", out _))
                    {
                        return ErrorOrNull(row, () => LocaleResolver.CanonicalizeTag(v));
                    }

                    string actual = LocaleResolver.CanonicalizeTag(v);
                    string expected = row.GetProperty("p")[0].GetString()!;
                    return actual == expected ? null : $"expected {expected} got {actual}";
                }

                case "loc":
                {
                    if (row.TryGetProperty("e", out _))
                    {
                        return ErrorOrNull(row, () => LocaleResolver.CanonicalizeTag(v));
                    }

                    LocaleId id = LocaleId.Parse(v)!.Canonicalize();
                    string max = id.Clone().Maximize().ToString();
                    string min = id.Clone().Minimize().ToString();
                    // The reference's language accessor gives undefined for "und"; ECMA-402 says "und".
                    string actual = string.Join("|", max, min, id.Language == "und" ? "" : id.Language, id.Script, id.Region, id.LanguageTag());
                    string expected = string.Join("|", row.GetProperty("p").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? "" : x.GetString()));
                    return actual == expected ? null : $"expected {expected} got {actual}";
                }

                case "sup":
                {
                    string[] req = v.Split(',');
                    string actual = string.Join(",", LocaleResolver.SupportedLocales(LocaleResolver.CanonicalizeLocaleList(req)));
                    string expected = string.Join(",", row.GetProperty("p").EnumerateArray().Select(x => x.GetString()));
                    string resolved = LocaleResolver.Resolve(LocaleResolver.CanonicalizeLocaleList(req), ["nu"], new Dictionary<string, string?>(), (_, _) => ["latn"], "en-US").Locale;
                    string q = row.GetProperty("q").GetString()!;
                    return actual == expected && resolved == q ? null : $"expected {expected} / {q} got {actual} / {resolved}";
                }

                default:
                {
                    string actual = string.Join(",", IntlEnumeration.SupportedValuesOf(v));
                    string expected = string.Join(",", row.GetProperty("p").EnumerateArray().Select(x => x.GetString()));
                    return actual == expected ? null : $"expected {expected.Length} chars got {actual.Length}: " + Diff(expected, actual);
                }
            }
        }));
    }

    private static string Diff(string expected, string actual)
    {
        var e = expected.Split(',').ToHashSet();
        var a = actual.Split(',').ToHashSet();
        return $"missing [{string.Join(",", e.Except(a).Take(15))}] extra [{string.Join(",", a.Except(e).Take(15))}]";
    }
}
