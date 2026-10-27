namespace RecursiveAI.OneEcma.Tests.RegExp;

public class RegExpBasicTests
{
    [Theory]
    [InlineData("a", "", "bab", "[[1,2]]")]
    [InlineData("a|ab", "", "abc", "[[0,1]]")]
    [InlineData("((a)|(ab))((c)|(bc))", "", "abc", "[[0,3],[0,1],[0,1],null,[1,3],null,[1,3]]")]
    [InlineData("a[a-z]{2,4}", "", "abcdefghi", "[[0,5]]")]
    [InlineData("a[a-z]{2,4}?", "", "abcdefghi", "[[0,3]]")]
    [InlineData("(aa|aabaac|ba|b|c)*", "", "aabaac", "[[0,4],[2,4]]")]
    [InlineData("(z)((a+)?(b+)?(c))*", "", "zaacbbbcac", "[[0,10],[0,1],[8,10],[8,9],null,[9,10]]")]
    [InlineData("(a*)*", "", "b", "[[0,0],null]")]
    [InlineData("(a*)b\\1+", "", "baaaac", "[[0,1],[0,0]]")]
    [InlineData("(?=(a+))", "", "baaabac", "[[1,1],[1,4]]")]
    [InlineData("(?=(a+))a*b\\1", "", "baaabac", "[[3,6],[3,4]]")]
    [InlineData("(.*?)a(?!(a+)b\\2c)\\2(.*)", "", "baaabaac", "[[0,8],[0,2],null,[3,8]]")]
    [InlineData("(?<=\\$)\\d+(\\.\\d*)?", "", "cost $10.50", "[[6,11],[8,11]]")]
    [InlineData("(?<!a)b", "", "abcb", "[[3,4]]")]
    [InlineData("(?<=(\\d)(\\d))x", "", "12x", "[[2,3],[0,1],[1,2]]")]
    [InlineData("(?<a>.)\\k<a>", "", "xaab", "[[1,3],[1,2]]")]
    [InlineData("\\u{1F600}+", "u", "x\ud83d\ude00\ud83d\ude00", "[[1,5]]")]
    [InlineData("^.$", "u", "\ud83d\ude00", "[[0,2]]")]
    [InlineData("^.$", "", "\ud83d\ude00", "null")]
    [InlineData("[a-z]+", "i", "HeLLo", "[[0,5]]")]
    [InlineData("\\w+", "iu", "\u017f", "[[0,1]]")]
    [InlineData("\\w+", "i", "\u017f", "null")]
    [InlineData("^b", "m", "a\nb", "[[2,3]]")]
    [InlineData("a.c", "s", "a\nc", "[[0,3]]")]
    [InlineData("a.c", "", "a\nc", "null")]
    [InlineData("(?i:a)b", "", "Ab", "[[0,2]]")]
    [InlineData("(?i:a)b", "", "AB", "null")]
    [InlineData("[\\p{L}--[a-z]]+", "v", "abcDEF", "[[3,6]]")]
    [InlineData("[\\q{abc|d}x]", "v", "abc", "[[0,3]]")]
    [InlineData("\\p{Lu}+", "u", "abCDe", "[[2,4]]")]
    [InlineData("(a+)+$", "", "aaaab", "null")]
    [InlineData("\\b\\w+\\b", "", " foo ", "[[1,4]]")]
    [InlineData("(?:)*", "", "", "[[0,0]]")]
    [InlineData("a{2}", "", "aaa", "[[0,2]]")]
    [InlineData("(a){2,3}?", "", "aaaa", "[[0,2],[1,2]]")]
    public void Matches(string pattern, string flags, string input, string expected)
    {
        Assert.Equal(expected, RegExpHelper.Run(pattern, flags, input));
    }
}
