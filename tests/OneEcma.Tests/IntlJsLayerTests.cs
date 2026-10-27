using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>The JavaScript surface of Intl: the shared differential script against output recorded from a reference Node, and spot checks.</summary>
public class IntlJsLayerTests
{
    private sealed class PrintHost : DefaultHost
    {
        internal readonly StringBuilder Output = new();

        public override void Print(string text) => Output.Append(text).Append('\n');
    }

    private static string Run(string source)
    {
        var host = new PrintHost();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true });
        engine.Evaluate(source, "t.js", TestContext.Current.CancellationToken);
        return host.Output.ToString().TrimEnd('\n');
    }

    private static string Norm(string s) => s.Replace('\u202f', ' ');

    [Fact]
    public void SurfaceScriptMatchesTheRecordedReferenceExceptDocumentedRows()
    {
        string dir = Path.Combine(RepoPaths.Root, "tools", "intl-diff");
        string actual = Run(File.ReadAllText(Path.Combine(dir, "js-surface.js")));
        var deviations = File.ReadAllLines(Path.Combine(dir, "js-surface.deviations.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l[..l.IndexOf(" | ", StringComparison.Ordinal)]).ToHashSet();

        static string Label(string line)
        {
            int i = line.IndexOf(" => ", StringComparison.Ordinal);
            int j = line.IndexOf(" !! ", StringComparison.Ordinal);
            return line[..(i < 0 ? j : j < 0 ? i : Math.Min(i, j))];
        }

        Dictionary<string, string> got = Norm(actual).Split('\n').ToDictionary(Label);
        var wrong = new List<string>();
        foreach (string line in Norm(File.ReadAllText(Path.Combine(dir, "js-surface.node24.txt"))).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string label = Label(line);
            if (!deviations.Contains(label) && (!got.TryGetValue(label, out string? g) || g != line))
            {
                wrong.Add(line + "\n   engine: " + (got.GetValueOrDefault(label) ?? "(missing)"));
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void LegacyConstructorsChainThroughTheFallbackSymbol()
    {
        string o = Run("""
            var nf = Object.create(Intl.NumberFormat.prototype);
            var r = Intl.NumberFormat.call(nf, 'de');
            print(r === nf);
            print(Intl.NumberFormat.prototype.resolvedOptions.call(nf).locale);
            print(Object.getOwnPropertySymbols(nf).map(String));
            """);
        Assert.Equal("true\nde\nSymbol(IntlLegacyConstructedSymbol)", o);
    }

    [Fact]
    public void LocaleSensitiveMethodsUseTheHostDefaultLocale()
    {
        var host = new PrintHost();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true });
        engine.Evaluate("print((1234.5).toLocaleString()); print(new Intl.DateTimeFormat().resolvedOptions().timeZone)", "t.js", TestContext.Current.CancellationToken);
        Assert.Equal("1,234.5\nUTC", host.Output.ToString().TrimEnd('\n'));
    }

    [Fact]
    public void SpecialCasingAppliesToTurkishAzeriAndLithuanian()
    {
        string o = Run("""
            print('i'.toLocaleUpperCase('tr') + ' ' + 'I'.toLocaleLowerCase('az') + ' ' + 'I\u0307'.toLocaleLowerCase('tr') + ' ' + 'i\u0307'.toLocaleUpperCase('lt'));
            print('I\u0300'.toLocaleLowerCase('lt') === 'i\u0307\u0300');
            """);
        Assert.Equal("\u0130 \u0131 i I\ntrue", o);
    }

    [Fact]
    public void RepeatedLocaleCallsShareOneServiceAndTheCacheStaysBounded()
    {
        var host = new PrintHost();
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true });
        var realm = engine.MainRealm.Inner;
        engine.Evaluate("""
            for (let i = 0; i < 50; i++) {
              (1234.5).toLocaleString('de');
              (1234.5).toLocaleString('de', { style: 'currency', currency: 'EUR' });
              'a'.localeCompare('b', 'es');
              new Date(0).toLocaleDateString('fr', { timeZone: 'UTC' });
            }
            print((1234.5).toLocaleString('de') + '|' + (1234.5).toLocaleString('de', { style: 'currency', currency: 'EUR' }) + '|' + 'a'.localeCompare('b', 'es') + '|' + new Date(0).toLocaleDateString('fr', { timeZone: 'UTC' }));
            """, "t.js", TestContext.Current.CancellationToken);
        Assert.Equal("1.234,5|1.234,50\u00a0\u20ac|-1|01/01/1970", host.Output.ToString().TrimEnd('\n'));
        Assert.Equal(4, IntlJs.CachedServiceCount(realm));

        // Many distinct option sets cannot grow the cache past its limit, and every result is still right.
        engine.Evaluate("""
            for (let d = 0; d < 6; d++) for (let m = 0; m < 20; m++) {
              if ((1.5).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d + m }).length === 0) throw new Error('empty');
            }
            """, "u.js", TestContext.Current.CancellationToken);
        Assert.InRange(IntlJs.CachedServiceCount(realm), 1, 64);
    }

    [Fact]
    public void ACachedServiceStillReadsOptionsInTheObservableOrder()
    {
        string o = Run("""
            const log = [];
            const opts = new Proxy({ style: 'currency', currency: 'USD' }, { get(t, k) { log.push(String(k)); return t[k]; } });
            (5).toLocaleString('en-US', opts);
            const first = log.length;
            log.length = 0;
            (5).toLocaleString('en-US', opts);
            print(first + ',' + log.length + ',' + (log.join() === 'localeMatcher,numberingSystem,style,currency,currencyDisplay,currencySign,unit,unitDisplay,notation,minimumIntegerDigits,minimumFractionDigits,maximumFractionDigits,minimumSignificantDigits,maximumSignificantDigits,roundingIncrement,roundingMode,roundingPriority,trailingZeroDisplay,compactDisplay,useGrouping,signDisplay'.split(',').slice(0, log.length).join()));
            """);
        string[] parts = o.Split(',');
        Assert.Equal(parts[0], parts[1]);
    }
}
