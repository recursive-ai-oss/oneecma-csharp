using System.Text;
using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Behaviour that must not depend on the operating system: JSON newlines, and time zones on a Windows machine whose BCL has no IANA
/// ids (it knows only Windows ids when ICU is absent). The Windows case is exercised here through a zone finder that refuses every id
/// containing '/', the way such a machine does.
/// </summary>
public class WindowsPortabilityTests
{
    // Windows id -> the IANA zone whose rules stand in for it on this machine.
    private static readonly Dictionary<string, string> WindowsToIana = new(StringComparer.Ordinal)
    {
        ["Romance Standard Time"] = "Europe/Paris",
        ["W. Europe Standard Time"] = "Europe/Berlin",
        ["Eastern Standard Time"] = "America/New_York",
        ["Central Standard Time"] = "America/Chicago",
        ["India Standard Time"] = "Asia/Calcutta",
    };

    private static TimeZoneInfo ReferenceZone(string iana)
    {
        foreach (var (windows, ianaId) in WindowsToIana)
        {
            if (ianaId == iana)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windows);
                }
                catch (TimeZoneNotFoundException)
                {
                }
            }
        }

        return TimeZoneInfo.FindSystemTimeZoneById(iana);
    }

    private static BclTimeZoneSource WindowsOnly(List<string>? asked = null) => new(id =>
    {
        asked?.Add(id);
        return WindowsToIana.TryGetValue(id, out string? iana) ? ReferenceZone(iana) : null;
    });

    [Fact]
    public void IndentedDiagnosticJsonUsesLineFeedOnly()
    {
        Diagnostic[] d = ScriptEngine.Check("let a = 1;\nlet a = 2;", new Syntax.ParseOptions { FileName = "t.js" }, TestContext.Current.CancellationToken).ToArray();
        string json = DiagnosticJson.Render(d, [], null, new JsonRenderOptions { Indented = true });
        Assert.Contains("\n", json);
        Assert.DoesNotContain("\r", json);
    }

    [Theory]
    [InlineData("Europe/Paris", "Europe/Paris")]
    [InlineData("Europe/Berlin", "Europe/Berlin")]
    [InlineData("America/New_York", "America/New_York")]
    [InlineData("America/Chicago", "America/Chicago")]
    [InlineData("Asia/Calcutta", "Asia/Calcutta")]
    [InlineData("Asia/Kolkata", "Asia/Calcutta")]
    public void AnIanaIdResolvesThroughTheWindowsMapping(string id, string native)
    {
        var asked = new List<string>();
        BclTimeZoneSource windows = WindowsOnly(asked);
        foreach (double t in new[] { 1_700_000_000_000.0, 1_720_000_000_000.0, 0, -2_000_000_000_000.0 })
        {
            Assert.True(windows.TryGetOffset(id, t, out TimeZoneOffset got), id);
            // The reference reads the same rules directly, by an id every OS has (Windows has no IANA ids without ICU).
            TimeZoneInfo reference = ReferenceZone(native);
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Max(t, -62135596800000.0 + 86400000.0));
            var want = new TimeZoneOffset((int)reference.GetUtcOffset(at).TotalMilliseconds, reference.IsDaylightSavingTime(at));
            Assert.Equal(want, got);
        }

        Assert.Contains(asked, a => !a.Contains('/'));
    }

    [Theory]
    [InlineData(typeof(TimeZoneNotFoundException))]
    [InlineData(typeof(InvalidTimeZoneException))]
    [InlineData(typeof(System.Security.SecurityException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public void AFinderThatThrowsMeansNotFound(Type exception)
    {
        var source = new BclTimeZoneSource(id => throw (Exception)Activator.CreateInstance(exception)!);
        Assert.False(source.TryGetOffset("Europe/Berlin", 0, out _));
    }

    [Fact]
    public void AHostWhoseFinderThrowsGivesARangeErrorNotAnInternalError()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new ThrowingHost() });
        JsException e = Assert.Throws<JsException>(() => engine.Evaluate("new Date(0).getHours()", "t.js", TestContext.Current.CancellationToken));
        Assert.Contains("Invalid time zone specified: Europe/Berlin", e.Message);
    }

    private sealed class ThrowingHost : DefaultHost
    {
        private readonly BclTimeZoneSource _source = new(id => throw new TimeZoneNotFoundException(id));

        public override string TimeZoneId => "Europe/Berlin";

        public override bool TryGetTimeZoneOffset(string zoneId, double epochMs, out int offsetMs, out bool isDaylight)
        {
            bool known = _source.TryGetOffset(zoneId, epochMs, out TimeZoneOffset o);
            offsetMs = o.OffsetMs;
            isDaylight = o.IsDaylight;
            return known;
        }
    }

    [Fact]
    public void AZoneNobodyKnowsIsUnknownNotUtc()
    {
        Assert.False(WindowsOnly().TryGetOffset("No/Such_Zone", 0, out _));
        Assert.False(WindowsOnly().TryGetOffset("Europe/Moscow", 0, out _)); // mapped, but this fake machine lacks its Windows zone
    }

    [Fact]
    public void EveryCanonicalZoneHasAWindowsId()
    {
        // The mapping is what a Windows host relies on; ids CLDR does not map are listed so a change shows up here.
        var unmapped = new List<string>();
        RecursiveAI.OneEcma.Intl.Data.DataTree sup = RecursiveAI.OneEcma.Intl.Data.CldrData.Supplemental;
        foreach (string key in sup.GetKeys("bcp47", "timezone", "tz"))
        {
            if (key.StartsWith('_') || key == "unk" || sup.GetString("bcp47", "timezone", "tz", key, "_alias") is not { } list)
            {
                continue;
            }

            string id = list.Split(' ')[0];
            if (id != "UTC" && BclTimeZoneSource.WindowsIdOf(id) is null)
            {
                unmapped.Add(id);
            }
        }

        // Etc/* fixed-offset names and a few historical or special ids are not in the table; real regions all are.
        Assert.DoesNotContain(unmapped, id => !id.StartsWith("Etc/", StringComparison.Ordinal) && !id.StartsWith("Antarctica/", StringComparison.Ordinal) && id.Contains('/'));
    }

    private sealed class WindowsHost(string zone) : DefaultHost
    {
        private readonly BclTimeZoneSource _source = WindowsOnly();

        public override string TimeZoneId => zone;

        public override double CurrentTimeMs() => 1_720_000_000_000;

        public override bool TryGetTimeZoneOffset(string zoneId, double epochMs, out int offsetMs, out bool isDaylight)
        {
            bool known = _source.TryGetOffset(zoneId, epochMs, out TimeZoneOffset o);
            offsetMs = o.OffsetMs;
            isDaylight = o.IsDaylight;
            return known;
        }
    }

    [Fact]
    public void DateAndIntlUseTheWindowsMappedZoneOnAWindowsMachine()
    {
        var host = new WindowsHost("Europe/Berlin");
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host });
        string s = engine.ToDisplayString(engine.Evaluate(
            "new Date(Date.UTC(2024, 6, 1, 10)).getHours() + ' ' + new Date(Date.UTC(2024, 0, 1, 10)).getHours() + ' ' +" +
            " new Intl.DateTimeFormat('en-US', { timeZone: 'Europe/Paris', hour: 'numeric', hourCycle: 'h23' }).format(Date.UTC(2024, 6, 1, 10)) + ' ' +" +
            " new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Calcutta', hour: 'numeric', minute: 'numeric', hourCycle: 'h23' }).format(Date.UTC(2024, 0, 1, 10))",
            "t.js", TestContext.Current.CancellationToken));
        Assert.Equal("12 11 12 15:30", s);
    }

    [Fact]
    public void LocalTimeInAZoneNobodyCanResolveThrowsInsteadOfReadingUtc()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new WindowsHost("Mars/Olympus_Mons") });
        JsException e = Assert.Throws<JsException>(() => engine.Evaluate("new Date(0).getHours()", "t.js", TestContext.Current.CancellationToken));
        Assert.Contains("Invalid time zone specified: Mars/Olympus_Mons", e.Message);
    }
}
