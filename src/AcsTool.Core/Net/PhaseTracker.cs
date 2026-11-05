using System.Net;

namespace AcsTool.Core.Net;

/// <summary>
/// Tracks how far the current upstream exchange got. Written by the ConnectCallback / TLS validation callback,
/// read by the error classifier. Connection facts persist across <see cref="Reset"/> so a reused pooled
/// connection is reported as already connected; a new connection attempt calls <see cref="BeginConnect"/>.
/// <see cref="ConnectionSerial"/> counts connection attempts (PT0.3): pooled reuse keeps it, each new socket increments it.
/// </summary>
public sealed class PhaseTracker
{
    // Set by MarkConnected, cleared by Reset: a socket connected during the current exchange (PT0.4, review m2).
    private bool _connectedThisExchange;

    private readonly object _lock = new();
    private bool _dnsDone;
    private bool _connected;
    private bool _tlsDone;
    private bool _headers;
    private TimeSpan? _dns;
    private TimeSpan? _connect;
    private TimeSpan? _toHeaders;
    private IPEndPoint? _localEndPoint;
    private long _connectionSerial;

    private IPEndPoint? _remoteEndPoint;

    /// <summary>Remote endpoint of the current upstream socket, IPv4-mapped IPv6 normalised; null until connected.</summary>
    public IPEndPoint? RemoteEndPoint { get { lock (_lock) { return _remoteEndPoint; } } }

    /// <summary>Local (ephemeral) endpoint of the current upstream socket, IPv4-mapped IPv6 normalised; null until connected.</summary>
    public IPEndPoint? LocalEndPoint { get { lock (_lock) { return _localEndPoint; } } }

    /// <summary>Number of connection attempts so far (0 = none); the current socket's serial once connected.</summary>
    public long ConnectionSerial { get { lock (_lock) { return _connectionSerial; } } }

    public IReadOnlyList<IPAddress> ResolvedAddresses { get; private set; } = [];

    /// <summary>Per-exchange reset: clears timings and the response-headers flag.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _headers = false;
            _connectedThisExchange = false;
            _dns = null;
            _connect = null;
            _toHeaders = null;
        }
    }

    /// <summary>A new connection is being established.</summary>
    public void BeginConnect()
    {
        lock (_lock)
        {
            _dnsDone = false;
            _connected = false;
            _tlsDone = false;
            _localEndPoint = null;
            _remoteEndPoint = null; // review m4: a failed reconnect must not report the previous socket's remote
            _connectionSerial++;
        }
    }

    public void MarkDnsDone(TimeSpan d, IPAddress[] addrs)
    {
        lock (_lock)
        {
            _dnsDone = true;
            _dns = d;
            ResolvedAddresses = addrs ?? [];
        }
    }

    public void MarkConnected(TimeSpan d, IPEndPoint? ep, IPEndPoint? local = null)
    {
        lock (_lock)
        {
            _dnsDone = true;
            _connected = true;
            _connect = d;
            _remoteEndPoint = EndPointUtil.Normalize(ep);
            _connectedThisExchange = true;
            _localEndPoint = EndPointUtil.Normalize(local);
        }
    }

    public void MarkTlsDone()
    {
        lock (_lock)
        {
            _tlsDone = true;
        }
    }

    public void MarkHeaders(TimeSpan d)
    {
        lock (_lock)
        {
            _dnsDone = true;
            _connected = true;
            _tlsDone = true;
            _headers = true;
            _toHeaders = d;
        }
    }

    /// <summary>
    /// Reads the upstream socket facts under one lock (PT0.4, review m3), so a background connect cannot produce a mixed tuple.
    /// Producers of <see cref="Sessions.ExchangeEndpoints"/> must use only this.
    /// </summary>
    public UpstreamSnapshot SnapshotUpstream()
    {
        lock (_lock)
        {
            return new UpstreamSnapshot(_localEndPoint, _remoteEndPoint, _connectionSerial, _connected,
                _connected && (_connectedThisExchange || _headers));
        }
    }

    public TimeSpan? DnsDuration { get { lock (_lock) { return _dns; } } }

    public TimeSpan? ConnectDuration { get { lock (_lock) { return _connect; } } }

    public TimeSpan? ToHeadersDuration { get { lock (_lock) { return _toHeaders; } } }

    public RequestPhase CurrentPhase(bool isHttps)
    {
        lock (_lock)
        {
            if (!_dnsDone)
            {
                return RequestPhase.Dns;
            }
            if (!_connected)
            {
                return RequestPhase.Connect;
            }
            if (isHttps && !_tlsDone)
            {
                return RequestPhase.Tls;
            }
            return _headers ? RequestPhase.ReadingBody : RequestPhase.AwaitingResponse;
        }
    }
}

/// <summary>Atomic view of a <see cref="PhaseTracker"/>'s upstream socket (PT0.4).</summary>
/// <param name="Local">Local endpoint of the last connected socket; null after a new attempt started.</param>
/// <param name="Remote">Remote endpoint of the last connected socket; null after a new attempt started.</param>
/// <param name="Serial">Connection-attempt serial (see <see cref="PhaseTracker.ConnectionSerial"/>).</param>
/// <param name="Connected">The last attempt connected.</param>
/// <param name="CarriedCurrentExchange">
/// True only when the current exchange (since the last <see cref="PhaseTracker.Reset"/>) is known to have used this socket: a
/// socket connected during it, or it received response headers. False when it failed before connecting or while waiting for a
/// pooled connection; also false when it timed out on a reused pooled socket before headers (conservative, review m2).
/// </param>
public readonly record struct UpstreamSnapshot(IPEndPoint? Local, IPEndPoint? Remote, long Serial, bool Connected, bool CarriedCurrentExchange);
