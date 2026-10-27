using RecursiveAI.OneEcma.Diagnostics;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>The host's policy for weak-reference collection points: when it asks for one, what dies, and that a replay reaches the same points.</summary>
public class WeakCollectionPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class EagerHost : DefaultHost
    {
        internal int Questions;
        internal bool Answer = true;

        public override bool WantsWeakCollection()
        {
            Questions++;
            return Answer;
        }
    }

    private sealed class SmallGrowthHost : DefaultHost
    {
        protected override long WeakCollectionGrowthBytes => 1 << 20;
    }

    private const string Make = "globalThis.r = (function () { return new WeakRef({ a: 1 }); })(); globalThis.held = {}; globalThis.k = new WeakRef(held);";

    [Fact]
    public void WithoutAPolicyTargetsLiveUntilAScriptAsks()
    {
        var host = new EagerHost { Answer = false };
        var e = new ScriptEngine(new ScriptEngineOptions { Host = host });
        e.Evaluate(Make, "a.js", Ct);
        e.Evaluate("void 0", "b.js", Ct);
        Assert.Equal("true", e.ToDisplayString(e.Evaluate("r.deref() !== undefined", "c.js", Ct)));
        Assert.True(host.Questions > 0);
    }

    [Fact]
    public void AHostThatAsksCollectsAfterTheJobsDrainAndKeepsReachableTargets()
    {
        var e = new ScriptEngine(new ScriptEngineOptions { Host = new EagerHost() });
        e.Evaluate(Make, "a.js", Ct);
        e.RunJobs(Ct);
        Assert.Equal("true,true", e.ToDisplayString(e.Evaluate("[r.deref() === undefined, k.deref() === held].join()", "c.js", Ct)));
    }

    [Fact]
    public void NoQuestionIsAskedBeforeAnyWeakTargetExists()
    {
        var host = new EagerHost();
        var e = new ScriptEngine(new ScriptEngineOptions { Host = host });
        e.Evaluate("Promise.resolve(1).then(() => 2)", "a.js", Ct);
        e.RunJobs(Ct);
        Assert.Equal(0, host.Questions);
    }

    [Fact]
    public void ARegistryCallbackRunsAfterAPolicyCollectionPoint()
    {
        var e = new ScriptEngine(new ScriptEngineOptions { Host = new EagerHost() });
        e.Evaluate("""
            globalThis.log = [];
            globalThis.reg = new FinalizationRegistry((v) => log.push('cleaned ' + v));
            (function () { reg.register({}, 'x'); })();
            """, "a.js", Ct);
        e.RunJobs(Ct);
        Assert.Equal("cleaned x", e.ToDisplayString(e.Evaluate("log.join()", "b.js", Ct)));
    }

    [Fact]
    public void ReplayReachesTheSameCollectionPoints()
    {
        var e = new ScriptEngine(new ScriptEngineOptions { Host = new EagerHost() });
        e.Evaluate(Make, "a.js", Ct);
        e.RunJobs(Ct);
        string original = e.ToDisplayString(e.Evaluate("[r.deref() === undefined, k.deref() === held].join()", "c.js", Ct));
        Assert.Equal("true,true", original);
        Assert.True(e.RecordLog.IsReplayable);

        ReplayResult rep = ScriptEngine.Replay(e.RecordLog, new ReplayOptions { Trace = false }, Ct);
        Assert.Null(rep.Divergence);
        Assert.Equal(original, rep.Engine.ToDisplayString(rep.Value));

        // The log also survives serialization, and a run whose policy said no replays without a collection point.
        ReplayResult again = ScriptEngine.Replay(RecordLog.FromArray(e.RecordLog.ToArray()), new ReplayOptions { Trace = false }, Ct);
        Assert.Null(again.Divergence);
        Assert.Equal(original, again.Engine.ToDisplayString(again.Value));

        var quiet = new ScriptEngine(new ScriptEngineOptions { Host = new EagerHost { Answer = false } });
        quiet.Evaluate(Make, "a.js", Ct);
        quiet.RunJobs(Ct);
        string q = quiet.ToDisplayString(quiet.Evaluate("[r.deref() === undefined, k.deref() === held].join()", "c.js", Ct));
        ReplayResult qr = ScriptEngine.Replay(quiet.RecordLog, new ReplayOptions { Trace = false }, Ct);
        Assert.Null(qr.Divergence);
        Assert.Equal("false,true", q);
        Assert.Equal(q, qr.Engine.ToDisplayString(qr.Value));
    }

    [Fact]
    public void ACollectionPointNextToAScriptRequestedOneReplays()
    {
        var e = new ScriptEngine(new ScriptEngineOptions { Host = new EagerHost(), InstallTest262Host = true });
        e.Evaluate(Make + " $262.gc();", "a.js", Ct);
        e.Evaluate("$262.gc(); globalThis.s = new WeakRef({});", "b.js", Ct);
        e.RunJobs(Ct);
        string original = e.ToDisplayString(e.Evaluate("[r.deref() === undefined, s.deref() === undefined].join()", "c.js", Ct));
        ReplayResult rep = ScriptEngine.Replay(e.RecordLog, new ReplayOptions { Trace = false, InstallTest262Host = true }, Ct);
        Assert.Null(rep.Divergence);
        Assert.Equal(original, rep.Engine.ToDisplayString(rep.Value));
    }

    [Fact]
    public void TheDefaultHostAsksOnlyAfterManagedMemoryHasGrown()
    {
        var host = new SmallGrowthHost();
        Assert.False(host.WantsWeakCollection());
        Assert.False(host.WantsWeakCollection());
        byte[] ballast = new byte[8 << 20];
        Assert.True(host.WantsWeakCollection());
        // After a yes the next question measures from the level then reached.
        Assert.False(host.WantsWeakCollection());
        GC.KeepAlive(ballast);
    }
}
