using System.Net;
using AcsTool.Core.Settings;

namespace AcsTool.PacketCapture.Backend;

/// <summary>
/// Picks the capture devices for the Proxy path (D10): the NIC(s) that own the proxy bind address
/// (all up non-loopback NICs when binding <c>0.0.0.0</c>/<c>::</c>; the loopback device(s) when binding <c>127.0.0.1</c>/<c>::1</c>),
/// plus Loopback when the upstream or a simulator target is local.
/// </summary>
public static class DeviceSelector
{
    /// <summary>Auto-select devices for live capture. Never throws; returns an empty list when nothing matches.</summary>
    public static IReadOnlyList<CaptureDeviceInfo> Auto(
        IReadOnlyList<CaptureDeviceInfo> devices,
        ProxySettings proxy,
        IEnumerable<string?>? simulatorTargets = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(proxy);

        var selected = new List<CaptureDeviceInfo>();
        var bind = ParseBind(proxy.BindAddress);

        if (bind is null || IsAny(bind))
        {
            foreach (var d in devices)
            {
                if (!d.IsLoopback && d.IsUp)
                    selected.Add(d);
            }
        }
        else
        {
            // A loopback bind (127.0.0.1, ::1) is owned by the loopback device(s); Npcap's loopback adapter lists no addresses,
            // so every loopback device counts (review m2).
            var loopbackBind = IsLoopbackAddress(bind);
            foreach (var d in devices)
            {
                if (DeviceOwnsAddress(d, bind) || (loopbackBind && d.IsLoopback))
                    selected.Add(d);
            }
        }

        if (NeedsLoopback(proxy.UpstreamUrl, simulatorTargets, devices))
        {
            foreach (var d in devices)
            {
                if (d.IsLoopback && !selected.Contains(d))
                    selected.Add(d);
            }
        }

        return selected;
    }

    internal static bool NeedsLoopback(string? upstreamUrl, IEnumerable<string?>? simulatorTargets, IReadOnlyList<CaptureDeviceInfo> devices)
    {
        if (IsLocalTarget(upstreamUrl, devices))
            return true;
        if (simulatorTargets is null)
            return false;
        foreach (var t in simulatorTargets)
        {
            if (IsLocalTarget(t, devices))
                return true;
        }

        return false;
    }

    internal static bool IsLocalTarget(string? urlOrHost, IReadOnlyList<CaptureDeviceInfo> devices)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
            return false;

        if (!TryGetHost(urlOrHost, out var host) || string.IsNullOrWhiteSpace(host))
            return false;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IsLoopbackAddress(ip))
                return true;
            // A host that is one of this machine's NIC addresses is local (self-connection → Loopback on Windows).
            foreach (var d in devices)
            {
                if (DeviceOwnsAddress(d, ip))
                    return true;
            }
        }

        return false;
    }

    private static bool DeviceOwnsAddress(CaptureDeviceInfo device, IPAddress address)
    {
        foreach (var a in device.Addresses)
        {
            if (a.Equals(address))
                return true;
            // IPv4-mapped IPv6 ↔ IPv4
            if (a.IsIPv4MappedToIPv6 && a.MapToIPv4().Equals(address))
                return true;
            if (address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(a))
                return true;
        }

        return false;
    }

    private static bool IsLoopbackAddress(IPAddress ip) =>
        IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));

    private static IPAddress? ParseBind(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
            return IPAddress.Any;
        return IPAddress.TryParse(bindAddress.Trim(), out var ip) ? ip : IPAddress.Any;
    }

    private static bool IsAny(IPAddress ip) =>
        ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);

    private static bool TryGetHost(string urlOrHost, out string host)
    {
        // Absolute URI first.
        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.IdnHost;
            return true;
        }

        // host:port or bare host (not a path).
        if (urlOrHost.Contains("://", StringComparison.Ordinal))
        {
            host = "";
            return false;
        }

        // Bracketed IPv6 with optional port: [fe80::1]:80
        if (urlOrHost.StartsWith('[') && urlOrHost.Contains(']', StringComparison.Ordinal))
        {
            var end = urlOrHost.IndexOf(']');
            host = urlOrHost[1..end];
            return true;
        }

        // host:port — only split on the last colon when it looks like IPv4/hostname (not raw IPv6).
        var colon = urlOrHost.LastIndexOf(':');
        if (colon > 0 && urlOrHost.AsSpan(colon + 1).IndexOfAny("0123456789".AsSpan()) == 0
            && urlOrHost.Count(c => c == ':') == 1)
        {
            host = urlOrHost[..colon];
            return true;
        }

        host = urlOrHost;
        return true;
    }
}
