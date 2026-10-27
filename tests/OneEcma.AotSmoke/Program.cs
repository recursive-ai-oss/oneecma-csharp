using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.RegExp;

// Published with NativeAOT in CI: the build must produce no trim or AOT warnings and this must run.
// RegExp: loads the embedded Unicode table (a manifest resource) and runs a property escape and a case-folded match.
var re = RegExpProgram.Compile(@"^\p{Script=Greek}+(?<n>\w)\k<n>$", "iu");
var hit = re.Program?.Exec("αΑss", 0, false);
if (hit?.Match is null || hit.Match.End != 4)
{
    Console.Error.WriteLine("smoke: regexp failed");
    return 1;
}

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
