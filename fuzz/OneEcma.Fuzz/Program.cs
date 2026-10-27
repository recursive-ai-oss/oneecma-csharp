using System.Text;
using RecursiveAI.OneEcma;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Stub harness: feeds stdin to the parser entry point. Coverage-guided drivers are wired in
/// here once there is a parser to drive.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // regexp <seconds> [seed] [corpus-file]: seeded pattern/input fuzzing of the RegExp parser and matcher.
        // regexp-file <path>: one byte-encoded case (pattern length, flags selector, pattern, input) for replaying a finding.
        if (args.Length >= 2 && args[0] == "regexp")
        {
            return RegExpFuzz.RunRandom(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1, args.Length > 3 ? args[3] : null) == 0 ? 0 : 1;
        }

        if (args.Length == 1 && args[0] == "regexp-throughput")
        {
            RegExpFuzz.Throughput();
            return 0;
        }

        if (args.Length == 2 && args[0] == "regexp-file")
        {
            string? failure = RegExpFuzz.RunBytes(File.ReadAllBytes(args[1]));
            Console.WriteLine(failure ?? "ok");
            return failure is null ? 0 : 1;
        }

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
