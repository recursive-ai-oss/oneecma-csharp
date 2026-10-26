namespace RecursiveAI.OneEcma.Test262;

/// <summary>
/// Turns a test file into the runs the suite requires: strict and sloppy unless a flag says
/// otherwise, the module run for module tests, and the harness files in the order test262 defines.
/// </summary>
public sealed class TestPreparer
{
    private readonly string _harnessDir;
    private readonly Dictionary<string, HarnessScript> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public TestPreparer(string suiteRoot)
    {
        _harnessDir = Path.Combine(suiteRoot, "harness");
    }

    public IReadOnlyList<PreparedTest> Prepare(string relativePath, string fullPath, string source, TestMetadata meta, bool parseOnly)
    {
        bool raw = meta.HasFlag("raw");
        bool module = meta.HasFlag("module");
        bool isAsync = meta.HasFlag("async");

        var harness = new List<HarnessScript>();
        if (!raw)
        {
            var names = new List<string> { "assert.js", "sta.js" };
            if (isAsync)
            {
                names.Add("doneprintHandle.js");
            }

            names.AddRange(meta.Includes);
            foreach (string name in names.Distinct(StringComparer.Ordinal))
            {
                harness.Add(LoadHarness(name));
            }
        }

        SourceMode[] modes = module
            ? [SourceMode.Module]
            : raw || meta.HasFlag("noStrict")
                ? [SourceMode.Sloppy]
                : meta.HasFlag("onlyStrict")
                    ? [SourceMode.Strict]
                    : [SourceMode.Sloppy, SourceMode.Strict];

        var runs = new List<PreparedTest>();
        foreach (SourceMode mode in modes)
        {
            runs.Add(new PreparedTest
            {
                RelativePath = relativePath,
                FullPath = fullPath,
                Meta = meta,
                Mode = mode,
                Raw = raw,
                IsAsync = isAsync,
                ParseOnly = parseOnly,
                Harness = harness,
                Source = source,
            });
        }

        return runs;
    }

    private HarnessScript LoadHarness(string name)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var script = new HarnessScript(name, File.ReadAllText(Path.Combine(_harnessDir, name)));
            _cache[name] = script;
            return script;
        }
    }
}
