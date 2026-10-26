using System.Text;
using RecursiveAI.OneEcma;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Stub harness: feeds stdin to the parser entry point. Coverage-guided drivers are wired in
/// here once there is a parser to drive.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        using var stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        string source = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        try
        {
            new ScriptEngine().Check(source);
        }
        catch (NotSupportedException)
        {
            // The parser does not exist yet; any other exception type is a finding.
        }

        return 0;
    }
}
