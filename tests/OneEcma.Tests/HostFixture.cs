using RecursiveAI.OneEcma.Diagnostics;
using Shop;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>An engine with the sample order service installed as <c>api</c>, as the host-boundary tests and golden cases use it.</summary>
internal sealed class HostFixture
{
    internal HostFixture()
    {
        Engine = new ScriptEngine(new ScriptEngineOptions { Limits = new EngineLimits { Fuel = 50_000_000, MaxAllocatedBytes = 128L << 20 }, Configure = Setup });
        Realm = Engine.MainRealm;
    }

    /// <summary>Builds the host world; it is the engine's Configure step, so a replay of a failed run rebuilds it and can explain the failure.</summary>
    private void Setup(ScriptEngine engine)
    {
        JsRealm realm = engine.MainRealm;
        JsExports.InstallAll(realm);
        Api ??= new OrderApi();
        realm.DefineData(realm.Global, "api", realm.Wrap(Api));
        engine.DefineGlobalFunction("print", 1, (_, a) =>
        {
            Output.Add(engine.ToDisplayString(a.Length > 0 ? a[0] : default));
            return JsValue.Undefined;
        });
    }

    internal ScriptEngine Engine { get; }

    internal JsRealm Realm { get; }

    internal OrderApi Api { get; private set; } = null!;

    internal List<string> Output { get; } = [];

    /// <summary>Runs a script to completion (waiting for host tasks) and returns the exception it ended with, or null.</summary>
    internal async Task<JsException?> RunAsync(string source, string name = "case.js")
    {
        try
        {
            await Engine.EvaluateAsync(source, name, TestContext.Current.CancellationToken);
            return null;
        }
        catch (JsException e)
        {
            return e;
        }
    }

    internal static string Render(JsException e) => DiagnosticRenderer.Render(e.Report!);
}
