using AcsTool.Testing;

namespace AcsTool.Presentation.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_Loads_HasName()
    {
        Assert.Equal("AcsTool.Presentation", AcsTool.Presentation.AssemblyMarker.Name);
    }

    [Fact]
    public void Fixtures_LoadByName_ReturnsBytes()
    {
        Assert.Empty(Fixtures.Load("empty.txt"));
    }
}
