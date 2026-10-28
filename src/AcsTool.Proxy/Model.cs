using AcsTool.Core.Net;
using AcsTool.Core.Sessions;

namespace AcsTool.Proxy;

public sealed record IncomingRequest(string Method, string PathAndQuery, IReadOnlyList<KeyValuePair<string, string>> Headers,
    ReadOnlyMemory<byte> Body, string RemoteIp, int RemotePort, string ConnectionId, bool IsHttps)
{
    public string? Header(string name) => HeaderUtil.Get(Headers, name);

    public string ClientEndpoint => RemoteIp.Contains(':', StringComparison.Ordinal) ? $"[{RemoteIp}]:{RemotePort}" : $"{RemoteIp}:{RemotePort}";
}

public sealed record ForwardResult(Uri UpstreamUri, int? StatusCode, IReadOnlyList<KeyValuePair<string, string>> Headers,
    ReadOnlyMemory<byte> Body, UpstreamError? Error, ExchangeTimings Timings)
{
    /// <summary>True when the CPE should get a 502/504 instead of a relayed response.</summary>
    public bool IsFailure => StatusCode is null || Error is { Kind: not UpstreamErrorKind.HttpStatus };
}

public enum ProxyState { Stopped, Starting, Running, Stopping, Faulted }

public enum ProxyBindFailure { PortInUse, AccessDenied, AddressNotAvailable, Other }

public sealed class ProxyBindException : Exception
{
    public ProxyBindException()
    {
    }

    public ProxyBindException(string message) : base(message)
    {
    }

    public ProxyBindException(string message, Exception inner) : base(message, inner)
    {
    }

    public ProxyBindException(ProxyBindFailure reason, string address, int port, Exception? inner)
        : base(Describe(reason, address, port), inner)
    {
        Reason = reason;
        Address = address;
        Port = port;
    }

    public ProxyBindFailure Reason { get; } = ProxyBindFailure.Other;
    public string? Address { get; }
    public int Port { get; }

    public static string Describe(ProxyBindFailure reason, string address, int port) => reason switch
    {
        ProxyBindFailure.PortInUse => $"Port {port} is already in use (another ACS/CPE tool?) — choose another port",
        ProxyBindFailure.AccessDenied => $"Access denied binding {address}:{port} — choose another port or check security software",
        ProxyBindFailure.AddressNotAvailable => $"Address {address} is not available on this PC — choose 0.0.0.0 or a local IP",
        _ => $"Could not listen on {address}:{port}",
    };
}
