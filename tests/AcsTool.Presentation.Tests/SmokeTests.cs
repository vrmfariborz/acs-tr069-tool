using AcsTool.PacketCapture.Backend;
using AcsTool.Presentation.Shell;
using AcsTool.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task AddAcsToolPresentation_RegistersPacketCaptureBackend_WithoutProbing()
    {
        // PT0.2: the backend is registered via AddAcsToolPacketCapture; building the shell must not need the native library.
        await using var app = AppHarness.Create();

        Assert.IsType<SharpPcapBackend>(app.Services.GetRequiredService<ICaptureBackend>());
        Assert.NotNull(app.Services.GetRequiredService<MainViewModel>());
    }
}
