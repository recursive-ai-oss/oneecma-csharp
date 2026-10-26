namespace RecursiveAI.OneEcma.Test262;

public sealed class RunOptions
{
    public required string SuiteRoot { get; init; }

    public required Scope Scope { get; init; }

    public DocumentedFailures Documented { get; init; } = DocumentedFailures.Empty;

    /// <summary>Directory or file prefixes relative to test/. Empty means all roots in scope.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];

    public bool ParseOnly { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public int Jobs { get; init; } = Environment.ProcessorCount;

    /// <summary>Called after each finished test with (done, total), for progress output.</summary>
    public Action<int, int>? Progress { get; init; }
}

/// <summary>Finds the tests, decides what is skipped, runs the rest in parallel and judges the outcomes.</summary>
public sealed class Runner
{
    private readonly RunOptions _options;
    private readonly ITestBackend _backend;
    private readonly TestPreparer _preparer;

    public Runner(RunOptions options, ITestBackend backend)
    {
        _options = options;
        _backend = backend;
        _preparer = new TestPreparer(options.SuiteRoot);
    }

    public IReadOnlyList<string> Discover()
    {
        string testRoot = Path.Combine(_options.SuiteRoot, "test");
        var files = new List<string>();
        foreach (string root in _options.Scope.Roots)
        {
            string dir = Path.Combine(testRoot, root);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Contains("_FIXTURE", StringComparison.Ordinal))
                {
                    continue;
                }

                string rel = Path.GetRelativePath(testRoot, f).Replace('\\', '/');
                if (Matches(rel))
                {
                    files.Add(rel);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private bool Matches(string rel)
    {
        if (_options.Filters.Count == 0)
        {
            return true;
        }

        foreach (string f in _options.Filters)
        {
            string p = f.TrimEnd('/');
            if (rel.Equals(p, StringComparison.Ordinal) || rel.StartsWith(p + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<TestResult>> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tests = Discover();
        var results = new TestResult[tests.Count];
        int done = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, tests.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.Jobs, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                results[i] = await RunOneAsync(tests[i], ct).ConfigureAwait(false);
                _options.Progress?.Invoke(Interlocked.Increment(ref done), tests.Count);
            }).ConfigureAwait(false);
        return results;
    }

    private async Task<TestResult> RunOneAsync(string rel, CancellationToken ct)
    {
        string full = Path.Combine(_options.SuiteRoot, "test", rel);
        string source = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        TestMetadata meta = TestMetadata.Parse(source);

        TestResult? skip = Skip(rel, meta);
        if (skip is not null)
        {
            return skip;
        }

        string? failure = null;
        bool notImplemented = false;
        try
        {
            foreach (PreparedTest run in _preparer.Prepare(rel, full, source, meta, _options.ParseOnly))
            {
                BackendResult r = await _backend.RunAsync(run, _options.Timeout, ct).ConfigureAwait(false);
                string? verdict = Judge(run, r);
                if (verdict is not null)
                {
                    failure = $"[{run.Mode}] {verdict}";
                    notImplemented = r.NotImplemented;
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failure = "harness: " + e.Message;
        }

        string? reason = _options.Documented.Find(rel);
        if (failure is null)
        {
            return new TestResult(rel, TestStatus.Pass, StaleDocumented: reason is not null);
        }

        return reason is not null
            ? new TestResult(rel, TestStatus.Documented, reason, notImplemented)
            : new TestResult(rel, TestStatus.Fail, failure, notImplemented);
    }

    private TestResult? Skip(string rel, TestMetadata meta)
    {
        string? feature = _options.Scope.FindOutOfScopeFeature(meta.Features);
        if (feature is not null)
        {
            return new TestResult(rel, TestStatus.SkippedFeature, feature);
        }

        string? hostFeature = meta.Features.FirstOrDefault(_backend.UnsupportedFeatures.Contains);
        if (hostFeature is not null)
        {
            return new TestResult(rel, TestStatus.SkippedHost, "host lacks " + hostFeature);
        }

        string? locale = _options.Scope.FindUnsupportedLocale(meta.Locales);
        if (locale is not null)
        {
            return new TestResult(rel, TestStatus.SkippedLocale, locale);
        }

        if ((meta.HasFlag("CanBlockIsTrue") && !_backend.CanBlock) || (meta.HasFlag("CanBlockIsFalse") && _backend.CanBlock))
        {
            return new TestResult(rel, TestStatus.SkippedHost, "CanBlock");
        }

        if (_options.ParseOnly)
        {
            if (!_backend.SupportsParseOnly)
            {
                return new TestResult(rel, TestStatus.SkippedHost, "backend cannot parse only");
            }

            // Parse-only covers tests whose verdict is decided by the parser.
            if (meta.IsNegative && meta.NegativePhase != "parse")
            {
                return new TestResult(rel, TestStatus.SkippedParseOnly, "negative " + meta.NegativePhase);
            }
        }

        return null;
    }

    /// <summary>Returns null when the run satisfies the test, otherwise a description of what was wrong.</summary>
    public static string? Judge(PreparedTest run, BackendResult r)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(r);
        if (r.NotImplemented)
        {
            return "not implemented: " + r.Message;
        }

        if (r.TimedOut)
        {
            return "timeout";
        }

        TestMetadata meta = run.Meta;
        if (!meta.IsNegative)
        {
            return r.Threw ? $"unexpected {r.ErrorType} ({r.Phase}): {Trim(r.Message)}" : null;
        }

        if (!r.Threw)
        {
            return $"expected {meta.NegativeType} in {meta.NegativePhase} phase, but it completed";
        }

        if (r.Phase != meta.NegativePhase || r.ErrorType != meta.NegativeType)
        {
            return $"expected {meta.NegativeType} in {meta.NegativePhase} phase, got {r.ErrorType} in {r.Phase}: {Trim(r.Message)}";
        }

        return null;
    }

    private static string Trim(string s)
    {
        string one = s.ReplaceLineEndings(" ");
        return one.Length <= 200 ? one : one[..200] + "...";
    }
}
