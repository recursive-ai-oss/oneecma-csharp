using RecursiveAI.OneEcma.ExecutorSpike;
using RecursiveAI.OneEcma.ExecutorSpike.Bytecode;
using RecursiveAI.OneEcma.ExecutorSpike.Closure;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>Both spike executors agree with each other and with C# on the benchmark sources.</summary>
public class ExecutorSpikeTests
{
    public static TheoryData<string> ScriptNames()
    {
        var d = new TheoryData<string>();
        foreach (var s in SpikeScripts.All)
        {
            d.Add(s.Name);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void ClosureExecutorComputesExpectedValue(string name)
    {
        var s = SpikeScripts.All.First(x => x.Name == name);
        Assert.Equal(s.Expected, ClosureEngine.Evaluate(s.Source));
    }

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void BytecodeExecutorComputesExpectedValue(string name)
    {
        var s = SpikeScripts.All.First(x => x.Name == name);
        Assert.Equal(s.Expected, BytecodeEngine.Evaluate(s.Source));
    }
}
