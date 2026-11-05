using System.Reflection;
using SharpPcap;

namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// SharpPcap-based backend. Constructing it touches no native code; the native <c>wpcap</c>/<c>libpcap</c> is loaded by
/// SharpPcap on the first <see cref="Probe"/> (never at app start, so <c>--smoke-exit</c> is unaffected; design §7).
/// </summary>
public sealed class SharpPcapBackend : ICaptureBackend
{
    private readonly Func<int> _countDevices;
    private readonly Func<string?> _version;

    public SharpPcapBackend()
        : this(CountLiveDevices, () => Pcap.Version)
    {
    }

    /// <summary>Test seam: replaces the native calls.</summary>
    internal SharpPcapBackend(Func<int> countDevices, Func<string?> version)
    {
        _countDevices = countDevices;
        _version = version;
    }

    internal static string LibraryName => OperatingSystem.IsWindows() ? "Npcap" : "libpcap";

    public CaptureBackendInfo Probe()
    {
        try
        {
            var count = _countDevices();
            var version = SafeVersion();
            return new CaptureBackendInfo(CaptureBackendStatus.Available, version,
                $"{LibraryName} is available ({count} capture device(s)).", count);
        }
#pragma warning disable CA1031 // Probe must never throw: every failure becomes a status (design §5.2).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Classify(ex);
        }
    }

    internal static CaptureBackendInfo Classify(Exception ex)
    {
        var root = Unwrap(ex);
        if (root is DllNotFoundException)
        {
            var hint = OperatingSystem.IsWindows()
                ? "Npcap is not installed. Live capture is disabled; you can still open .pcap/.pcapng files. Get Npcap from https://npcap.com."
                : "libpcap is not installed. Live capture is disabled; you can still open .pcap/.pcapng files.";
            return new CaptureBackendInfo(CaptureBackendStatus.NotInstalled, null, hint, 0);
        }

        if (root is UnauthorizedAccessException || (root is PcapException && MentionsAccess(root.Message)))
        {
            return new CaptureBackendInfo(CaptureBackendStatus.AccessDenied, null,
                $"{LibraryName} is installed but this user may not capture packets: {root.Message}", 0);
        }

        return new CaptureBackendInfo(CaptureBackendStatus.Error, null,
            $"{LibraryName} could not be used ({root.GetType().Name}): {root.Message}", 0);
    }

    private string? SafeVersion()
    {
        try
        {
            return _version();
        }
#pragma warning disable CA1031 // The version string is informational only.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static int CountLiveDevices()
    {
        // New() instead of Instance: a fresh enumeration each probe, so installing Npcap while the app runs is picked up,
        // and a failed first load is not cached inside a static.
        return CaptureDeviceList.New().Count;
    }

    private static Exception Unwrap(Exception ex)
    {
        var current = ex;
        while (current is TypeInitializationException or TargetInvocationException or AggregateException
               && current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static bool MentionsAccess(string message) =>
        message.Contains("access", StringComparison.OrdinalIgnoreCase)
        || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
        || message.Contains("privilege", StringComparison.OrdinalIgnoreCase);
}
