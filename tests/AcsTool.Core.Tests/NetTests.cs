using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using AcsTool.Core.Net;
using AcsTool.Testing;

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
        // Connect timeout 500 ms, request timeout 10 s. Upper bound 5 s: well above 500 ms plus a cold handler, and below the
        // 10 s request timeout, so a missing or inflated connect timeout fails here. The 30 s guard turns a hang into a failure.
        var (r, elapsed) = await TestUtil.TimedAsync(
            () => Send("http://10.255.255.1:7547/acs", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(30));
        Assert.Equal(UpstreamErrorKind.Connect, r.Error!.Kind);
        // Some sandboxes reject unroutable addresses immediately (NetworkUnreachable) instead of timing out.
        Assert.Contains(r.Error.SocketError, new[] { "TimedOut", "NetworkUnreachable", "HostUnreachable" });
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"connect failure took {elapsed}");
        if (r.Error.SocketError == "TimedOut")
        {
            // Lower bound only for a real timeout: the connect timeout must not fire early (400 ms allows timer granularity).
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(400), $"connect timed out after only {elapsed}");
        }
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
            // Bounds for the 3 s request timeout: at least the timeout minus 100 ms for timer granularity (it doesn't fire
            // early; review n1) and at most 20 s (a 60 s or missing timeout fails; 20 s leaves room for a slow runner).
            // The 30 s guard fails a removed timeout instead of hanging.
            var (r, elapsed) = await TestUtil.TimedAsync(
                () => Send($"http://127.0.0.1:{port}/acs", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3)), TimeSpan.FromSeconds(30));
            Assert.Equal(UpstreamErrorKind.Timeout, r.Error!.Kind);
            Assert.Equal(RequestPhase.AwaitingResponse, r.Error.Phase);
            Assert.InRange(elapsed, TimeSpan.FromSeconds(3) - TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(20));
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

