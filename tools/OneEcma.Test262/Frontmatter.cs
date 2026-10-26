namespace RecursiveAI.OneEcma.Test262;

/// <summary>The part of a test's frontmatter the runner acts on.</summary>
public sealed class TestMetadata
{
    public string Description { get; init; } = "";

    public IReadOnlyList<string> Flags { get; init; } = [];

    public IReadOnlyList<string> Includes { get; init; } = [];

    public IReadOnlyList<string> Features { get; init; } = [];

    public IReadOnlyList<string> Locales { get; init; } = [];

    /// <summary>Phase of the expected error (parse, resolution or runtime), or null for a positive test.</summary>
    public string? NegativePhase { get; init; }

    /// <summary>Constructor name of the expected error, or null for a positive test.</summary>
    public string? NegativeType { get; init; }

    public bool IsNegative => NegativePhase is not null;

    public bool HasFlag(string flag) => Flags.Contains(flag);

    /// <summary>
    /// Parses the <c>/*--- ... ---*/</c> block. The block is a small YAML subset: scalar values,
    /// inline lists (<c>[a, b]</c>), block lists (<c>- a</c>) and the two-key <c>negative</c> map.
    /// Returns metadata with no flags when the file has no block.
    /// </summary>
    public static TestMetadata Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int start = source.IndexOf("/*---", StringComparison.Ordinal);
        if (start < 0)
        {
            return new TestMetadata();
        }

        int end = source.IndexOf("---*/", start + 5, StringComparison.Ordinal);
        if (end < 0)
        {
            return new TestMetadata();
        }

        var entries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (string raw in source.AsSpan(start + 5, end - start - 5).ToString().Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]) && line[0] != '-')
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    string key = line[..colon].Trim();
                    current = [line[(colon + 1)..].Trim()];
                    entries[key] = current;
                    continue;
                }
            }

            current?.Add(line.Trim());
        }

        string? phase = null;
        string? type = null;
        if (entries.TryGetValue("negative", out var negative))
        {
            foreach (string l in negative.Skip(1))
            {
                if (l.StartsWith("phase:", StringComparison.Ordinal))
                {
                    phase = Unquote(l[6..].Trim());
                }
                else if (l.StartsWith("type:", StringComparison.Ordinal))
                {
                    type = Unquote(l[5..].Trim());
                }
            }
        }

        return new TestMetadata
        {
            Description = entries.TryGetValue("description", out var d) ? d[0] : "",
            Flags = List(entries, "flags"),
            Includes = List(entries, "includes"),
            Features = List(entries, "features"),
            Locales = List(entries, "locale"),
            NegativePhase = phase,
            NegativeType = type,
        };
    }

    private static List<string> List(Dictionary<string, List<string>> entries, string key)
    {
        var result = new List<string>();
        if (!entries.TryGetValue(key, out var lines))
        {
            return result;
        }

        string first = lines[0];
        if (first.StartsWith('['))
        {
            // An inline list may continue over several lines until the closing bracket.
            string joined = string.Join(' ', lines);
            int close = joined.IndexOf(']', StringComparison.Ordinal);
            string inner = joined[1..(close < 0 ? joined.Length : close)];
            foreach (string item in inner.Split(','))
            {
                string t = Unquote(item.Trim());
                if (t.Length > 0)
                {
                    result.Add(t);
                }
            }

            return result;
        }

        foreach (string l in lines.Skip(1))
        {
            if (l.StartsWith('-'))
            {
                string t = Unquote(l[1..].Trim());
                if (t.Length > 0)
                {
                    result.Add(t);
                }
            }
        }

        return result;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s[1..^1] : s;
}
