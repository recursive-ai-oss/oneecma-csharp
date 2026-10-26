using RecursiveAI.OneEcma;

// Published with NativeAOT in CI: the build must produce no trim or AOT warnings and this must run.
var engine = new ScriptEngine();
try
{
    engine.Evaluate("1 + 1");
    Console.Error.WriteLine("smoke: expected NotSupportedException");
    return 1;
}
catch (NotSupportedException)
{
    Console.Out.WriteLine("aot-smoke ok " + ScriptEngine.Version);
    return 0;
}
