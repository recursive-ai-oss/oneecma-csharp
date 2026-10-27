namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// Runs the <c>host-*</c> cases of the error golden corpus: scripts that call the sample order service
/// (<c>samples/OrderHost/Domain.cs</c>, exported with <c>[JsExport]</c>) and fail at the boundary. The report text must equal
/// <c>expected.txt</c> byte for byte. After a deliberate change, regenerate with <c>ONEECMA_UPDATE_GOLDEN=1</c> and review the diff.
/// </summary>
public sealed class HostGoldenTests
{
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden");
        foreach (string d in Directory.GetDirectories(dir, "host-*").Order(StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(Path.GetFileName(d));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ReportMatchesExpectedText(string id)
    {
        string dir = Path.Combine(RepoPaths.Root, "testdata", "golden", id);
        var fixture = new HostFixture();
        JsException? e = await fixture.RunAsync(File.ReadAllText(Path.Combine(dir, "case.js")));
        Assert.NotNull(e);
        string actual = HostFixture.Render(e);
        string path = Path.Combine(dir, "expected.txt");
        if (Environment.GetEnvironmentVariable("ONEECMA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }

        Assert.Equal(File.ReadAllText(path), actual);
    }

    [Fact]
    public void CasesCoverTheirStatedCount()
    {
        Assert.True(Cases().Count() >= 6);
    }
}
