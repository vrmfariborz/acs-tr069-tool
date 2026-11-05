using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using AcsTool.Core.Net;

namespace AcsTool.Core.Tests;

public class ErrorClassifierTests
{
    private static PhaseTracker Connected()
    {
        var t = new PhaseTracker();
        t.MarkDnsDone(TimeSpan.FromMilliseconds(1), [IPAddress.Loopback]);
        t.MarkConnected(TimeSpan.FromMilliseconds(1), new IPEndPoint(IPAddress.Loopback, 1));
        return t;
    }

    [Fact]
    public void Classify_CallerCanceled_Canceled()
    {
        var e = ErrorClassifier.Classify(new OperationCanceledException(), new PhaseTracker(), false, timedOutByUs: false, canceledByCaller: true);
        Assert.Equal(UpstreamErrorKind.Canceled, e.Kind);
    }

    [Theory]
    [InlineData(0, RequestPhase.Dns)]
    [InlineData(1, RequestPhase.Connect)]
    [InlineData(2, RequestPhase.Tls)]
    [InlineData(3, RequestPhase.AwaitingResponse)]
    [InlineData(4, RequestPhase.ReadingBody)]
    public void Classify_OurTimeout_TimeoutWithPhase(int progress, RequestPhase expected)
    {
        var t = new PhaseTracker();
        if (progress >= 1)
        {
            t.MarkDnsDone(TimeSpan.Zero, []);
        }
        if (progress >= 2)
        {
            t.MarkConnected(TimeSpan.Zero, null);
        }
        if (progress >= 3)
        {
            t.MarkTlsDone();
        }
        if (progress >= 4)
        {
            t.MarkHeaders(TimeSpan.Zero);
        }
        var e = ErrorClassifier.Classify(new TaskCanceledException(), t, isHttps: true, timedOutByUs: true, canceledByCaller: false);
        Assert.Equal(UpstreamErrorKind.Timeout, e.Kind);
        Assert.Equal(expected, e.Phase);
    }

    [Fact]
    public void Classify_NameResolutionError_Dns()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "no such host", new SocketException((int)SocketError.HostNotFound));
        Assert.Equal(UpstreamErrorKind.Dns, ErrorClassifier.Classify(ex, new PhaseTracker(), false, false, false).Kind);
    }

    [Fact]
    public void Classify_WrappedNameResolution_DnsEvenIfOuterIsConnectionError()
    {
        var inner = new HttpRequestException(HttpRequestError.NameResolutionError, "dns", new SocketException((int)SocketError.TryAgain));
        var ex = new HttpRequestException(HttpRequestError.ConnectionError, "wrapped", inner);
        Assert.Equal(UpstreamErrorKind.Dns, ErrorClassifier.Classify(ex, new PhaseTracker(), false, false, false).Kind);
    }

    [Fact]
    public void Classify_ConnectionError_ConnectWithSocketError()
    {
        var t = new PhaseTracker();
        t.MarkDnsDone(TimeSpan.Zero, []);
        var ex = new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused));
        var e = ErrorClassifier.Classify(ex, t, false, false, false);
        Assert.Equal(UpstreamErrorKind.Connect, e.Kind);
        Assert.Equal("ConnectionRefused", e.SocketError);
        Assert.Equal("CONNECT ConnectionRefused", e.ShortText);
    }

    [Fact]
    public void Classify_SecureConnectionError_Tls()
    {
        var ex = new HttpRequestException(HttpRequestError.SecureConnectionError, "tls", new AuthenticationException("remote certificate is invalid"));
        var e = ErrorClassifier.Classify(ex, Connected(), true, false, false, lastTlsPolicyErrors: "RemoteCertificateChainErrors");
        Assert.Equal(UpstreamErrorKind.Tls, e.Kind);
        Assert.Equal("RemoteCertificateChainErrors", e.TlsDetail);
        Assert.Contains("certificate", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.HttpProtocolError)]
    public void Classify_ProtocolErrors_Protocol(HttpRequestError err)
    {
        Assert.Equal(UpstreamErrorKind.Protocol, ErrorClassifier.Classify(new HttpRequestException(err, "x"), Connected(), false, false, false).Kind);
    }

    [Fact]
    public void Classify_InnerWalk_SocketAndAuthAndIo()
    {
        Assert.Equal(UpstreamErrorKind.Dns, ErrorClassifier.Classify(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), new PhaseTracker(), false, false, false).Kind);
        var t = new PhaseTracker();
        t.MarkDnsDone(TimeSpan.Zero, []);
        Assert.Equal(UpstreamErrorKind.Connect, ErrorClassifier.Classify(new HttpRequestException("x", new SocketException((int)SocketError.NetworkUnreachable)), t, false, false, false).Kind);
        Assert.Equal(UpstreamErrorKind.Tls, ErrorClassifier.Classify(new HttpRequestException("x", new AuthenticationException("a")), Connected(), true, false, false).Kind);
        Assert.Equal(UpstreamErrorKind.Protocol, ErrorClassifier.Classify(new HttpRequestException("x", new IOException("eof")), Connected(), false, false, false).Kind);
        Assert.Equal(UpstreamErrorKind.Unknown, ErrorClassifier.Classify(new InvalidOperationException("?"), Connected(), false, false, false).Kind);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(204, false)]
    [InlineData(401, false)]
    [InlineData(403, true)]
    [InlineData(500, true)]
    public void FromStatus_ErrorsForStatusAbove400Except401(int status, bool isError)
    {
        var e = ErrorClassifier.FromStatus(status);
        Assert.Equal(isError, e is not null);
        if (e is not null)
        {
            Assert.Equal(UpstreamErrorKind.HttpStatus, e.Kind);
            Assert.Equal(status, e.HttpStatus);
        }
    }
}

