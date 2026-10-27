using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RecursiveAI.OneEcma.Generators.Tests;

/// <summary>Runs the host-binding generator over a source string with the Roslyn compiler API.</summary>
internal static class GeneratorHarness
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var list = new List<MetadataReference>();
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        foreach (string p in trusted)
        {
            string name = Path.GetFileName(p);
            if (name.StartsWith("System.", StringComparison.Ordinal) || name is "mscorlib.dll" or "netstandard.dll")
            {
                list.Add(MetadataReference.CreateFromFile(p));
            }
        }

        list.Add(MetadataReference.CreateFromFile(typeof(JsValue).Assembly.Location));
        return [.. list];
    }

    internal sealed record Result(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, string> Sources, IReadOnlyList<Diagnostic> CompileErrors)
    {
        internal IEnumerable<string> Ids => Diagnostics.Select(d => d.Id);

        internal Diagnostic Single(string id) => Diagnostics.Single(d => d.Id == id);
    }

    internal static Result Run(string source, string rootNamespace = "Demo")
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Demo",
            [CSharpSyntaxTree.ParseText("#nullable enable\nusing System;\nusing System.Collections.Generic;\nusing System.Threading.Tasks;\nusing RecursiveAI.OneEcma;\nusing System.IO;\nnamespace Demo {\n" + source + "\n}\n", new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new HostBindingGenerator().AsSourceGenerator()],
            optionsProvider: new TestOptions(rootNamespace));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out System.Collections.Immutable.ImmutableArray<Diagnostic> diags);
        GeneratorDriverRunResult run = driver.GetRunResult();
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (GeneratedSourceResult s in run.Results.SelectMany(r => r.GeneratedSources))
        {
            sources[s.HintName] = s.SourceText.ToString().TrimStart('\uFEFF').ReplaceLineEndings("\n");
        }

        Diagnostic[] compileErrors = [.. output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        return new Result([.. run.Diagnostics], sources, compileErrors);
    }
}
