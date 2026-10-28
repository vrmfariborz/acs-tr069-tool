using AcsTool.Testing;

namespace AcsTool.Proxy.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_Loads_HasName()
    {
        Assert.Equal("AcsTool.Proxy", AcsTool.Proxy.AssemblyMarker.Name);
    }

    [Fact]
    public void Fixtures_LoadByName_ReturnsBytes()
    {
        Assert.Empty(Fixtures.Load("empty.txt"));
    }
}
