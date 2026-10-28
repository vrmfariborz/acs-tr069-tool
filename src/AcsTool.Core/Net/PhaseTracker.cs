using System.Net;

namespace AcsTool.Core.Net;

/// <summary>
/// Tracks how far the current upstream exchange got. Written by the ConnectCallback / TLS validation callback,
/// read by the error classifier. Connection facts persist across <see cref="Reset"/> so a reused pooled
/// connection is reported as already connected; a new connection attempt calls <see cref="BeginConnect"/>.
/// </summary>
public sealed class PhaseTracker
{
    private readonly object _lock = new();
    private bool _dnsDone;
    private bool _connected;
    private bool _tlsDone;
    private bool _headers;
    private TimeSpan? _dns;
    private TimeSpan? _connect;
    private TimeSpan? _toHeaders;

    public IPEndPoint? RemoteEndPoint { get; private set; }

    public IReadOnlyList<IPAddress> ResolvedAddresses { get; private set; } = [];

    /// <summary>Per-exchange reset: clears timings and the response-headers flag.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _headers = false;
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

    public void MarkConnected(TimeSpan d, IPEndPoint? ep)
    {
        lock (_lock)
        {
            _dnsDone = true;
            _connected = true;
            _connect = d;
            RemoteEndPoint = ep;
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
