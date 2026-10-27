using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;

// Published with NativeAOT in CI: the build must produce no trim or AOT warnings and this must run.
var engine = new ScriptEngine();
if (engine.Check("let a = 1; function f(x) { return x + a; }").Count != 0)
{
    Console.Error.WriteLine("smoke: valid script reported errors");
    return 1;
}

var errors = engine.Check("let a = ;\nlet ok = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];\nlet b = (1 + );");
string report = DiagnosticRenderer.RenderAll(errors);
if (errors.Count != 2 || !report.StartsWith("error[SyntaxError]: Unexpected token ';'", StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke: unexpected report\n" + report);
    return 1;
}

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
