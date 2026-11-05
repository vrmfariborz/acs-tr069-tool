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

    // NOTE: no test in AcsTool.Presentation.Tests may call Probe() on the real backend (or otherwise touch SharpPcap types):
    // the two tests below check this process's loaded assemblies, so a probe anywhere in the assembly would make them flaky.

    [Fact]
    public async Task ShellBuild_ResolveAll_DoesNotLoadSharpPcap()
    {
        // Design §5.2/§5.4: capture is probed when first needed, never at startup. Not even the managed assembly may load.
        await using var app = AppHarness.Create();

        await app.Services.GetRequiredService<MainViewModel>().InitializeAsync();
        _ = app.Services.GetRequiredService<ICaptureBackend>();

        AssertNotLoaded("SharpPcap");
        AssertNotLoaded("PacketDotNet");
    }

    [Fact]
    public async Task SmokeExitPath_InitializeThenShutdown_DoesNotLoadSharpPcap()
    {
        // Mirrors App.OnStartup with --smoke-exit: resolve MainViewModel, InitializeAsync, then ShutdownAsync.
        await using var app = AppHarness.Create();
        var main = app.Services.GetRequiredService<MainViewModel>();

        await main.InitializeAsync();
        await main.ShutdownAsync();

        AssertNotLoaded("SharpPcap");
        AssertNotLoaded("PacketDotNet");
    }

    private static void AssertNotLoaded(string assemblyName) =>
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == assemblyName);
}
