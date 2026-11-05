using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// SharpPcap-based backend. Constructing it touches no native code; the native <c>wpcap</c>/<c>libpcap</c> is loaded by
/// SharpPcap on the first <see cref="Probe"/> / <see cref="ListDevices"/> / <see cref="Open"/> (never at app start, so
/// <c>--smoke-exit</c> is unaffected; design §5.2/§5.4).
/// </summary>
public sealed class SharpPcapBackend : ICaptureBackend
{
    // libpcap PCAP_IF_* flags (also used by Npcap).
    private const uint PcapIfLoopback = 0x0000_0001;
    private const uint PcapIfUp = 0x0000_0002;

    private readonly Func<int> _countDevices;
    private readonly Func<string?> _version;

    public SharpPcapBackend()
        : this(CountLiveDevices, () => Pcap.Version)
    {
    }

    /// <summary>Test seam: replaces the native calls used by <see cref="Probe"/>.</summary>
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
            var message = count == 0
                ? $"{LibraryName} is installed but no capture devices were found. The driver may need repairing (see troubleshooting)."
                : $"{LibraryName} is available ({count} capture device(s)).";
            return new CaptureBackendInfo(CaptureBackendStatus.Available, version, message, count);
        }
#pragma warning disable CA1031 // Probe must never throw: every failure becomes a status (design §5.2).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Classify(ex);
        }
    }

    public IReadOnlyList<CaptureDeviceInfo> ListDevices()
    {
        try
        {
            var list = LibPcapLiveDeviceList.New();
            var result = new List<CaptureDeviceInfo>(list.Count);
            foreach (var d in list)
                result.Add(ToInfo(d));
            return result;
        }
        catch (Exception ex) when (ex is not CaptureOpenException)
        {
            var info = Classify(ex);
            throw new CaptureOpenException(info.Message, info.Status, Unwrap(ex));
        }
    }

    public ICaptureSession Open(CaptureDeviceInfo device, CaptureOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        LibPcapLiveDevice? live = null;
        try
        {
            live = FindDevice(device.Name) ?? throw new CaptureOpenException(
                $"Capture device '{device.FriendlyName}' ({device.Name}) was not found. It may have been removed.",
                CaptureBackendStatus.Error);

            var config = new DeviceConfiguration
            {
                Snaplen = options.Snaplen,
                Mode = options.Promiscuous ? DeviceModes.Promiscuous : DeviceModes.None,
                ReadTimeout = options.ReadTimeoutMilliseconds,
                KernelBufferSize = options.KernelBufferBytes > 0 ? options.KernelBufferBytes : null,
                TimestampResolution = options.PreferNanosecondTimestamps
                    ? TimestampResolution.Nanosecond
                    : TimestampResolution.Microsecond,
            };
            // Some drivers reject KernelBufferSize / ns timestamps; ConfigurationFailed is informational — Open still proceeds
            // with defaults (spike PT0.1). We ignore the event and rely on Open throwing when the open itself fails.
            live.Open(config);

            if (!string.IsNullOrWhiteSpace(options.BpfFilter))
            {
                try
                {
                    live.Filter = options.BpfFilter;
                }
                catch (Exception ex)
                {
                    live.Close();
                    live.Dispose();
                    live = null;
                    throw new CaptureOpenException(
                        $"The capture filter is not valid: {Unwrap(ex).Message}",
                        CaptureBackendStatus.Error,
                        Unwrap(ex));
                }
            }

            // Link type is only meaningful after Open.
            var link = LinkKindMapping.FromPacketDotNet(live.LinkType);
            var info = ToInfo(live);
            var session = new SharpPcapCaptureSession(live, info, options.InterfaceId, link);
            live = null; // session owns it
            return session;
        }
        catch (CaptureOpenException)
        {
            live?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            live?.Dispose();
            var classified = Classify(ex);
            throw new CaptureOpenException(classified.Message, classified.Status, Unwrap(ex));
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
        // LibPcapLiveDeviceList.New(): exactly one fresh pcap_findalldevs per probe, so installing Npcap while the app runs is
        // picked up, and neither a failure nor a device list is cached in a static (CaptureDeviceList.New() also fills
        // LibPcapLiveDeviceList.Instance and enumerates several times; review n3).
        return LibPcapLiveDeviceList.New().Count;
    }

    private static LibPcapLiveDevice? FindDevice(string name)
    {
        foreach (var d in LibPcapLiveDeviceList.New())
        {
            if (string.Equals(d.Name, name, StringComparison.Ordinal))
                return d;
        }

        return null;
    }

    internal static CaptureDeviceInfo ToInfo(LibPcapLiveDevice device)
    {
        var iface = device.Interface;
        var name = device.Name ?? iface?.Name ?? "";
        var friendly = !string.IsNullOrWhiteSpace(iface?.FriendlyName)
            ? iface!.FriendlyName!
            : (!string.IsNullOrWhiteSpace(device.Description) ? device.Description! : name);
        var description = iface?.Description ?? device.Description;
        var addresses = CollectAddresses(device);
        var flags = iface?.Flags ?? device.Flags;
        var isLoopback = device.Loopback || (flags & PcapIfLoopback) != 0 || LooksLikeLoopback(name, friendly);
        // When the driver does not set UP, treat the device as up so auto-select still finds it (safer than silently skipping).
        var isUp = (flags & PcapIfUp) != 0 || flags == 0;
        string? mac = null;
        try
        {
            var pa = device.MacAddress ?? iface?.MacAddress;
            if (pa is not null && pa.GetAddressBytes().Length > 0)
                mac = FormatMac(pa);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return new CaptureDeviceInfo(name, friendly, description, addresses, isLoopback, isUp, mac);
    }

    private static List<IPAddress> CollectAddresses(LibPcapLiveDevice device)
    {
        var list = new List<IPAddress>();
        try
        {
            IEnumerable<PcapAddress>? source = device.Addresses;
            if (source is null || !source.Any())
                source = device.Interface?.Addresses;
            if (source is null)
                return list;
            foreach (var addr in source)
            {
                var ip = addr.Addr?.ipAddress;
                if (ip is not null && !list.Contains(ip))
                    list.Add(ip);
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return list;
    }

    private static bool LooksLikeLoopback(string name, string friendly) =>
        name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        || name.Equals("lo", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("lo:", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("/lo", StringComparison.OrdinalIgnoreCase)
        || friendly.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        || string.Equals(friendly, "lo", StringComparison.OrdinalIgnoreCase);

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return string.Join(':', bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
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
