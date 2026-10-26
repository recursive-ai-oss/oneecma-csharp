using System.Text;

namespace RecursiveAI.OneEcma.Test262;

internal static class Program
{
    private const string Usage = """
        oneecma-test262: run the test262 suite against a backend.

        Usage: oneecma-test262 [options] [filter ...]

          filter                  directory or file under test/ (e.g. language/expressions); default: all in scope
          --backend engine|node   engine (default) = the library; node = Node as a black-box reference
          --node <path>           node executable for the node backend (default: node on PATH)
          --suite <dir>           test262 checkout (default: $ONEECMA_TEST262, else $HOME/oneecma/test262/<pinned sha>)
          --parse-only            only parse: negative parse-phase tests and positive parses
          --scope <file>          scope file (default: tools/test262/scope.txt)
          --documented <file>     documented failures (default: none)
          --jobs <n>              parallel tests (default: all cores)
          --timeout <seconds>     per-test limit (default 30)
          --json <file>           write the machine-readable summary
          --failures <file>       write one line per failing test with the reason
          --depth <n>             directory depth in the text summary (default 2)
          --quiet                 no progress output
        """;

    private static async Task<int> Main(string[] args)
    {
        var filters = new List<string>();
        string backendName = "engine";
        string? nodePath = null, suite = null, scopePath = null, docPath = null, jsonPath = null, failPath = null;
        bool parseOnly = false, quiet = false;
        int jobs = Environment.ProcessorCount, depth = 2;
        double timeout = 30;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "--backend": backendName = Next(); break;
                    case "--node": nodePath = Next(); break;
                    case "--suite": suite = Next(); break;
                    case "--scope": scopePath = Next(); break;
                    case "--documented": docPath = Next(); break;
                    case "--json": jsonPath = Next(); break;
                    case "--failures": failPath = Next(); break;
                    case "--jobs": jobs = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--depth": depth = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--timeout": timeout = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--parse-only": parseOnly = true; break;
                    case "--quiet": quiet = true; break;
                    case "-h" or "--help": Console.WriteLine(Usage); return 0;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException("unknown option " + a);
                        }

                        filters.Add(a);
                        break;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string? dataDir = FindDataDir();
        string pinned = dataDir is null ? "" : File.ReadAllText(Path.Combine(dataDir, "pin.txt")).Split('\n')[0].Trim();
        suite ??= Environment.GetEnvironmentVariable("ONEECMA_TEST262")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "oneecma", "test262", pinned);
        scopePath ??= dataDir is null ? null : Path.Combine(dataDir, "scope.txt");
        if (scopePath is null || !File.Exists(scopePath) || !Directory.Exists(suite))
        {
            Console.Error.WriteLine($"need a scope file ({scopePath}) and a test262 checkout ({suite}); run tools/test262/fetch.sh");
            return 2;
        }

        ITestBackend backend;
        switch (backendName)
        {
            case "engine": backend = new EngineBackend(); break;
            case "node": backend = new NodeBackend(nodePath ?? "node"); break;
            default:
                Console.Error.WriteLine("unknown backend " + backendName);
                return 2;
        }

        using var disposable = backend as IDisposable;
        var options = new RunOptions
        {
            SuiteRoot = suite,
            Scope = Scope.Load(scopePath),
            Documented = docPath is null ? DocumentedFailures.Empty : DocumentedFailures.Load(docPath),
            Filters = filters,
            ParseOnly = parseOnly,
            Timeout = TimeSpan.FromSeconds(timeout),
            Jobs = jobs,
            Progress = quiet ? null : (done, total) =>
            {
                if (done % 2000 == 0 || done == total)
                {
                    Console.Error.WriteLine($"  {done}/{total}");
                }
            },
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        IReadOnlyList<TestResult> results = await new Runner(options, backend).RunAsync(cts.Token).ConfigureAwait(false);

        var report = new Report(results);
        Console.Write(report.ToText(backend.Name + (parseOnly ? " (parse only)" : ""), depth));

        if (jsonPath is not null)
        {
            using FileStream f = File.Create(jsonPath);
            report.WriteJson(f, backend.Name, pinned, parseOnly);
        }

        if (failPath is not null)
        {
            var sb = new StringBuilder();
            foreach (TestResult r in results.Where(r => r.Status == TestStatus.Fail))
            {
                sb.Append(r.RelativePath).Append('\t').AppendLine(r.Detail.ReplaceLineEndings(" "));
            }

            File.WriteAllText(failPath, sb.ToString());
        }

        return 0;
    }

    /// <summary>Finds tools/test262 by walking up from the working directory or the executable.</summary>
    private static string? FindDataDir()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "tools", "test262");
                if (File.Exists(Path.Combine(candidate, "pin.txt")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
