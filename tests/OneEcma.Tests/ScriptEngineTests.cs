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
        Assert.Throws<ArgumentNullException>(() => new ScriptEngine().Check(null!, TestContext.Current.CancellationToken));
    }
}
