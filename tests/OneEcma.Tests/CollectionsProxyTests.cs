using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Map, Set, weak collections, FinalizationRegistry and Proxy: behaviour the test262 directories do not pin down.</summary>
public class CollectionsProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingHost : DefaultHost, IJsHost
    {
        internal readonly StringBuilder Output = new();
        internal readonly List<IReadOnlyList<long>> Collections = [];
        internal IReadOnlyList<long>? Replay;

        public override void Print(string text) => Output.Append(text).Append('\n');

        void IJsHost.WeakTargetsCollected(IReadOnlyList<long> ids) => Collections.Add(ids.ToArray());

        IReadOnlyList<long>? IJsHost.ReplayWeakCollection() => Replay;
    }

    private static (ScriptEngine Engine, RecordingHost Host) Engine(IReadOnlyList<long>? replay = null)
    {
        var host = new RecordingHost { Replay = replay };
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true, Limits = new EngineLimits { Fuel = 400_000_000 } });
        return (engine, host);
    }

    private static string Run(string source)
    {
        (ScriptEngine e, RecordingHost h) = Engine();
        e.Evaluate(source, "t.js", Ct);
        return h.Output.ToString().TrimEnd('\n');
    }

    [Fact]
    public void MapIterationSeesDeletesClearsAndAppendsLive()
    {
        string o = Run("""
            const m = new Map([[1, 'a'], [2, 'b'], [3, 'c']]);
            const seen = [];
            for (const [k] of m) {
              seen.push(k);
              if (k === 1) { m.delete(2); m.set(4, 'd'); }
              if (k === 3) { m.clear(); m.set(5, 'e'); }
            }
            print(seen.join());
            const s = new Set([1, 2, 3]);
            const it = s.values();
            it.next(); s.delete(1); s.delete(2); s.delete(3); s.add(9);
            const n1 = it.next(), n2 = it.next();
            print(n1.value + ',' + (n1.done ? 'done' : 'more') + ',' + n2.done);
            const z = new Map([[-0, 1]]);
            print(Object.is([...z.keys()][0], 0) + ',' + new Map([[NaN, 1]]).get(NaN));
            """);
        Assert.Equal("1,3,5\n9,more,true\ntrue,1", o);
    }

    [Fact]
    public void LargeMapDeletesStayLinear()
    {
        // 200k inserts then deletes while an iterator is parked: no step may be O(n).
        string o = Run("""
            const m = new Map();
            for (let i = 0; i < 200000; i++) m.set(i, i);
            const it = m.keys();
            it.next();
            for (let i = 0; i < 199999; i++) m.delete(i);
            print(JSON.stringify(it.next()) + m.size);
            """);
        Assert.Equal("{\"value\":199999,\"done\":false}1", o);
    }

    [Fact]
    public void SetMethodsFollowTheSpecOrder()
    {
        string o = Run("""
            const a = new Set([1, 2, 3, 4]);
            const b = new Set([3, 4, 5]);
            print([...a.union(b)].join() + '|' + [...a.intersection(b)].join() + '|' + [...a.difference(b)].join() + '|' + [...a.symmetricDifference(b)].join());
            print([a.isSubsetOf(b), new Set([3]).isSubsetOf(b), a.isSupersetOf(new Set([1, 2])), a.isDisjointFrom(new Set([9]))].join());
            const log = [];
            const other = { get size() { log.push('size'); return 2; }, get has() { log.push('has'); return () => false; }, get keys() { log.push('keys'); return () => [][Symbol.iterator](); } };
            a.union(other);
            print(log.join());
            """);
        Assert.Equal("1,2,3,4,5|3,4|1,2|1,2,5\nfalse,true,true,true\nsize,has,keys", o);
    }

    [Fact]
    public void UpsertAndGroupBy()
    {
        string o = Run("""
            const m = new Map();
            print(m.getOrInsert('k', 1) + ',' + m.getOrInsert('k', 2) + ',' + m.getOrInsertComputed('j', k => k + '!') + ',' + m.size);
            const wm = new WeakMap(); const key = {};
            print(wm.getOrInsert(key, 7) + ',' + wm.getOrInsertComputed(key, () => 8));
            const g = Map.groupBy([1, 2, 3, 4, 5], x => x % 2 ? 'odd' : 'even');
            print([...g.keys()].join() + ':' + g.get('odd').join());
            """);
        Assert.Equal("1,1,j!,2\n7,7\nodd,even:1,3,5", o);
    }

    [Fact]
    public void DeepProxyChainsEndInARangeErrorNotAHostCrash()
    {
        string o = Run("""
            function chain(handler) { let p = {}; for (let i = 0; i < 100000; i++) p = new Proxy(p, handler()); return p; }
            const plain = chain(() => ({}));
            const trapped = chain(() => ({ get: Reflect.get, has: Reflect.has, ownKeys: Reflect.ownKeys, getOwnPropertyDescriptor: Reflect.getOwnPropertyDescriptor }));
            function name(f) { try { f(); return 'ok'; } catch (e) { return e.name; } }
            print([name(() => plain.x), name(() => 'x' in plain), name(() => Object.keys(plain)), name(() => plain.x = 1),
                   name(() => trapped.x), name(() => 'x' in trapped), name(() => Object.keys(trapped)), name(() => Array.isArray(trapped)),
                   name(() => Object.getPrototypeOf(plain)), name(() => JSON.stringify(trapped))].join());
            """);
        string[] parts = o.Split(',');
        Assert.Equal(10, parts.Length);
        Assert.All(parts, p => Assert.True(p is "ok" or "RangeError", p));
        Assert.Contains("RangeError", parts);
    }

    [Fact]
    public void RevokedAndNestedProxiesBehave()
    {
        string o = Run("""
            const { proxy, revoke } = Proxy.revocable(function f() { return 1; }, {});
            const before = proxy();
            revoke(); revoke();
            let msg = ''; try { proxy(); } catch (e) { msg = e.message; }
            print(before + '|' + msg + '|' + typeof proxy);
            const arr = new Proxy(new Proxy([], {}), {});
            print(Array.isArray(arr) + ',' + Object.prototype.toString.call(arr) + ',' + JSON.stringify(new Proxy([1, { a: 2 }], {})));
            """);
        Assert.Equal("1|Cannot perform 'apply' on a proxy that has been revoked|function\ntrue,[object Array],[1,{\"a\":2}]", o);
    }

    // The tests below build garbage in one Evaluate and collect it in a later one: the interpreter's finished frames are
    // then unreachable, so what a script sees depends only on the engine's collection points.

    [Fact]
    public void WeakRefTargetsDieAtACollectionPointAndOnlyThere()
    {
        (ScriptEngine e, RecordingHost h) = Engine();
        e.Evaluate("""
            var held = {};
            var keep = new WeakRef(held);
            var gone = (function () { return new WeakRef({}); })();
            for (let i = 0; i < 20000; i++) ({ junk: new Array(10) });
            """, "a.js", Ct);
        // A .NET collection by itself changes nothing a script can see.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        e.Evaluate("print(String(gone.deref() !== undefined))", "b.js", Ct);
        e.Evaluate("$262.gc(); print(String(gone.deref() === undefined) + ',' + String(keep.deref() === held))", "c.js", Ct);
        Assert.Equal("true\ntrue,true", h.Output.ToString().TrimEnd('\n'));
    }

    [Fact]
    public void KeptObjectsSurviveACollectionInTheSameRun()
    {
        (ScriptEngine e, RecordingHost h) = Engine();
        e.Evaluate("var r = (function () { return new WeakRef({}); })();", "a.js", Ct);
        e.Evaluate("void r.deref(); $262.gc(); print(String(r.deref() !== undefined));", "b.js", Ct);
        e.Evaluate("$262.gc(); print(String(r.deref() === undefined));", "c.js", Ct);
        Assert.Equal("true\ntrue", h.Output.ToString().TrimEnd('\n'));
    }

    [Fact]
    public void WeakMapValuesDoNotKeepTheirKeysAlive()
    {
        // The value points back at the key; an ephemeron table must still let both go.
        (ScriptEngine e, RecordingHost h) = Engine();
        e.Evaluate("""
            var wm = new WeakMap();
            var reg = new FinalizationRegistry(h => print('cleaned ' + h));
            (function () {
              const key = {};
              wm.set(key, { back: key });
              reg.register(key, 'k');
            })();
            """, "a.js", Ct);
        e.Evaluate("$262.gc();", "b.js", Ct);
        Assert.Equal("cleaned k", h.Output.ToString().TrimEnd('\n'));
    }

    private const string CleanupSetup = """
        var log = [];
        var reg = new FinalizationRegistry(h => log.push(h));
        var live = [];
        (function () {
          for (let i = 0; i < 4; i++) {
            const o = { i };
            reg.register(o, 'h' + i);
            if (i % 2 === 0) live.push(o);
          }
        })();
        """;

    [Fact]
    public void FinalizationCleanupRunsAsAJobAfterTheHostCollects()
    {
        (ScriptEngine e, RecordingHost h) = Engine();
        e.Evaluate(CleanupSetup, "a.js", Ct);
        e.Evaluate("$262.gc(); print('after gc, before jobs: ' + log.length); Promise.resolve().then(() => print('after jobs: ' + log.join()));", "b.js", Ct);
        // The unreachable objects are the odd ones (ids 2 and 4); cleanup ran on the job queue, in registration order.
        Assert.Equal("after gc, before jobs: 0\nafter jobs: h1,h3", h.Output.ToString().TrimEnd('\n'));
        Assert.Single(h.Collections);
        Assert.Equal([2L, 4L], h.Collections[0]);
    }

    [Fact]
    public void ARecordedCollectionReplaysEvenWhenNothingIsGarbage()
    {
        (ScriptEngine e, RecordingHost h) = Engine(replay: [1L, 3L]);
        // Every object stays reachable; the replaying host alone decides which ones died.
        e.Evaluate("""
            var log = [];
            var reg = new FinalizationRegistry(h => log.push(h));
            var all = [];
            for (let i = 0; i < 4; i++) { const o = { i }; all.push(o); reg.register(o, 'h' + i); }
            $262.gc();
            Promise.resolve().then(() => print(log.join() + '/' + all.length));
            """, "r.js", Ct);
        Assert.Equal("h0,h2/4", h.Output.ToString().TrimEnd('\n'));
        Assert.Empty(h.Collections);
    }
}
