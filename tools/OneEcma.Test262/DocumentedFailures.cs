namespace RecursiveAI.OneEcma.Test262;

/// <summary>
/// Known failures, each with a reason. A gate reads "passes or is documented" from this.
/// </summary>
/// <remarks>
/// File format, one entry per line: <c>&lt;path&gt; | &lt;reason&gt;</c>. The path is relative to the
/// suite's <c>test/</c> directory. A path ending in <c>/</c> covers a whole directory; a path
/// containing <c>*</c> is a glob where <c>*</c> matches any characters. Blank lines and lines
/// starting with <c>#</c> are ignored. The reason is required.
/// </remarks>
public sealed class DocumentedFailures
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, string Reason)> _prefixes = [];
    private readonly List<(System.Text.RegularExpressions.Regex Pattern, string Reason)> _globs = [];

    public static DocumentedFailures Empty { get; } = new();

    public int Count => _exact.Count + _prefixes.Count + _globs.Count;

    public static DocumentedFailures Load(string path)
    {
        var result = new DocumentedFailures();
        int n = 0;
        foreach (string raw in File.ReadLines(path))
        {
            n++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            if (bar < 0 || line[(bar + 3)..].Trim().Length == 0)
            {
                throw new InvalidDataException($"{path}:{n}: expected '<path> | <reason>'");
            }

            string p = line[..bar].Trim();
            string reason = line[(bar + 3)..].Trim();
            if (p.Contains('*', StringComparison.Ordinal))
            {
                string rx = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
                result._globs.Add((new System.Text.RegularExpressions.Regex(rx, System.Text.RegularExpressions.RegexOptions.CultureInvariant), reason));
            }
            else if (p.EndsWith('/'))
            {
                result._prefixes.Add((p, reason));
            }
            else
            {
                result._exact[p] = reason;
            }
        }

        return result;
    }

    /// <summary>Returns the reason a test is documented as failing, or null.</summary>
    public string? Find(string relativePath)
    {
        if (_exact.TryGetValue(relativePath, out var reason))
        {
            return reason;
        }

        foreach (var (prefix, r) in _prefixes)
        {
            if (relativePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return r;
            }
        }

        foreach (var (pattern, r) in _globs)
        {
            if (pattern.IsMatch(relativePath))
            {
                return r;
            }
        }

        return null;
    }
}
