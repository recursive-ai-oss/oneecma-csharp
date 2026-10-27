namespace RecursiveAI.OneEcma.Generators.Tests;

/// <summary>What the generator accepts, what it writes, and every OE1xxx diagnostic.</summary>
public sealed class GeneratorTests
{
    private static GeneratorHarness.Result Run(string source) => GeneratorHarness.Run(source);

    private static void AssertCompiles(GeneratorHarness.Result r)
    {
        Assert.Empty(r.Diagnostics);
        Assert.True(r.CompileErrors.Count == 0, string.Join('\n', r.CompileErrors.Select(d => d.ToString())));
    }

    // ---- generated shapes compile --------------------------------------------------------------------

    [Fact]
    public void GeneratedCodeCompilesWithoutErrors()
    {
        GeneratorHarness.Result r = Run("""
            [JsExport] public enum Color { Red, Green = 5 }
            [JsExport] public record Pt(double X, double Y);
            [JsExport] public struct Sz { public int W { get; set; } public int H; }
            [JsExport] public class Thing
            {
                public Thing() { }
                public Thing(string id, int n = 1) { Id = id; }
                public string Id { get; } = "";
                public Color Tint { get; set; }
                public static int Count;
                public const string Kind = "thing";
                public int Add(int a, int b) => a + b;
                public string Add(string a, string b) => a + b;
                public int Sum(params int[] xs) => 0;
                public Task<Pt> Later(Pt p) => Task.FromResult(p);
                public Task Nothing() => Task.CompletedTask;
                public ValueTask<int> Quick() => new(1);
                public IEnumerable<Pt> Points() => new Pt[0];
                public async IAsyncEnumerable<int> Ticks() { await Task.Yield(); yield return 1; }
                public void Each(Action<Pt> f) { }
                public int Fold(Func<int, int, int> f, int[] xs, Dictionary<string, Pt> d, JsValue any, JsObject o) => 0;
                public Pt? Maybe(int? x, Color? c = null) => null;
                public long Big(ulong u, decimal m, char c, float f, System.Numerics.BigInteger b) => 0;
                public Sz Grow(Sz s) => s;
            }
            [JsExport] public static class Util { public static int Twice(int x) => x * 2; public static int Calls { get; set; } }
            """);
        AssertCompiles(r);
        Assert.Contains("JsExports.g.cs", r.Sources.Keys);
        Assert.Contains("Demo.ThingJsBinding.g.cs", r.Sources.Keys);
    }

