namespace RecursiveAI.OneEcma.Tests.RegExp;

/// <summary>Minimized cases found by fuzzing; each used to throw from a public call.</summary>
public class RegExpRegressionTests
{
    [Fact]
    public void LoneSurrogateInClassStringIsMatchedLiterally()
    {
        // \q{...} strings may contain lone surrogates, written as an escape or as the raw code unit.
        Assert.Equal("[[0,3]]", RegExpHelper.Run("[\\q{a\ude00b|c}]", "v", "a\ude00b"));
        Assert.Equal("[[1,2]]", RegExpHelper.Run(@"[\q{\ud83da.b|c}]", "iv", "zc"));
        Assert.Equal("[[0,1]]", RegExpHelper.Run(@"\ude00*?[\w\q{ab|c\ude00}]", "iv", "c\ud83d\ude00"));
    }
}