public class LiveNetworkClassificationTests
{
    private static async Task<UpstreamSendResult> Send(string url, TimeSpan connectTimeout, TimeSpan requestTimeout)
    {
        var tracker = new PhaseTracker();
        using var handler = new UpstreamHandlerFactory().Create(
            new UpstreamHandlerOptions(connectTimeout, TlsOptions.Default, false, null, null, false, false), () => tracker, _ => { });
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent([]) };
        return await UpstreamSender.SendAsync(invoker, req, tracker, requestTimeout, 1024 * 1024, TimeProvider.System, CancellationToken.None);
    }

    private static async Task<bool> ResolvesAsync(string host)
    {
        try
        {
            return (await Dns.GetHostAddressesAsync(host)).Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task Dns_NonexistentInvalidTld_Dns()
    {
        // Reported as Skipped, not Passed, when the environment can't run it (BUG-03); also covered by constructed-exception tests.
        Skip.If(await ResolvesAsync("nonexistent.invalid"),
            "Environment resolves nonexistent.invalid (DNS hijacked, e.g. a sandbox egress resolver answers every name)");
        var r = await Send("http://nonexistent.invalid/acs", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        Assert.Equal(UpstreamErrorKind.Dns, r.Error!.Kind);
    }

    [Fact]
    public async Task Connect_ClosedLoopbackPort_ConnectionRefused()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var r = await Send($"http://127.0.0.1:{port}/acs", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        Assert.Equal(UpstreamErrorKind.Connect, r.Error!.Kind);
        Assert.Equal("ConnectionRefused", r.Error.SocketError);
    }

    [SkippableFact]
    public async Task Connect_UnroutableAddress_ConnectTimedOut()
    {
        Skip.If(Environment.GetEnvironmentVariable("ACSTOOL_SKIP_NET_TIMEOUT") == "1", "ACSTOOL_SKIP_NET_TIMEOUT=1 (plan §5.1)"); // BUG-03
        var r = await Send("http://10.255.255.1:7547/acs", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10));
        Assert.Equal(UpstreamErrorKind.Connect, r.Error!.Kind);
        // Some sandboxes reject unroutable addresses immediately (NetworkUnreachable) instead of timing out.
        Assert.Contains(r.Error.SocketError, new[] { "TimedOut", "NetworkUnreachable", "HostUnreachable" });
    }

    [Fact]
    public async Task Timeout_ServerAcceptsButNeverResponds_TimeoutAwaitingResponse()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var accept = l.AcceptTcpClientAsync();
            // 3 s, not 500 ms: on a cold CI runner (coverage instrumentation, parallel test classes) the first request through
            // a new handler can spend >500 ms before it reaches the connect callback, so the timeout fired while the phase was
            // still Dns (FLAKY-02, linux run 40860632752). The asserts are unchanged; the server still never responds.
            var r = await Send($"http://127.0.0.1:{port}/acs", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
            Assert.Equal(UpstreamErrorKind.Timeout, r.Error!.Kind);
            Assert.Equal(RequestPhase.AwaitingResponse, r.Error.Phase);
            (await accept).Dispose();
        }
        finally
        {
            l.Stop();
        }
    }

    [Theory]
    [InlineData("http://user:secret@acs.example:7557/acs?x=1", "http://acs.example:7557/acs?x=1", true)]
    [InlineData("https://acs.example/acs", "https://acs.example/acs", false)]
    [InlineData("not a url", "not a url", false)]
    public void UrlUtil_RedactsUserInfo(string url, string expected, bool hasUserInfo)
    {
        Assert.Equal(expected, UrlUtil.RedactUserInfo(url));
        Assert.Equal(hasUserInfo, UrlUtil.HasUserInfo(url));
    }
}

public class PhaseTrackerEndpointTests
{
    [Fact]
    public void New_NoConnection_SerialZero_LocalNull()
    {
        var t = new PhaseTracker();

        Assert.Equal(0, t.ConnectionSerial);
        Assert.Null(t.LocalEndPoint);
    }

