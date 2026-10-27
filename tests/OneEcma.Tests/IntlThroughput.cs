using System.Diagnostics;
using RecursiveAI.OneEcma.Intl;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Informal throughput of NumberFormat.format and PluralRules.select. Skipped unless ONEECMA_INTL_THROUGHPUT is set; it prints, it asserts nothing.</summary>
public class IntlThroughput
{
    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("ONEECMA_INTL_THROUGHPUT") is null)
        {
            Assert.Skip("Set ONEECMA_INTL_THROUGHPUT=1 to print throughput numbers.");
        }

        var values = new double[1000];
        var rng = new Random(7);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Math.Round(rng.NextDouble() * Math.Pow(10, rng.Next(0, 9)), rng.Next(0, 4));
        }

        var decimals = values.Select(DecimalNumber.FromDouble).ToArray();
        var configs = new (string Name, NumberFormatService Service)[]
        {
            ("decimal en-US", NumberFormatService.Create(["en-US"], new NumberFormatOptions(), "en-US")),
            ("currency de EUR", NumberFormatService.Create(["de"], new NumberFormatOptions { Style = "currency", Currency = "EUR" }, "en-US")),
            ("compact en-US", NumberFormatService.Create(["en-US"], new NumberFormatOptions { Notation = "compact" }, "en-US")),
        };
        PluralRulesService plural = PluralRulesService.Create(["es"], new PluralRulesOptions(), "en-US");

        foreach (var (name, service) in configs)
        {
            Time(name + " format(double)", () => { foreach (double v in values) { service.Format(v); } }, values.Length);
            Time(name + " format(decimal)", () => { foreach (DecimalNumber v in decimals) { service.Format(v); } }, values.Length);
        }

        Time("plural es select", () => { foreach (DecimalNumber v in decimals) { plural.Select(v); } }, values.Length);

        // Date-time and duration formatting over a fixed zone source.
        var instants = new double[1000];
        for (int i = 0; i < instants.Length; i++)
        {
            instants[i] = 1.6e12 + (rng.NextDouble() * 4e11);
        }

        var zone = new FixedZoneSource(-4 * 3600000, true);
        var dates = new (string Name, DateTimeFormatService Service)[]
        {
            ("datetime en-US numeric", DateTimeFormatService.Create(["en-US"], new DateTimeFormatOptions { Year = "numeric", Month = "numeric", Day = "numeric", Hour = "numeric", Minute = "numeric" }, "en-US", "UTC", zone)),
            ("datetime de full+long", DateTimeFormatService.Create(["de"], new DateTimeFormatOptions { DateStyle = "full", TimeStyle = "long", TimeZone = "America/New_York" }, "en-US", "UTC", zone)),
        };
        foreach (var (name, service) in dates)
        {
            Time(name + " format", () => { foreach (double t in instants) { service.Format(t); } }, instants.Length);
        }

        DateTimeFormatService range = DateTimeFormatService.Create(["en-US"], new DateTimeFormatOptions { Year = "numeric", Month = "short", Day = "numeric" }, "en-US", "UTC", zone);
        Time("datetime en-US formatRange", () => { for (int i = 1; i < instants.Length; i++) { range.FormatRange(instants[i - 1], instants[i]); } }, instants.Length - 1);

        DurationFormatService duration = DurationFormatService.Create(["en"], new DurationFormatOptions { Style = "short" }, "en-US");
        DurationRecord record = DurationRecord.Create([null, null, null, 2, 3, 4, 5, 6, null, null]);
        Time("duration en short format", () => { for (int i = 0; i < 1000; i++) { duration.Format(record); } }, 1000);
    }

    private static void Time(string name, Action run, int perRun)
    {
        for (int i = 0; i < 20; i++)
        {
            run();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var w = Stopwatch.StartNew();
        const int reps = 300;
        for (int i = 0; i < reps; i++)
        {
            run();
        }

        double ns = w.Elapsed.TotalMilliseconds * 1e6 / (reps * (double)perRun);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (reps * (long)perRun);
        File.AppendAllText(Environment.GetEnvironmentVariable("ONEECMA_INTL_THROUGHPUT")!, $"THROUGHPUT {name}: {ns:F0} ns/op ({1e9 / ns / 1e6:F2} M ops/s), {bytes} B/op\n");
    }
}

/// <summary>A zone source with one fixed offset.</summary>
internal sealed class FixedZoneSource(int offsetMs, bool daylight) : ITimeZoneSource
{
    public bool TryGetOffset(string zoneId, double epochMs, out TimeZoneOffset offset)
    {
        offset = new TimeZoneOffset(offsetMs, daylight);
        return true;
    }
}
