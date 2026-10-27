using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Date behaviour that comes from the host: the clock, the zone and its offsets.</summary>
public class DateHostTests
{
    // A zone with a fixed +01:00 offset that jumps to +02:00 at 2024-03-31T01:00Z and back at 2024-10-27T01:00Z.
    private sealed class RuleHost : DefaultHost
    {
        internal readonly StringBuilder Output = new();

        public override string TimeZoneId => "Test/Zone";

        public override double CurrentTimeMs() => 1_720_000_000_000;

        public override void Print(string text) => Output.Append(text).Append('\n');

        public override bool TryGetTimeZoneOffset(string zoneId, double epochMs, out int offsetMs, out bool isDaylight)
        {
            isDaylight = epochMs >= 1711846800000 && epochMs < 1729990800000;
            offsetMs = (isDaylight ? 2 : 1) * 3600000;
            return zoneId == "Test/Zone";
        }
    }

    private static string Run(string source)
    {
        var host = new RuleHost();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true });
        engine.Evaluate(source, "t.js", TestContext.Current.CancellationToken);
        return host.Output.ToString().TrimEnd('\n');
    }

    [Fact]
    public void ClockAndOffsetsComeFromTheHost()
    {
        string o = Run("""
            print(Date.now());
            print(new Date().getTime());
            print(new Date(0).getHours());
            print(new Date(1711846800000).getHours() + ',' + new Date(1711846800000 - 1).getHours());
            print(new Date(1711846800000).getTimezoneOffset());
            """);
        Assert.Equal("1720000000000\n1720000000000\n1\n3,1\n-120", o);
    }

    [Fact]
    public void GapAndOverlapFollowTheSpecifiedDisambiguation()
    {
        // Local 02:30 on 2024-03-31 does not exist (01:59 -> 03:00): read with the offset before the transition, so 03:30 local.
        // Local 02:30 on 2024-10-27 happens twice: the earlier instant (offset +02:00) is used.
        string o = Run("""
            var gap = new Date(2024, 2, 31, 2, 30);
            var over = new Date(2024, 9, 27, 2, 30);
            print(gap.getHours() + ':' + gap.getMinutes() + ' ' + gap.toISOString());
            print(over.getHours() + ':' + over.getMinutes() + ' ' + over.toISOString());
            """);
        Assert.Equal("3:30 2024-03-31T01:30:00.000Z\n2:30 2024-10-27T00:30:00.000Z", o);
    }

    [Fact]
    public void UnknownZoneIsUtc()
    {
        var host = new DefaultHost();
        Assert.Equal("UTC", host.TimeZoneId);
        Assert.False(host.TryGetTimeZoneOffset("No/Such_Zone", 0, out int ms, out _));
        Assert.Equal(0, ms);
    }
}
