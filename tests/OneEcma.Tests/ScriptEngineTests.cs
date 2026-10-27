namespace RecursiveAI.OneEcma.Tests;

public class ScriptEngineTests
{
    [Fact]
    public void PlaceholderEngineSaysItIsNotImplemented()
    {
        Assert.Throws<NotSupportedException>(() => new ScriptEngine().Evaluate("1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NullSourceIsAnArgumentError()
    {
        Assert.Throws<ArgumentNullException>(() => ScriptEngine.Check(null!, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CheckReturnsNothingForValidSourceAndDiagnosticsForInvalid()
    {
        var engine = new ScriptEngine();
        Assert.Empty(engine.Check("let a = 1;", null, TestContext.Current.CancellationToken));
        Assert.NotEmpty(engine.Check("let a = ;", null, TestContext.Current.CancellationToken));
    }
}
