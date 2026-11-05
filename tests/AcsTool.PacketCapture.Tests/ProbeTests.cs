using AcsTool.PacketCapture.Backend;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpPcap;

namespace AcsTool.PacketCapture.Tests;

public class ProbeTests
{
    [Fact]
    public void Probe_RealBackend_NeverThrows_ReturnsConsistentInfo()
    {
        // CI runners may or may not have libpcap/Npcap; the contract is "never throws" and a coherent result either way.
        var info = new SharpPcapBackend().Probe();

        Assert.True(Enum.IsDefined(info.Status));
        Assert.False(string.IsNullOrWhiteSpace(info.Message));
        if (info.Status == CaptureBackendStatus.Available)
        {
            Assert.True(info.DeviceCount >= 0);
        }
        else
        {
            Assert.Equal(0, info.DeviceCount);
            Assert.False(info.IsAvailable);
        }
    }

    [SkippableFact]
    public void Probe_RealBackend_NativeLibraryMissing_IsNotInstalled()
    {
        // The CI runners have no Npcap; this pins the real DllNotFoundException path end to end (design §4 spike result).
        Skip.If(NativePcapLoadable(), "Npcap/libpcap is installed here; Probe_RealBackend_NeverThrows_ReturnsConsistentInfo covers it.");

        var info = new SharpPcapBackend().Probe();

        Assert.Equal(CaptureBackendStatus.NotInstalled, info.Status);
        Assert.Contains(SharpPcapBackend.LibraryName, info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_DllNotFound_IsNotInstalled()
    {
        var info = Backend(() => throw new DllNotFoundException("Unable to load shared library 'wpcap'")).Probe();

        Assert.Equal(CaptureBackendStatus.NotInstalled, info.Status);
        Assert.Contains(SharpPcapBackend.LibraryName, info.Message, StringComparison.Ordinal);
        Assert.Null(info.Version);
        Assert.Equal(0, info.DeviceCount);
    }

    [Fact]
    public void Probe_DllNotFoundInsideTypeInitializer_IsNotInstalled()
    {
        var wrapped = new TypeInitializationException("SharpPcap.LibPcap.LibPcapSafeNativeMethods", new DllNotFoundException("wpcap"));

        var info = Backend(() => throw wrapped).Probe();

        Assert.Equal(CaptureBackendStatus.NotInstalled, info.Status);
    }

    [Fact]
    public void Probe_UnauthorizedAccess_IsAccessDenied()
    {
        var info = Backend(() => throw new UnauthorizedAccessException("Access is denied.")).Probe();

        Assert.Equal(CaptureBackendStatus.AccessDenied, info.Status);
        Assert.Contains("Access is denied.", info.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Error opening adapter: Access is denied.", CaptureBackendStatus.AccessDenied)]
    [InlineData("socket: Operation not permitted (permission)", CaptureBackendStatus.AccessDenied)]
    [InlineData("No interfaces found", CaptureBackendStatus.Error)]
    public void Probe_PcapException_MapsByMessage(string message, CaptureBackendStatus expected)
    {
        var info = Backend(() => throw NewPcapException(message)).Probe();

        Assert.Equal(expected, info.Status);
    }

    [Fact]
    public void Probe_OtherException_IsError_WithTypeName()
    {
        var info = Backend(() => throw new BadImageFormatException("wrong bitness")).Probe();

        Assert.Equal(CaptureBackendStatus.Error, info.Status);
        Assert.Contains(nameof(BadImageFormatException), info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_DevicesEnumerated_IsAvailable_WithVersionAndCount()
    {
        var info = new SharpPcapBackend(() => 3, () => "Npcap version 1.80, based on libpcap version 1.10.5").Probe();

        Assert.True(info.IsAvailable);
        Assert.Equal(3, info.DeviceCount);
        Assert.Equal("Npcap version 1.80, based on libpcap version 1.10.5", info.Version);
    }

    [Fact]
    public void Probe_VersionThrows_StillAvailable_WithoutVersion()
    {
        var info = new SharpPcapBackend(() => 1, () => throw new InvalidOperationException("boom")).Probe();

        Assert.Equal(CaptureBackendStatus.Available, info.Status);
        Assert.Null(info.Version);
    }

    [Fact]
    public void Construct_DoesNotTouchNativeLibrary()
    {
        var calls = 0;

        _ = new SharpPcapBackend(() => { calls++; return 0; }, () => { calls++; return null; });

        Assert.Equal(0, calls);
    }

    private static bool NativePcapLoadable()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"), "wpcap"]
            : ["libpcap.so.1", "libpcap.so", "libpcap.so.0.8", "libpcap.dylib", "wpcap"];
        foreach (var name in candidates)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    // PcapException only has internal constructors; SharpPcap throws it, so the tests build one the same way.
    private static PcapException NewPcapException(string message) =>
        (PcapException)Activator.CreateInstance(typeof(PcapException), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [message], null)!;

    private static SharpPcapBackend Backend(Func<int> countDevices) => new(countDevices, () => "unused");
}
