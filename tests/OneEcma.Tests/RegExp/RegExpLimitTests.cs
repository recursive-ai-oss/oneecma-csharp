using System.Diagnostics;
using RecursiveAI.OneEcma.RegExp;

namespace RecursiveAI.OneEcma.Tests.RegExp;

/// <summary>Catastrophic patterns and oversized inputs must end in a limit result, quickly, with the process intact.</summary>
public class RegExpLimitTests
{
    private const long Budget = 2_000_000;

    public static TheoryData<string, string, string> Catastrophic => new()
    {
        { @"(a+)+$", "", new string('a', 40) + "b" },
        { @"(a*)*$", "", new string('a', 40) + "b" },
        { @"(a|aa)+$", "", new string('a', 60) + "b" },
        { @"(a|a)*$", "", new string('a', 60) + "b" },
        { @"^(([a-z])+.)+[A-Z]([a-z])+$", "", new string('a', 50) + "!" },
        { @"(.*a){20}$", "s", new string('a', 40) + "b" },
        { @"(?:a?){40}a{40}", "", new string('a', 40) },
        { @"(x+x+)+y", "", new string('x', 60) },
        { @"(?=(a+)+$)\1b", "", new string('a', 40) + "c" },
        { @"(?<=b(a+)+)c", "", new string('a', 40) + "c" },
        { @"(a+)+$", "iu", new string('a', 40) + "b" },
        { @"(\w+\s?)*$", "", new string('a', 40) + "!" },
    };

    [Theory]
    [MemberData(nameof(Catastrophic))]
    public void CatastrophicPatternEndsInFuelLimit(string pattern, string flags, string input)
    {
        var prog = RegExpHelper.Compile(pattern, flags);
        var sw = Stopwatch.StartNew();
        var r = prog.Exec(input, 0, false, new FuelMeter(Budget));
        sw.Stop();
        Assert.Equal(RegExpOutcome.LimitExceeded, r.Outcome);
        Assert.Equal(RegExpLimitKind.Fuel, r.Limit);
        Assert.Null(r.Match);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ZeroFuelStopsImmediately()
    {
        var r = RegExpHelper.Compile("a", "").Exec("aaa", 0, false, new FuelMeter(0));
        Assert.Equal(RegExpOutcome.LimitExceeded, r.Outcome);
    }

    [Fact]
    public void LinearScanWithinBudgetIsNotAFalseLimit()
    {
        var r = RegExpHelper.Compile(@"\d+$", "").Exec(new string('x', 10_000) + "123", 0, false, new FuelMeter(5_000_000));
        Assert.Equal(RegExpOutcome.Match, r.Outcome);
    }

    [Fact]
    public void BacktrackStackIsBoundedByMemoryLimit()
    {
        // Each iteration of a group with an alternation pushes backtrack entries; 2 MiB input forces growth past 1 MiB.
        var prog = RegExpHelper.Compile(@"(?:a|b)*c", "");
        var r = prog.Exec(new string('a', 2_000_000), 0, false, FuelMeter.Unlimited, new RegExpLimits { MaxBacktrackBytes = 1 << 20 });
        Assert.Equal(RegExpOutcome.LimitExceeded, r.Outcome);
        Assert.Equal(RegExpLimitKind.Memory, r.Limit);
    }

    [Fact]
    public void DeeplyNestedPatternIsRejectedNotRecursed()
    {
        string pattern = new string('(', 20_000) + "a" + new string(')', 20_000);
        var r = RegExpProgram.Compile(pattern, "");
        Assert.Null(r.Program);
        Assert.Contains("Regular expression too large", r.Diagnostics[0].Message);

        string classes = string.Concat(Enumerable.Repeat("(?:", 50_000));
        Assert.Null(RegExpProgram.Compile(classes, "").Program);
        Assert.Null(RegExpProgram.Compile(new string('(', 20_000) + "?=a", "v").Program);
    }

    [Fact]
    public void HugeAlternationAndQuantifierCountsCompileOrFailCleanly()
    {
        string alt = string.Join('|', Enumerable.Range(0, 20_000).Select(i => "w" + i));
        var r = RegExpProgram.Compile(alt, "");
        Assert.NotNull(r.Program);
        var m = r.Program!.Exec("zzw19999", 0, false, new FuelMeter(50_000_000));
        Assert.True(m.Outcome is RegExpOutcome.Match or RegExpOutcome.LimitExceeded);

        // Large repeat counts must not allocate a program proportional to the count without bound.
        var big = RegExpProgram.Compile(@"(?:a{1000}){1000}", "");
        if (big.Program is not null)
        {
            var e = big.Program.Exec(new string('a', 1000), 0, false, new FuelMeter(100_000));
            Assert.NotEqual(RegExpOutcome.Match, e.Outcome);
        }
    }

    [Fact]
    public void PerStartPositionRetriesAreCharged()
    {
        // Quadratic scan: every start position runs a long failing attempt.
        var prog = RegExpHelper.Compile(@"a*b", "");
        var r = prog.Exec(new string('a', 50_000), 0, false, new FuelMeter(1_000_000));
        Assert.Equal(RegExpOutcome.LimitExceeded, r.Outcome);
    }
}
