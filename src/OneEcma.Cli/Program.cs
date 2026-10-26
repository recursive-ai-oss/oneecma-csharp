using RecursiveAI.OneEcma;

namespace RecursiveAI.OneEcma.Cli;

internal static class Program
{
    private const string Usage =
        "usage: oneecma run <file>\n" +
        "       oneecma check <file>\n" +
        "       oneecma repl\n";

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Out.Write(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] is "--version")
        {
            Console.Out.WriteLine(ScriptEngine.Version);
            return 0;
        }

        var engine = new ScriptEngine();
        try
        {
            switch (args[0])
            {
                case "run" when args.Length == 2:
                    Console.Out.WriteLine(engine.Evaluate(File.ReadAllText(args[1])));
                    return 0;
                case "check" when args.Length == 2:
                    engine.Check(File.ReadAllText(args[1]));
                    return 0;
                case "repl" when args.Length == 1:
                    Console.Error.WriteLine("oneecma: repl is not implemented yet");
                    return 1;
                default:
                    Console.Error.Write(Usage);
                    return 2;
            }
        }
        catch (NotSupportedException e)
        {
            Console.Error.WriteLine("oneecma: " + e.Message);
            return 1;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine("oneecma: " + e.Message);
            return 1;
        }
    }
}
