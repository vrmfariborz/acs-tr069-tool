using AcsTool.Testing;

namespace AcsTool.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_Loads_HasName()
    {
        Assert.Equal("AcsTool.Core", AcsTool.Core.AssemblyMarker.Name);
    }

    [Fact]
    public void Fixtures_LoadByName_ReturnsBytes()
    {
        Assert.Empty(Fixtures.Load("empty.txt"));
    }
}
