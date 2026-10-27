using RecursiveAI.OneEcma.TextServices;

namespace RecursiveAI.OneEcma.Tests;

public class CollatorTests
{
    private static string[] Sort(TextCollator c, params string[] items)
    {
        var copy = (string[])items.Clone();
        Array.Sort(copy, (a, b) => c.Compare(a, b));
        return copy;
    }

    [Fact]
    public void RootOrderBasics()
    {
        var c = TextCollator.Create("en-US");
        Assert.Equal(["a", "A", "\u00E1", "b", "B"], Sort(c, "B", "\u00E1", "b", "A", "a"));
        Assert.Equal(0, c.Compare("e\u0301", "\u00E9")); // canonically equivalent text is equal (UTS #10 S2.1)
        Assert.True(c.Compare("\u4E00", "\u4E01") < 0); // Han: implicit weights follow code point order
    }

    [Fact]
    public void SpanishTailoringPlacesEnyeAfterN()
    {
        var es = TextCollator.Create("es");
        var en = TextCollator.Create("en");
        Assert.Equal(["n", "nz", "\u00F1", "o"], Sort(es, "o", "\u00F1", "nz", "n"));
        Assert.Equal(["n", "\u00F1", "nz", "o"], Sort(en, "o", "\u00F1", "nz", "n")); // root: n-tilde is a secondary variant of n
    }

    [Fact]
    public void LocaleCollationTypes()
    {
        var trad = TextCollator.Create("es", new CollatorOptions { Collation = "trad" });
        Assert.Equal(["cz", "ch", "da"], Sort(trad, "da", "ch", "cz")); // "ch" is a letter between c and d
        var phone = TextCollator.Create("de", new CollatorOptions { Collation = "phonebk" });
        Assert.NotEqual(0, phone.Compare("\u00E4", "ae"));
        Assert.True(phone.Compare("\u00E4a", "ab") > 0); // a-umlaut sorts as "ae" with a secondary difference
        var standard = TextCollator.Create("de");
        Assert.True(standard.Compare("\u00E4a", "ab") < 0);
    }

    [Fact]
    public void OptionsFollowEcma402()
    {
        var numeric = TextCollator.Create("en", new CollatorOptions { Numeric = true });
        Assert.Equal(["a2", "a10", "a010x"], Sort(numeric, "a010x", "a10", "a2"));
        Assert.Equal(0, numeric.Compare("a10", "a010"));
        var punct = TextCollator.Create("en", new CollatorOptions { IgnorePunctuation = true });
        Assert.Equal(0, punct.Compare("a-b", "ab"));
        var baseOnly = TextCollator.Create("en", new CollatorOptions().WithSensitivity("base"));
        Assert.Equal(0, baseOnly.Compare("a", "\u00C1"));
        var accent = TextCollator.Create("en", new CollatorOptions().WithSensitivity("accent"));
        Assert.Equal(0, accent.Compare("a", "A"));
        Assert.NotEqual(0, accent.Compare("a", "\u00E1"));
        var caseOnly = TextCollator.Create("en", new CollatorOptions().WithSensitivity("case"));
        Assert.Equal(0, caseOnly.Compare("a", "\u00E1"));
        Assert.True(caseOnly.Compare("a", "A") < 0);
        var upperFirst = TextCollator.Create("en", new CollatorOptions { CaseFirst = CaseFirst.Upper });
        Assert.True(upperFirst.Compare("A", "a") < 0);
        Assert.Throws<ArgumentException>(() => new CollatorOptions().WithSensitivity("nope"));
    }

    [Fact]
    public void SortKeysOrderLikeCompare()
    {
        var c = TextCollator.Create("fr");
        string[] items = ["cote", "cot\u00E9", "c\u00F4te", "c\u00F4t\u00E9", "Cote", "coup", "co\u00FBt"];
        foreach (string a in items)
        {
            foreach (string b in items)
            {
                Assert.Equal(Math.Sign(c.Compare(a, b)), Math.Sign(c.GetSortKey(a).AsSpan().SequenceCompareTo(c.GetSortKey(b))));
            }
        }

        Span<byte> tiny = stackalloc byte[1];
        Assert.False(c.TryGetSortKey("cote", tiny, out int needed));
        Assert.Equal(c.GetSortKey("cote").Length, needed);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("es")]
    [InlineData("es-US")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public void EveryStoredTailoringParses(string locale)
    {
        foreach (string? type in new string?[] { null, "standard", "search", "phonebk", "trad" })
        {
            Assert.NotNull(TextCollator.Create(locale, new CollatorOptions { Collation = type }));
        }

        Assert.NotNull(TextCollator.Create(locale, new CollatorOptions { Usage = CollationUsage.Search }));
    }

    [Fact]
    public void HostileInputsFinish()
    {
        var c = TextCollator.Create("en", new CollatorOptions { Numeric = true, IgnorePunctuation = true, Strength = CollationStrength.Identical });
        string marks = "\u0F71" + new string('\u0F72', 200_000);
        string digits = new string('7', 300_000);
        string soup = string.Concat(Enumerable.Repeat("a-1\u0301 ", 100_000));
        Assert.NotEqual(0, c.Compare(marks, marks + "x"));
        Assert.Equal(0, c.Compare(digits, digits));
        Assert.True(c.Compare(digits, digits + "0") < 0);
        Assert.NotEqual(0, c.Compare(soup, soup + "b"));
    }
}