    // ---- snapshots ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("static-class", "[JsExport] public static class Util { public static int Twice(int x) => x * 2; public static string Greet(string name, string greeting = \"Hello\") => greeting + name; }", "Demo.UtilJsBinding.g.cs")]
    [InlineData("value-record", "[JsExport] public record Pt(double X, double Y, string? Label = null);", "Demo.PtJsBinding.g.cs")]
    [InlineData("reference-class", "[JsExport] public class Counter { public int Value { get; set; } public Counter Bump() { Value++; return this; } public Task<int> LaterAsync(Func<int, int> f) => Task.FromResult(f(Value)); public int Add(int a, int b) => a + b; public string Add(string a, string b) => a + b; }", "Demo.CounterJsBinding.g.cs")]
    [InlineData("enum", "[JsExport] public enum Level { Low, High = 5 }", "Demo.LevelJsBinding.g.cs")]
    [InlineData("install-all", "[JsExport] public class A { } [JsExport] public class B { }", "JsExports.g.cs")]
    public void GeneratedSourceMatchesSnapshot(string name, string source, string hint)
    {
        GeneratorHarness.Result r = Run(source);
        AssertCompiles(r);
        string actual = r.Sources[hint];
        string path = Path.Combine(RepoPaths.Root, "tests", "OneEcma.Generators.Tests", "Snapshots", name + ".g.cs.txt");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, actual);
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        const string source = "[JsExport] public class Z { public int A(int x) => x; public int B(int x) => x; } [JsExport] public class Y { public int C { get; set; } }";
        Assert.Equal(Run(source).Sources.Select(kv => kv.Key + kv.Value), Run(source).Sources.Select(kv => kv.Key + kv.Value));
    }

    [Fact]
    public void NamesAreCamelCasedOrOverridden()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class N { public int GetOrder() => 1; public int URL() => 2; public int ID { get; set; } [JsExport(Name = \"renamed\")] public int Old() => 3; }");
        AssertCompiles(r);
        string src = r.Sources["Demo.NJsBinding.g.cs"];
        Assert.Contains("\"getOrder\"", src, StringComparison.Ordinal);
        Assert.Contains("\"url\"", src, StringComparison.Ordinal);
        Assert.Contains("\"id\"", src, StringComparison.Ordinal);
        Assert.Contains("\"renamed\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectMembersAndIgnoredMembersAreNotExported()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class N { public int A() => 1; [JsIgnore] public int Secret() => 2; public override string ToString() => \"x\"; }");
        AssertCompiles(r);
        string src = r.Sources["Demo.NJsBinding.g.cs"];
        Assert.DoesNotContain("secret", src, StringComparison.Ordinal);
        Assert.DoesNotContain("toString", src, StringComparison.Ordinal);
        Assert.DoesNotContain("equals", src, StringComparison.OrdinalIgnoreCase);
    }

    // ---- diagnostics: one test per id, each checks the fix text ---------------------------------------------

    [Fact]
    public void OE1001GenericType()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class Box<T> { }");
        Assert.Contains("generic types are not supported", r.Single("OE1001").GetMessage(), StringComparison.Ordinal);
        Assert.Contains("Fix:", r.Single("OE1001").GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void OE1001RefStruct()
    {
        Assert.Equal(["OE1001"], Run("[JsExport] public ref struct Span2 { }").Ids);
    }

    [Fact]
    public void OE1002ByRefParameterEventAndIndexerAndGenericMethod()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class M { public void A(ref int x) { } public event Action? E; public int this[int i] => i; public T G<T>(T t) => t; }");
        Assert.Equal(4, r.Ids.Count(i => i == "OE1002"));
        Assert.All(r.Diagnostics, d => Assert.Contains("[JsIgnore]", d.GetMessage(), StringComparison.Ordinal));
    }

    [Fact]
    public void OE1003UnsupportedTypes()
    {
        GeneratorHarness.Result r = Run("""
            public class Plain { }
            [JsExport] public class U
            {
                public object A(object o) => o;
                public Plain B() => new();
                public List<Plain> C() => new();
                public Dictionary<int, string> D() => new();
                public int[,] E() => new int[1, 1];
                public Func<int, Plain> F() => x => new();
                public Task<Task> G() => Task.FromResult(Task.CompletedTask);
                public Stream H() => null!;
            }
            """);
        Assert.True(r.Ids.Count(i => i == "OE1003") >= 7, string.Join(",", r.Ids));
        Assert.Contains(r.Diagnostics, d => d.GetMessage().Contains("add [JsExport] to Plain", StringComparison.Ordinal));
        Assert.Contains(r.Diagnostics, d => d.GetMessage().Contains("Dictionary<string, T>", StringComparison.Ordinal));
    }

    [Fact]
    public void OE1004AmbiguousOverloads()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class A { public int F(int x) => 1; public int F(long x) => 2; public int G(string s) => 1; public int G(char c) => 2; }");
        Assert.Equal(2, r.Ids.Count(i => i == "OE1004"));
        Assert.Contains("[JsExport(Name", r.Diagnostics.First(d => d.Id == "OE1004").GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void OE1004NotRaisedWhenKindsDiffer()
    {
        AssertCompiles(Run("[JsExport] public class A { public int F(int x) => 1; public int F(string x) => 2; public int F(int x, int y) => 3; public int F(int[] xs) => 4; public int F(Func<int> f) => 5; public int F(bool b) => 6; }"));
    }

    [Fact]
    public void OE1005NameCollision()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class C { public int Value { get; set; } public int value() => 1; }");
        Assert.Contains("OE1005", r.Ids);
        Assert.Equal(["OE1005"], Run("[JsExport] public class A1 { } [JsExport(Name = \"A1\")] public class A2 { }").Ids.Distinct());
    }

    [Fact]
    public void OE1006ValueTypeCannotBeBuilt()
    {
        GeneratorHarness.Result r = Run("[JsExport(Kind = JsExportKind.Value)] public class V { private V() { } public int X { get; set; } }");
        Assert.Contains("OE1006", r.Ids);
        Assert.Contains("parameterless constructor", r.Single("OE1006").GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void OE1007InvalidName()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class W { [JsExport(Name = \"not valid\")] public int A() => 1; [JsExport(Name = \"1st\")] public int B() => 1; }");
        Assert.Equal(2, r.Ids.Count(i => i == "OE1007"));
    }

    [Fact]
    public void OE1008KindConflicts()
    {
        GeneratorHarness.Result r = Run("[JsExport(Kind = JsExportKind.Reference)] public struct S { public int A { get; set; } } [JsExport(Kind = JsExportKind.Value)] public interface I { }");
        Assert.Equal(2, r.Ids.Count(i => i == "OE1008"));
    }

    [Fact]
    public void ErrorsDoNotProduceBindingsForThatType()
    {
        GeneratorHarness.Result r = Run("[JsExport] public class Bad { public object A() => 1; } [JsExport] public class Good { public int A() => 1; }");
        Assert.DoesNotContain("Demo.BadJsBinding.g.cs", r.Sources.Keys);
        Assert.Contains("Demo.GoodJsBinding.g.cs", r.Sources.Keys);
        Assert.Contains("Demo.Good", r.Sources["JsExports.g.cs"].Replace("JsBinding", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("Bad", r.Sources["JsExports.g.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDescriptorIsDocumentedInTheReleaseFile()
    {
        string notes = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "RecursiveAI.OneEcma.Generators", "AnalyzerReleases.Unshipped.md"));
        for (int i = 1; i <= 8; i++)
        {
            Assert.Contains("OE100" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), notes, StringComparison.Ordinal);
        }
    }
}