    [Fact]
    public void BeginConnect_IncrementsSerial_ClearsLocalAndRemote_ResetKeepsThem()
    {
        var t = new PhaseTracker();
        t.BeginConnect();
        t.MarkConnected(TimeSpan.FromMilliseconds(1), IPEndPoint.Parse("127.0.0.1:7557"), IPEndPoint.Parse("127.0.0.1:50000"));

        t.Reset(); // per-exchange reset on a pooled connection

        Assert.Equal(1, t.ConnectionSerial);
        Assert.Equal(IPEndPoint.Parse("127.0.0.1:50000"), t.LocalEndPoint);
        Assert.Equal(IPEndPoint.Parse("127.0.0.1:7557"), t.RemoteEndPoint);

        t.BeginConnect(); // a new socket; until it connects there is no local endpoint (connect failure → upstream local null)

        Assert.Equal(2, t.ConnectionSerial);
        Assert.Null(t.LocalEndPoint);
        Assert.Null(t.RemoteEndPoint);
    }

    [Fact]
    public void MarkConnected_IPv4MappedLocal_IsNormalised()
    {
        var t = new PhaseTracker();
        t.BeginConnect();

        t.MarkConnected(TimeSpan.Zero, IPEndPoint.Parse("127.0.0.1:7557"), new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 50001));

        Assert.Equal(IPEndPoint.Parse("127.0.0.1:50001"), t.LocalEndPoint);
    }

    [Theory]
    [InlineData("[::ffff:192.0.2.7]:7547", "192.0.2.7:7547")]
    [InlineData("192.0.2.7:7547", "192.0.2.7:7547")]
    [InlineData("[2001:db8::1]:7547", "[2001:db8::1]:7547")]
    public void EndPointUtil_Normalize(string input, string expected)
    {
        Assert.Equal(IPEndPoint.Parse(expected), EndPointUtil.Normalize(IPEndPoint.Parse(input)));
    }

    [Fact]
    public void EndPointUtil_Normalize_Null_IsNull()
    {
        Assert.Null(EndPointUtil.Normalize((IPEndPoint?)null));
        Assert.Null(EndPointUtil.Normalize((IPAddress?)null));
    }
}

public sealed class LiveConnectEndpointTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<IPEndPoint> _accepted = new();
    private readonly Task _serverLoop;

    public LiveConnectEndpointTests()
    {
        _listener.Start();
        _serverLoop = ServeAsync();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    [Fact]
    public async Task Connect_RecordsLocalEndPoint_Serial_PooledReuseKeeps_NewSocketIncrements()
    {
        var tracker = new PhaseTracker();
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectCallbackFactory.Create(TimeSpan.FromSeconds(5), () => tracker),
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        // 1st request: a new socket.
        (await client.GetAsync(new Uri($"http://127.0.0.1:{Port}/keep"))).Dispose();
        var first = tracker.LocalEndPoint;
        Assert.NotNull(first);
        Assert.NotEqual(0, first.Port);
        Assert.Equal(IPAddress.Loopback, first.Address);
        Assert.Equal(1, tracker.ConnectionSerial);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, Port), tracker.RemoteEndPoint);
        Assert.Contains(first, _accepted); // the server saw our recorded local endpoint as its remote

        // 2nd request: pooled reuse of the same socket (server answered keep-alive); this one asks the server to close after.
        tracker.Reset();
        (await client.GetAsync(new Uri($"http://127.0.0.1:{Port}/close"))).Dispose();
        Assert.Equal(1, tracker.ConnectionSerial);
        Assert.Equal(first, tracker.LocalEndPoint);
        Assert.Single(_accepted);

        // 3rd request: the server closed the connection, so the handler opens a new socket.
        tracker.Reset();
        (await client.GetAsync(new Uri($"http://127.0.0.1:{Port}/keep"))).Dispose();
        Assert.Equal(2, tracker.ConnectionSerial);
        Assert.NotNull(tracker.LocalEndPoint);
        Assert.NotEqual(first, tracker.LocalEndPoint);
        Assert.Contains(tracker.LocalEndPoint!, _accepted);
    }

    [Fact]
    public async Task Connect_RefusedAfterSuccessfulConnect_RemoteAndLocalNull_SerialCounted()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var tracker = new PhaseTracker();
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectCallbackFactory.Create(TimeSpan.FromSeconds(5), () => tracker),
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        (await client.GetAsync(new Uri($"http://127.0.0.1:{Port}/keep"))).Dispose();
        Assert.Equal(1, tracker.ConnectionSerial);
        Assert.NotNull(tracker.RemoteEndPoint);
        Assert.NotNull(tracker.LocalEndPoint);
        tracker.Reset();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{port}/")));

        // Review m4: the failed attempt must not keep the previous socket's endpoints.
        Assert.Equal(2, tracker.ConnectionSerial);
        Assert.Null(tracker.RemoteEndPoint);
        Assert.Null(tracker.LocalEndPoint);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _accepted.Enqueue((IPEndPoint)client.Client.RemoteEndPoint!);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var pending = new System.Text.StringBuilder();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buffer, _cts.Token);
                    if (n == 0)
                    {
                        return;
                    }
                    pending.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, n));
                    int end;
                    while ((end = pending.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        var head = pending.ToString(0, end);
                        pending.Remove(0, end + 4);
                        var close = head.StartsWith("GET /close", StringComparison.Ordinal);
                        var response = close
                            ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
                        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), _cts.Token);
                        if (close)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        await _serverLoop;
        _cts.Dispose();
    }
}

