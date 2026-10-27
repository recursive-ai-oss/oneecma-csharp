using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>
/// A host whose modules are strings you add. A specifier that starts with <c>./</c> or <c>../</c> is resolved against the importing
/// module's id, treated as a slash-separated path; any other specifier is looked up as written. It does not touch the file system.
/// </summary>
public class InMemoryModuleHost : DefaultHost
{
    private readonly Dictionary<string, string> _modules = new(StringComparer.Ordinal);

    /// <summary>Creates a host. A seed makes Math.random reproducible.</summary>
    /// <param name="seed">The seed, or <see langword="null"/> for a random one.</param>
    public InMemoryModuleHost(int? seed = null)
        : base(seed)
    {
    }

    /// <summary>Adds (or replaces) a module.</summary>
    /// <param name="id">The module id, such as <c>lib/util.js</c>; it is also the file name in reports.</param>
    /// <param name="source">The module source.</param>
    /// <returns>This host, for chaining.</returns>
    public InMemoryModuleHost Add(string id, string source)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(source);
        _modules[id] = source;
        return this;
    }

    /// <inheritdoc/>
    public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
    {
        string id = specifier;
        if (referrerId is not null && (specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal)))
        {
            var parts = new List<string>(referrerId.Split('/'));
            parts.RemoveAt(parts.Count - 1);
            foreach (string seg in specifier.Split('/'))
            {
                if (seg == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                }
                else if (seg != "." && seg.Length > 0)
                {
                    parts.Add(seg);
                }
            }

            id = new StringBuilder().AppendJoin('/', parts).ToString();
        }

        return _modules.TryGetValue(id, out string? source) ? new HostModule(id, source) : null;
    }
}
