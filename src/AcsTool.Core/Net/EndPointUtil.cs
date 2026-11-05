using System.Net;

namespace AcsTool.Core.Net;

/// <summary>Endpoint helpers shared by the session model, the upstream tracker and the packet correlator (PT0.3).</summary>
public static class EndPointUtil
{
    /// <summary>Maps an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) to plain IPv4, like <c>ProxyRequestHandler.Snapshot</c>.</summary>
    public static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>Same as <see cref="Normalize(IPAddress?)"/> for an endpoint; the port is kept. Returns a new instance when mapped.</summary>
    public static IPEndPoint? Normalize(IPEndPoint? endPoint) =>
        endPoint is { Address.IsIPv4MappedToIPv6: true } ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;
}
