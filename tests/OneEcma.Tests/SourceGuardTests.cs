using System.Text.RegularExpressions;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// The engine must stay free of ambient authority, nondeterminism and unsafe code. Host access
/// (clock, randomness) is allowed in exactly one file, <c>HostDefaults.cs</c>.
/// </summary>
public class SourceGuardTests
{
    private const string AllowedFile = "HostDefaults.cs";

    private static readonly (string Name, Regex Pattern)[] Forbidden =
    [
        ("unsafe", new Regex(@"\bunsafe\b")),
        ("Unsafe.", new Regex(@"\bUnsafe\.")),
        ("MemoryMarshal", new Regex(@"MemoryMarshal")),
        ("Reflection.Emit", new Regex(@"Reflection\.Emit")),
        ("DynamicMethod", new Regex(@"DynamicMethod")),
        ("HttpClient", new Regex(@"HttpClient")),
        ("System.Net", new Regex(@"System\.Net")),
        ("Process", new Regex(@"\bProcess\.|Diagnostics\.Process")),
        ("System.IO.File", new Regex(@"System\.IO\.File|\bFile\.")),
        ("Environment.GetEnvironmentVariable", new Regex(@"Environment\.GetEnvironmentVariable")),
        ("DateTime.Now", new Regex(@"DateTime\.(Utc)?Now")),
        ("new Random", new Regex(@"new\s+Random\b")),
        ("Stopwatch", new Regex(@"Stopwatch")),
    ];

    [Fact]
    public void EngineSourceHasNoForbiddenApis()
    {
        string dir = Path.Combine(RepoPaths.Root, "src", "RecursiveAI.OneEcma");
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == AllowedFile || file.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var (name, pattern) in Forbidden)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        hits.Add($"{Path.GetRelativePath(dir, file)}:{i + 1}: {name}");
                    }
                }
            }
        }

        Assert.Empty(hits);
    }

    [Fact]
    public void EngineHasNoPackageReferences()
    {
        string proj = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "src", "RecursiveAI.OneEcma", "RecursiveAI.OneEcma.csproj"));
        Assert.DoesNotContain("PackageReference", proj, StringComparison.Ordinal);
    }
}
