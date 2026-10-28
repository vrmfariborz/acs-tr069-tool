using AcsTool.Testing;

namespace AcsTool.Simulator.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_Loads_HasName()
    {
        Assert.Equal("AcsTool.Simulator", AcsTool.Simulator.AssemblyMarker.Name);
    }

    [Fact]
    public void Fixtures_LoadByName_ReturnsBytes()
    {
        Assert.Empty(Fixtures.Load("empty.txt"));
    }
}
