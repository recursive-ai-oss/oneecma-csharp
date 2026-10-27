using System.Numerics;
using RecursiveAI.OneEcma.Intl.Data;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Probes of the embedded locale data. Expected values come from the reference Node (v22.22.1 and v24.22.1,
/// both CLDR 48.0 / Unicode 17.0); the comment on each test names the Node expression or UCD line.
/// </summary>
public class LocaleDataTests
{
    private const string Latn = "-numberSystem-latn";

    [Fact]
    public void VersionsMatchTheReferenceNode()
    {
        // node -p process.versions.cldr  => 48.0 ; process.versions.unicode => 17.0
        Assert.Equal("48.0", CldrData.CldrVersion);
        Assert.Equal("17.0.0", CldrData.Collation.Version);
    }

    [Fact]
    public void GermanDecimalAndGrouping()
    {
        // new Intl.NumberFormat('de').formatToParts(1234567.5) => group "." decimal ","
        DataTree de = CldrData.GetLocale("de");
        Assert.Equal(",", de.GetString("numbers", "symbols" + Latn, "decimal"));
        Assert.Equal(".", de.GetString("numbers", "symbols" + Latn, "group"));
    }

    [Fact]
    public void FrenchGroupsWithNarrowNoBreakSpace()
    {
        // new Intl.NumberFormat('fr').format(1234567.5) code points: 31 202f 32 33 34 202f 35 36 37 2c 35
        DataTree fr = CldrData.GetLocale("fr");
        Assert.Equal("\u202F", fr.GetString("numbers", "symbols" + Latn, "group"));
        Assert.Equal(",", fr.GetString("numbers", "symbols" + Latn, "decimal"));
    }

    [Fact]
    public void PortugueseBrazilCurrencyPattern()
    {
        // new Intl.NumberFormat('pt-BR',{style:'currency',currency:'BRL'}).format(1234.5)
        //   code points 52 24 a0 31 2e 32 33 34 2c 35 30, i.e. "R$" NBSP "1.234,50"; accounting(-1234.5) = "-R$ 1.234,50"
        DataTree pt = CldrData.GetLocale("pt-BR");
        Assert.Equal("\u00A4\u00A0#,##0.00", pt.GetString("numbers", "currencyFormats" + Latn, "standard"));
        Assert.Equal("R$", pt.GetString("currencies", "BRL", "symbol"));
    }

    [Fact]
    public void BritishYearMonthDayPatternAndInheritance()
    {
        // new Intl.DateTimeFormat('en-GB',{timeZone:'UTC',year:'numeric',month:'numeric',day:'numeric'}).format(Date.UTC(2024,0,5))
        //   => "05/01/2024", i.e. dd/MM/y; month:'short' gives "5 Jan 2024" (d MMM y).
        DataTree gb = CldrData.GetLocale("en-GB");
        Assert.Equal("dd/MM/y", gb.GetString("gregorian", "dateTimeFormats", "availableFormats", "yMd"));
        Assert.Equal("d MMM y", gb.GetString("gregorian", "dateTimeFormats", "availableFormats", "yMMMd"));

        // en-GB stores only its differences; month names come through en-001 -> en -> root.
        Assert.Equal("January", gb.GetString("gregorian", "months", "format", "wide", "1"));
        // en-US (default content of en) differs from en-GB.
        Assert.Equal("M/d/y", CldrData.GetLocale("en-US").GetString("gregorian", "dateTimeFormats", "availableFormats", "yMd"));
    }

    [Theory]
    [InlineData("es", "Cardinal", "0", "Other")]
    [InlineData("es", "Cardinal", "1", "One")]
    [InlineData("es", "Cardinal", "2", "Other")]
    [InlineData("es", "Cardinal", "1000000", "Many")]
    [InlineData("es", "Cardinal", "1.5", "Other")]
    [InlineData("es", "Ordinal", "1", "Other")]
    [InlineData("en", "Ordinal", "1", "One")]
    [InlineData("en", "Ordinal", "2", "Two")]
    [InlineData("en", "Ordinal", "3", "Few")]
    [InlineData("en", "Ordinal", "11", "Other")]
    [InlineData("en", "Cardinal", "1.0", "Other")]
    [InlineData("fr", "Cardinal", "0", "One")]
    [InlineData("fr", "Cardinal", "1.5", "One")]
    [InlineData("fr", "Cardinal", "1000000", "Many")]
    [InlineData("fr", "Ordinal", "1", "One")]
    [InlineData("de", "Cardinal", "1", "One")]
    [InlineData("pt-BR", "Cardinal", "0", "One")]
    public void PluralSelection(string locale, string type, string number, string expected)
    {
        // new Intl.PluralRules(locale, {type}).select(Number(number)) for each row. English cardinal "1.0"
        // is "other" in CLDR because v = 1; the JS number 1.0 would print as "1", so that row follows UTS #35.
        var ops = PluralOperands.Parse(number);
        Assert.Equal(Enum.Parse<PluralCategory>(expected), CldrData.Plurals.Select(locale, Enum.Parse<PluralType>(type), ops));
    }

    [Fact]
    public void PluralCategoryLists()
    {
        // new Intl.PluralRules('es').resolvedOptions().pluralCategories => many, one, other (Node lists them unordered)
        var es = CldrData.Plurals.Categories("es", PluralType.Cardinal);
        Assert.Equal([PluralCategory.One, PluralCategory.Many, PluralCategory.Other], es);
        Assert.Equal([PluralCategory.Other], CldrData.Plurals.Categories("es", PluralType.Ordinal));
        // ... 'en' ordinal => few, one, two, other
        Assert.Equal(
            [PluralCategory.One, PluralCategory.Two, PluralCategory.Few, PluralCategory.Other],
            CldrData.Plurals.Categories("en", PluralType.Ordinal));
    }

    [Fact]
    public void PluralOperandsFollowTheSpecTable()
    {
        // UTS #35 Part 3 "Plural Operand Examples": 1.20050c3 -> i=1200 v=2 w=1 f=50 t=5 c=3
        var ops = PluralOperands.Parse("1.20050c3");
        Assert.Equal(new BigInteger(1200), ops.IntegerPart);
        Assert.Equal((2, 1, 50L, 5L, 3), (ops.V, ops.W, ops.F, ops.T, ops.Exponent));
        var shifted = PluralOperands.Parse("123c6");
        Assert.Equal(new BigInteger(123000000), shifted.IntegerPart);
    }

    [Fact]
    public void EnglishListPatterns()
    {
        // new Intl.ListFormat('en-US').format(['a','b','c']) => "a, b, and c"; two items => "a and b"
        DataTree en = CldrData.GetLocale("en-US");
        Assert.Equal("{0}, and {1}", en.GetString("listPatterns", "listPattern-type-standard", "end"));
        Assert.Equal("{0}, {1}", en.GetString("listPatterns", "listPattern-type-standard", "middle"));
        Assert.Equal("{0} and {1}", en.GetString("listPatterns", "listPattern-type-standard", "2"));
        // en-GB => "a, b and c"; es => "a, b y c"
        Assert.Equal("{0} and {1}", CldrData.GetLocale("en-GB").GetString("listPatterns", "listPattern-type-standard", "end"));
        Assert.Equal("{0} y {1}", CldrData.GetLocale("es").GetString("listPatterns", "listPattern-type-standard", "end"));
    }

    [Theory]
    [InlineData("zh", "zh-Hans-CN")]
    [InlineData("sr", "sr-Cyrl-RS")]
    [InlineData("und", "en-Latn-US")]
    [InlineData("pt", "pt-Latn-BR")]
    [InlineData("ja", "ja-Jpan-JP")]
    public void LikelySubtags(string input, string expected)
    {
        // new Intl.Locale(input).maximize().toString()
        Assert.Equal(expected, CldrData.Supplemental.GetString("likelySubtags", input));
    }

    [Fact]
    public void OtherDisplayAndUnitData()
    {
        // new Intl.DisplayNames('de',{type:'region'}).of('FR') => Frankreich
        Assert.Equal("Frankreich", CldrData.GetLocale("de").GetString("territories", "FR"));
        // new Intl.DisplayNames('fr',{type:'language'}).of('de') => allemand
        Assert.Equal("allemand", CldrData.GetLocale("fr").GetString("languages", "de"));
        // new Intl.NumberFormat('en',{style:'unit',unit:'kilometer-per-hour',unitDisplay:'long'}).format(5)
        //   => "5 kilometers per hour"
        Assert.Equal("{0} kilometers per hour", CldrData.GetLocale("en").GetString("units", "long", "speed-kilometer-per-hour", "unitPattern-count-other"));
        // new Intl.NumberFormat('de',{notation:'compact',compactDisplay:'long'}).format(1200000) => "1,2 Millionen"
        Assert.Equal("0 Millionen", CldrData.GetLocale("de").GetString("numbers", "decimalFormats" + Latn, "long", "decimalFormat", "1000000-count-other"));
        // new Intl.RelativeTimeFormat('es').format(-2,'day') => "hace 2 días"
        Assert.Equal("hace {0} días", CldrData.GetLocale("es").GetString("dateFields", "day", "relativeTime-type-past", "relativeTimePattern-count-other"));
    }

    [Fact]
    public void LocaleResolutionAndSupplementalLists()
    {
        Assert.True(CldrData.TryResolveLocale("pt-BR", out string id));
        Assert.Equal("pt", id);
        Assert.True(CldrData.TryResolveLocale("es-US", out id));
        Assert.Equal("es-US", id);
        Assert.True(CldrData.TryResolveLocale("es-MX", out id));
        Assert.Equal("es", id);
        Assert.False(CldrData.TryResolveLocale("zh-Hans", out _));
        foreach (string tag in CldrData.SupportedLocales)
        {
            Assert.NotNull(CldrData.GetLocale(tag).GetString("numbers", "symbols" + Latn, "decimal"));
        }

        // Currency digits and the IANA zone table (aliases) are present for any input tag.
        Assert.Equal("0", CldrData.Supplemental.GetString("currencyData", "fractions", "JPY", "_digits"));
        string? kyiv = CldrData.Supplemental.GetString("bcp47", "timezone", "tz", "uaiev", "_alias");
        Assert.Equal("Europe/Kiev Europe/Kyiv Europe/Zaporozhye Europe/Uzhgorod", kyiv);
        Assert.Equal("Europe/Kyiv", CldrData.Supplemental.GetString("bcp47", "timezone", "tz", "uaiev", "_iana"));
    }

    // --- collation ---

    private static List<CollationElement> Elements(string s)
    {
        // Test strings use precomposed characters that have table entries, so no normalization is needed.
        int[] cps = s.EnumerateRunes().Select(r => r.Value).ToArray();
        var ces = new List<CollationElement>();
        for (int i = 0; i < cps.Length;)
        {
            i += CldrData.Collation.MatchLongest(cps.AsSpan(i), out CollationElement[] e);
            ces.AddRange(e);
        }

        return ces;
    }

    private static int Compare(string a, string b)
    {
        var x = Elements(a);
        var y = Elements(b);
        foreach (Func<CollationElement, int> level in new Func<CollationElement, int>[] { c => c.Primary, c => c.Secondary, c => c.Tertiary })
        {
            int[] wx = x.Select(level).Where(w => w != 0).ToArray();
            int[] wy = y.Select(level).Where(w => w != 0).ToArray();
            int c = wx.AsSpan().SequenceCompareTo(wy);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return 0;
    }

    [Theory]
    [InlineData("a", "B", -1)]
    [InlineData("a", "A", -1)]
    [InlineData("resume", "résumé", -1)]
    [InlineData("résumé", "resumf", -1)]
    [InlineData("ä", "b", -1)]
    [InlineData("co-op", "coop", -1)]
    [InlineData("1", "a", -1)]
    [InlineData("10", "2", -1)]
    [InlineData("a b", "ab", -1)]
    [InlineData("z", "ä", 1)]
    [InlineData("Z", "z", 1)]
    [InlineData("é", "f", -1)]
    [InlineData("ñ", "nz", -1)]
    public void RootCollationOrdering(string a, string b, int expected)
    {
        // Math.sign(new Intl.Collator('und').compare(a, b)) for each row (default options: non-ignorable).
        Assert.Equal(expected, Compare(a, b));
    }

    [Fact]
    public void SpanishTailoringIsShippedAsRuleText()
    {
        // new Intl.Collator('es').compare('ñ','nz') => 1 whereas 'und' gives -1: the difference is this rule.
        // CLDR writes the rule in decomposed form: "&N<n+U+0303<<<N+U+0303".
        Assert.Equal("&N<n\u0303<<<N\u0303", CldrData.Supplemental.GetString("collationTailoring", "es", "standard"));
        Assert.NotNull(CldrData.Supplemental.GetString("collationTailoring", "es", "traditional"));
        Assert.NotNull(CldrData.Supplemental.GetString("collationTailoring", "de", "phonebook"));
    }

    [Fact]
    public void ImplicitWeightsOrderHanBeforeUnassigned()
    {
        // UTS #10 Table 16: U+4E00 is core Han (FB40 + (cp >> 15), BBBB = (cp & 7FFF) | 8000).
        CollationElement[] han = CldrData.Collation.ImplicitElements(0x4E00);
        Assert.Equal(0xFB40, han[0].Primary);
        Assert.Equal(0xCE00, han[1].Primary);
        // U+0378 is unassigned: FBC0 + (cp >> 15).
        Assert.Equal(0xFBC0, CldrData.Collation.ImplicitElements(0x0378)[0].Primary);
        // U+17000 is Tangut: FB00, (cp - 17000) | 8000.
        Assert.Equal(0xFB00, CldrData.Collation.ImplicitElements(0x17000)[0].Primary);
    }

    // --- UAX #29 properties; expected values are lines of the pinned UCD 17.0.0 files ---

    [Fact]
    public void GraphemeBreakProperties()
    {
        BreakData b = CldrData.Breaks;
        Assert.Equal(GraphemeBreak.CR, b.GraphemeClusterBreak(0x000D));         // GraphemeBreakProperty: 000D ; CR
        Assert.Equal(GraphemeBreak.Extend, b.GraphemeClusterBreak(0x0301));     // 0300..036F ; Extend
        Assert.Equal(GraphemeBreak.RegionalIndicator, b.GraphemeClusterBreak(0x1F1E7)); // 1F1E6..1F1FF
        Assert.Equal(GraphemeBreak.ZWJ, b.GraphemeClusterBreak(0x200D));        // 200D ; ZWJ
        Assert.Equal(GraphemeBreak.LV, b.GraphemeClusterBreak(0xAC00));         // AC00 ; LV
        Assert.Equal(GraphemeBreak.SpacingMark, b.GraphemeClusterBreak(0x0E33));// 0E33 ; SpacingMark
        Assert.Equal(GraphemeBreak.Other, b.GraphemeClusterBreak('a'));
    }

    [Fact]
    public void WordSentenceAndOtherBreakProperties()
    {
        BreakData b = CldrData.Breaks;
        Assert.Equal(WordBreak.ALetter, b.WordBreakOf('A'));                    // WordBreakProperty: 0041..005A ; ALetter
        Assert.Equal(WordBreak.Numeric, b.WordBreakOf('7'));                    // 0030..0039 ; Numeric
        Assert.Equal(WordBreak.MidNumLet, b.WordBreakOf('.'));                  // 002E ; MidNumLet
        Assert.Equal(WordBreak.SingleQuote, b.WordBreakOf('\''));               // 0027 ; Single_Quote
        Assert.Equal(WordBreak.HebrewLetter, b.WordBreakOf(0x05D0));            // 05D0..05EA ; Hebrew_Letter
        Assert.Equal(WordBreak.Katakana, b.WordBreakOf(0x30A2));                // 30A1..30FA ; Katakana
        Assert.Equal(SentenceBreak.ATerm, b.SentenceBreakOf('.'));              // SentenceBreakProperty: 002E ; ATerm
        Assert.Equal(SentenceBreak.STerm, b.SentenceBreakOf('!'));              // 0021 ; STerm
        Assert.Equal(SentenceBreak.Lower, b.SentenceBreakOf('q'));              // 0061..007A ; Lower
        Assert.Equal(SentenceBreak.Sp, b.SentenceBreakOf(' '));                 // 0020 ; Sp
        Assert.True(b.IsExtendedPictographic(0x1F600));                         // emoji-data: 1F600 ; Extended_Pictographic
        Assert.False(b.IsExtendedPictographic('a'));
        Assert.Equal(IndicConjunctBreak.Linker, b.IndicConjunctBreakOf(0x094D)); // DerivedCoreProperties: 094D ; InCB; Linker
        Assert.Equal(IndicConjunctBreak.Consonant, b.IndicConjunctBreakOf(0x0915));
        Assert.Equal(IndicConjunctBreak.None, b.IndicConjunctBreakOf('a'));
    }
}
