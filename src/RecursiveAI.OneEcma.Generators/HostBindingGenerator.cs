using Microsoft.CodeAnalysis;

namespace RecursiveAI.OneEcma.Generators;

/// <summary>
/// Placeholder incremental generator. It registers no outputs yet; host-binding generation
/// for exported .NET types is added on top of this entry point.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HostBindingGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
    }
}
