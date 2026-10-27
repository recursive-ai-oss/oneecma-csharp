using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

public class IntlOptionsTests
{
    [Theory]
    [InlineData("lookup")]
    [InlineData("best fit")]
    public void LocaleMatcherAcceptsSpecValues(string value) =>
        Assert.NotNull(NumberFormatService.Create(["en-US"], new NumberFormatOptions { LocaleMatcher = value }, "en-US"));

    [Theory]
    [InlineData("bestfit")]
    [InlineData("")]
    [InlineData("Lookup")]
    public void LocaleMatcherRejectsOtherValues(string value)
    {
        Assert.Equal(IntlErrorKind.Range, Assert.Throws<IntlException>(() => NumberFormatService.Create(["en"], new NumberFormatOptions { LocaleMatcher = value }, "en-US")).Kind);
        Assert.Throws<IntlException>(() => ListFormatService.Create(["en"], null, null, "en-US", value));
        Assert.Throws<IntlException>(() => RelativeTimeFormatService.Create(["en"], null, null, "en-US", value));
        Assert.Throws<IntlException>(() => PluralRulesService.Create(["en"], new PluralRulesOptions { LocaleMatcher = value }, "en-US"));
        Assert.Throws<IntlException>(() => DisplayNamesService.Create(["en"], new DisplayNamesOptions { Type = "region", LocaleMatcher = value }, "en-US"));
    }

    [Theory]
    [InlineData("latn", true)]
    [InlineData("arab", true)]
    [InlineData("a-b", false)]
    [InlineData("ab", false)]
    [InlineData("abcdefghi", false)]
    [InlineData("abc-defg", true)]
    [InlineData("abc-", false)]
    [InlineData("ab_c", false)]
    [InlineData("", false)]
    public void NumberingSystemMustBeWellFormed(string value, bool ok)
    {
        var options = new NumberFormatOptions { NumberingSystem = value };
        if (ok)
        {
            // Well formed but unsupported systems fall back to Latin digits.
            Assert.Equal("latn", NumberFormatService.Create(["en"], options, "en-US").NumberingSystem);
        }
        else
        {
            Assert.Throws<IntlException>(() => NumberFormatService.Create(["en"], options, "en-US"));
        }
    }

    [Fact]
    public void UnitDisplayIsValidatedForEveryStyle() =>
        Assert.Throws<IntlException>(() => NumberFormatService.Create(["en"], new NumberFormatOptions { UnitDisplay = "Short" }, "en-US"));

    [Fact]
    public void CompactPluralRulesShiftTheDigits()
    {
        var pr = PluralRulesService.Create(["fr"], new PluralRulesOptions { Notation = "compact" }, "en-US");
        Assert.Equal("many", pr.Select(DecimalNumber.FromDouble(1.5e6)));
        Assert.Equal("one", pr.Select(DecimalNumber.FromDouble(1)));
        Assert.Throws<IntlException>(() => PluralRulesService.Create(["fr"], new PluralRulesOptions { Notation = "scientific" }, "en-US"));
    }

    [Fact]
    public void LocaleOptionsApplyToTag()
    {
        Assert.Equal("fr-CA-u-ca-gregory-hc-h23-kn", LocaleOptions.Apply("en-US", new LocaleOptionsInput { Language = "fr", Region = "ca", Calendar = "gregory", HourCycle = "h23", Numeric = true }));
        Assert.Equal("en-Latn-US-u-kf-upper", LocaleOptions.Apply("en-US", new LocaleOptionsInput { Script = "latn", CaseFirst = "upper" }));
        Assert.Throws<IntlException>(() => LocaleOptions.Apply("en", new LocaleOptionsInput { Language = "e" }));
        Assert.Throws<IntlException>(() => LocaleOptions.Apply("en", new LocaleOptionsInput { Calendar = "a" }));
        Assert.Throws<IntlException>(() => LocaleOptions.Apply("en", new LocaleOptionsInput { HourCycle = "h13" }));
    }
}
