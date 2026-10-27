using System.Text.Json;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>NumberFormat, PluralRules and range formatting against the recorded Node output.</summary>
public class IntlNumberDifferentialTests
{
    internal static NumberFormatOptions NfOptions(JsonElement o)
    {
        bool? flag = null;
        string? ug = null;
        if (o.TryGetProperty("useGrouping", out JsonElement g))
        {
            if (g.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                flag = g.GetBoolean();
            }
            else
            {
                ug = g.ValueKind == JsonValueKind.String ? g.GetString() : g.GetRawText();
            }
        }

        return new NumberFormatOptions
        {
            LocaleMatcher = o.Str("localeMatcher"),
            NumberingSystem = o.Str("numberingSystem"),
            Style = o.Str("style"),
            Currency = o.Str("currency"),
            CurrencyDisplay = o.Str("currencyDisplay"),
            CurrencySign = o.Str("currencySign"),
            Unit = o.Str("unit"),
            UnitDisplay = o.Str("unitDisplay"),
            Notation = o.Str("notation"),
            CompactDisplay = o.Str("compactDisplay"),
            SignDisplay = o.Str("signDisplay"),
            UseGrouping = ug,
            UseGroupingFlag = flag,
            Digits = Digits(o),
        };
    }

    internal static DigitOptionInputs Digits(JsonElement o) => new()
    {
        MinimumIntegerDigits = o.Int("minimumIntegerDigits"),
        MinimumFractionDigits = o.Int("minimumFractionDigits"),
        MaximumFractionDigits = o.Int("maximumFractionDigits"),
        MinimumSignificantDigits = o.Int("minimumSignificantDigits"),
        MaximumSignificantDigits = o.Int("maximumSignificantDigits"),
        RoundingIncrement = o.Int("roundingIncrement"),
        RoundingMode = o.Str("roundingMode"),
        RoundingPriority = o.Str("roundingPriority"),
        TrailingZeroDisplay = o.Str("trailingZeroDisplay"),
    };

    private static string Actual(List<NumberPart> parts) => IntlCorpus.PartsText(parts.Select(p => (p.Type, p.Value)));

    [Fact]
    public void NumberFormatMatchesNode()
    {
        var (total, matched, documented, failures) = IntlCorpus.Run("number-format", IntlCorpus.Rows("number-format.jsonl"), row =>
        {
            string locale = row.GetProperty("l").GetString()!;
            NumberFormatService f = NumberFormatService.Create([locale], NfOptions(row.GetProperty("o")), "en-US");
            if (row.TryGetProperty("e", out JsonElement e))
            {
                return $"expected {e.GetString()}, got a value";
            }

            string actual = Actual(f.FormatToParts(IntlCorpus.Value(row.GetProperty("v").GetString()!)));
            string expected = IntlCorpus.PartsText(row.GetProperty("p"));
            return actual == expected ? null : $"expected {expected}  got {actual}";
        });
        Assert.True(failures.Count == 0, $"{failures.Count} of {total} rows differ ({matched} match, {documented} documented):\n" + string.Join("\n", failures.Take(25)));
    }

    [Fact]
    public void NumberFormatRangeMatchesNode()
    {
        var (total, matched, documented, failures) = IntlCorpus.Run("number-format-range", IntlCorpus.Rows("number-format-range.jsonl"), row =>
        {
            string locale = row.GetProperty("l").GetString()!;
            NumberFormatService f = NumberFormatService.Create([locale], NfOptions(row.GetProperty("o")), "en-US");
            string[] v = row.GetProperty("v").GetString()!.Split(' ');
            if (row.TryGetProperty("e", out JsonElement e))
            {
                try
                {
                    f.FormatRangeToParts(IntlCorpus.Value(v[0]), IntlCorpus.Value(v[1]));
                }
                catch (IntlException x) when (x.Kind == IntlErrorKind.Range && e.GetString() == "RangeError")
                {
                    return null;
                }

                return $"expected {e.GetString()}, got a value";
            }

            string actual = string.Join("|", f.FormatRangeToParts(IntlCorpus.Value(v[0]), IntlCorpus.Value(v[1])).Select(p => p.Type + ":" + p.Value + ":" + p.Source));
            string expected = string.Join("|", row.GetProperty("p").EnumerateArray().Select(p => p[0].GetString() + ":" + p[1].GetString() + ":" + p[2].GetString()));
            return actual == expected ? null : $"expected {expected}  got {actual}";
        });
        Assert.True(failures.Count == 0, $"{failures.Count} of {total} rows differ ({matched} match, {documented} documented):\n" + string.Join("\n", failures.Take(25)));
    }
}
