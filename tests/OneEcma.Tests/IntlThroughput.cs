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
