using System.Reflection;

namespace RecursiveAI.OneEcma.Generators.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = typeof(RepoPaths).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(a => a.Key == "RepoRoot").Value!;
}
