using System.Net;
using System.Net.Http;
using AcsTool.Core.Model;
using AcsTool.Core.Net;

namespace AcsTool.Proxy;

/// <summary>One proxied CWMP session with its own upstream handler (D6) and an exchange gate (§3.8 rule 2).</summary>
public sealed class ProxySession : IAsyncDisposable, IDisposable
{
    private int _disposed;

    internal ProxySession(Guid sessionId, string clientIp, string? deviceKey, PhaseTracker tracker, SocketsHttpHandler handler,
        CookieContainer? cookies, DateTimeOffset now, bool isOrphan, bool oneShot)
    {
        SessionId = sessionId;
        ClientIp = clientIp;
        DeviceKey = deviceKey;
        Tracker = tracker;
        Upstream = new HttpMessageInvoker(handler, disposeHandler: true);
        Cookies = cookies;
        LastActivity = now;
        IsOrphan = isOrphan;
        IsOneShot = oneShot;
    }

    public Guid SessionId { get; }
    public string ClientIp { get; }
    public string? DeviceKey { get; set; }

    /// <summary>Serializes exchanges of this session. Not disposed (no wait handle is ever allocated) so a late Release is safe.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public HttpMessageInvoker Upstream { get; }
    public PhaseTracker Tracker { get; }
    public CookieContainer? Cookies { get; }
    public bool LastRequestWasEmpty { get; set; }
    public int? LastStatus { get; set; }
    public CwmpMessageKind LastRequestKind { get; set; } = CwmpMessageKind.Empty;
    /// <summary>The CPE has sent an empty POST ("no more requests") and has not issued a new CPE request since.</summary>
    public bool CpeSentEmpty { get; set; }
    /// <summary>
    /// Both sides signalled "no more requests" but the CPE answered last (ACS sent empty to a CPE response after the CPE's empty POST).
    /// Some CPEs close here, others send one more empty POST; the session is completed by that POST or by the sweeper after
    /// <see cref="CpeSessionTracker.CompletionGrace"/>.
    /// </summary>
    public bool AwaitingFinalEmpty { get; set; }
    public bool SawAcsFault { get; set; }
    public bool SawCpeFault { get; set; }
    public DateTimeOffset LastActivity { get; set; }
    public bool IsOrphan { get; }

    /// <summary>Non-CWMP orphan: ends after its single exchange.</summary>
    public bool IsOneShot { get; }

    public bool IsEnded { get; internal set; }
    public string? LastTlsPolicyErrors { get; internal set; }
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;
    internal HashSet<string> ConnectionIds { get; } = [];
    internal HashSet<string> CookiePairs { get; } = [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Upstream.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
