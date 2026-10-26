namespace RecursiveAI.OneEcma.Test262;

/// <summary>
/// What is in scope: the test roots, the features that belong to later editions or unfinished
/// proposals, and the locales the Intl implementation supports. Loaded from one committed file
/// so that scope changes are reviewable.
/// </summary>
/// <remarks>
/// File format, one entry per line, <c>#</c> starts a comment:
/// <c>root &lt;dir&gt;</c>, <c>feature-out &lt;feature&gt; | &lt;reason&gt;</c>, <c>locale &lt;tag&gt;</c>.
/// </remarks>
public sealed class Scope
{
    public List<string> Roots { get; } = [];

    public Dictionary<string, string> OutOfScopeFeatures { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Locales { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Scope Load(string path)
    {
        var scope = new Scope();
        foreach (string raw in File.ReadLines(path))
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string kind = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (kind)
            {
                case "root":
                    scope.Roots.Add(rest);
                    break;
                case "locale":
                    scope.Locales.Add(rest);
                    break;
                case "feature-out":
                    int bar = rest.IndexOf('|', StringComparison.Ordinal);
                    string name = (bar < 0 ? rest : rest[..bar]).Trim();
                    scope.OutOfScopeFeatures[name] = bar < 0 ? "" : rest[(bar + 1)..].Trim();
                    break;
                default:
                    throw new InvalidDataException($"{path}: unknown entry '{line}'");
            }
        }

        return scope;
    }

    /// <summary>Returns the first listed feature that is out of scope, or null.</summary>
    public string? FindOutOfScopeFeature(IEnumerable<string> features) =>
        features.FirstOrDefault(OutOfScopeFeatures.ContainsKey);

    /// <summary>
    /// Returns the first locale tag in <paramref name="tags"/> that needs a locale outside the
    /// supported set, or null. A bare language (<c>en</c>) is supported when any supported locale
    /// has that language; a tag with a region or extensions must match a supported locale exactly.
    /// </summary>
    public string? FindUnsupportedLocale(IEnumerable<string> tags)
    {
        foreach (string tag in tags)
        {
            if (!IsSupported(tag))
            {
                return tag;
            }
        }

        return null;
    }

    private bool IsSupported(string tag)
    {
        // Unicode extension keywords (-u-...) do not change which base locale is needed.
        int ext = tag.IndexOf("-u-", StringComparison.OrdinalIgnoreCase);
        string baseTag = ext < 0 ? tag : tag[..ext];
        if (Locales.Contains(baseTag))
        {
            return true;
        }

        return !baseTag.Contains('-', StringComparison.Ordinal) &&
            Locales.Any(l => l.StartsWith(baseTag + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? line : line[..hash];
    }
}
