using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Testing;

namespace AcsTool.Proxy.Tests;

public class ForwardingTests
{
    [Fact]
    public async Task FakeAcs_PlainClient_DefaultScriptWorks()
    {
        await using var acs = await FakeAcs.StartAsync();
        using var client = ProxyHarness.CpeClient();
        using var r1 = await client.SendAsync(ProxyHarness.Post(acs.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(CwmpMessageKind.InformResponse, new Core.Parsing.CwmpParser().Parse(await r1.Content.ReadAsByteArrayAsync()).Kind);
        using var r2 = await client.SendAsync(ProxyHarness.Post(acs.Url(), null));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);
        Assert.Empty(acs.Violations);
        Assert.True(acs.Sessions.Single().Ended);
    }

    [Fact]
    public async Task FakeAcs_DigestWithWrongUri_Rejected()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Digest = ("cpe", "pw", "acs"));
        using var client = ProxyHarness.CpeClient();
        using var challenge = await client.SendAsync(ProxyHarness.Post(acs.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var nonce = challenge.Headers.WwwAuthenticate.ToString();
        var n = AuthRedactorNonce(nonce);
        var ha1 = Md5("cpe:acs:pw");
        var ha2 = Md5("POST:/other");
        var resp = Md5($"{ha1}:{n}:00000001:abc:auth:{ha2}");
        var header = $"Digest username=\"cpe\", realm=\"acs\", nonce=\"{n}\", uri=\"/other\", qop=auth, nc=00000001, cnonce=\"abc\", response=\"{resp}\"";
        Assert.False(acs.ValidateDigest(header, "POST", "/acs", "cpe", "pw", "acs"));
        var good = header.Replace("/other", "/acs", StringComparison.Ordinal).Replace(resp, Md5($"{ha1}:{n}:00000001:abc:auth:{Md5("POST:/acs")}"), StringComparison.Ordinal);
        Assert.True(acs.ValidateDigest(good, "POST", "/acs", "cpe", "pw", "acs"));
    }

    private static string AuthRedactorNonce(string www)
    {
        var i = www.IndexOf("nonce=\"", StringComparison.Ordinal) + 7;
        return www[i..www.IndexOf('"', i)];
    }

    #pragma warning disable CA5351 // Digest (RFC 2617) is MD5 by definition
    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
#pragma warning restore CA5351

    [Fact]
    public async Task FullSession_ThroughProxy_OneCompletedSessionWithOrderedExchanges()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
            .ExpectResponse<GetParameterValuesResponseMessage>().End204()
            .ExpectEmpty().End204());
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();

        using var r1 = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        var gpv = await r2.Content.ReadAsByteArrayAsync();
        var id = ProxyHarness.IdOf(gpv);
        var gpvResp = ProxyHarness.Soap(new GetParameterValuesResponseMessage([new ParameterValue("InternetGatewayDevice.DeviceInfo.SoftwareVersion", "1.0")]), id);
        using var r3 = await client.SendAsync(ProxyHarness.Post(h.Url(), gpvResp));
        Assert.Equal(HttpStatusCode.NoContent, r3.StatusCode);
        Assert.Equal(1, h.Tracker.ActiveCount); // awaiting a possible trailing empty POST
        using var r4 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.Equal(HttpStatusCode.NoContent, r4.StatusCode);
        Assert.Empty(acs.Violations);

        var s = await h.WaitForEndedAsync();
        Assert.Single(h.Store.GetAll());
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(SessionSource.Proxy, s.Source);
        Assert.Equal("00D09E-SN1", s.DeviceId!.Key);
        Assert.Equal(new[] { 1, 2, 3, 4 }, s.Exchanges.Select(e => e.Seq));
        Assert.Equal(new[] { CwmpMessageKind.Inform, CwmpMessageKind.Empty, CwmpMessageKind.GetParameterValuesResponse, CwmpMessageKind.Empty },
            s.Exchanges.Select(e => e.RequestSummary.Kind));
        Assert.Equal(new[] { 200, 200, 204, 204 }, s.Exchanges.Select(e => e.Response!.StatusCode ?? 0));
        Assert.All(s.Exchanges, e => Assert.NotNull(e.Timings.Total));
    }

    [Fact]
    public async Task BodyAndHeaders_ForwardedVerbatim_HopByHopRemoved()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        var body = ProxyHarness.Inform("FID");
        var raw = RawHttp.Request("POST", "/acs?x=1", "pc:7547", body, chunked: false, close: true,
            "Content-Type: text/xml; charset=\"utf-8\"", "SOAPAction: \"urn:dslforum-org:cwmp-1-0#Inform\"",
            "Authorization: Basic Y3BlOnB3", "Cookie: a=1; b=2", "Keep-Alive: timeout=5", "X-Custom: Y");
        var resp = await RawHttp.SendAsync(h.Server.BoundPort, raw);
        Assert.StartsWith("HTTP/1.1 200", resp, StringComparison.Ordinal);

        var got = acs.Received.Single();
        Assert.Equal(SHA256.HashData(body), SHA256.HashData(got.Body));
        Assert.Equal("/acs?x=1", got.PathAndQuery);
        Assert.Equal("text/xml; charset=\"utf-8\"", got.Header("Content-Type"));
        Assert.Equal("\"urn:dslforum-org:cwmp-1-0#Inform\"", got.Header("SOAPAction"));
        Assert.Equal("Basic Y3BlOnB3", got.Header("Authorization"));
        Assert.Equal("a=1; b=2", got.Header("Cookie"));
        Assert.Equal("Y", got.Header("X-Custom"));
        Assert.Equal(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), got.Header("Content-Length"));
        Assert.Equal($"127.0.0.1:{acs.Port}", got.Header("Host"));
        Assert.False(got.HasHeader("Keep-Alive"));
        Assert.False(got.HasHeader("Transfer-Encoding"));
        Assert.NotEqual("close", got.Header("Connection"));
    }

    [Fact]
    public async Task ChunkedRequest_UpstreamGetsContentLength()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        var body = ProxyHarness.Inform("CHK");
        var resp = await RawHttp.SendAsync(h.Server.BoundPort, RawHttp.Request("POST", "/acs", "pc", body, chunked: true, close: true, "Content-Type: text/xml"));
        Assert.StartsWith("HTTP/1.1 200", resp, StringComparison.Ordinal);
        var got = acs.Received.Single();
        Assert.Equal(body, got.Body);
        Assert.False(got.HasHeader("Transfer-Encoding"));
        Assert.Equal(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), got.Header("Content-Length"));
    }

    [Fact]
    public async Task EmptyPost_ForwardedWithContentLengthZero_204RelayedWithoutBody()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()))).Dispose();
        var resp = await RawHttp.SendAsync(h.Server.BoundPort, RawHttp.Request("POST", "/acs", "pc", [], false, true));
        Assert.StartsWith("HTTP/1.1 204", resp, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", resp, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length: 0", resp, StringComparison.OrdinalIgnoreCase); // 204 must not carry a body framing
        var empty = acs.Received[1];
        Assert.Empty(empty.Body);
        Assert.Equal("0", empty.Header("Content-Length"));
        Assert.False(empty.HasHeader("Content-Type"));
    }

    [Fact]
    public async Task Upstream500_RelayedAndClassifiedHttpStatus()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().RespondRaw(500, "<err/>"));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("<err/>", await r.Content.ReadAsStringAsync());
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.HttpError, s.Outcome);
        Assert.Equal(UpstreamErrorKind.HttpStatus, s.Exchanges.Single().Error!.Kind);
    }

    [Fact]
    public async Task UpstreamDown_502AndConnectError()
    {
        await using var acs = await FakeAcs.StartAsync();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var h = await ProxyHarness.StartAsync(acs, upstreamOverride: $"http://127.0.0.1:{deadPort}");
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.UpstreamError, s.Outcome);
        var err = s.Exchanges.Single().Error!;
        Assert.Equal(UpstreamErrorKind.Connect, err.Kind);
        Assert.Equal(RequestPhase.Connect, err.Phase);
        Assert.Equal(err, s.LastError);
    }

    [Fact]
    public async Task SlowUpstream_504AndTimeoutAwaitingResponse()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().Delay(TimeSpan.FromSeconds(4)).RespondInformResponse());
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { RequestTimeoutSeconds = 1 });
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.GatewayTimeout, r.StatusCode);
        var err = (await h.WaitForEndedAsync()).Exchanges.Single().Error!;
        Assert.Equal(UpstreamErrorKind.Timeout, err.Kind);
        Assert.Equal(RequestPhase.AwaitingResponse, err.Phase);
    }

    [Fact]
    public async Task AbortMode_UpstreamDown_ResetsCpeConnection()
    {
        await using var acs = await FakeAcs.StartAsync();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { FailureMode = FailureMode.Abort }, upstreamOverride: $"http://127.0.0.1:{deadPort}");
        using var client = ProxyHarness.CpeClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform())));
        Assert.Equal(SessionOutcome.UpstreamError, (await h.WaitForEndedAsync()).Outcome);
    }

    [Fact]
    public async Task TwoCpesInParallel_TwoSessions()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        async Task RunCpe(string serial)
        {
            using var client = ProxyHarness.CpeClient();
            (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform(serial)))).EnsureSuccessStatusCode();
            using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }
        await Task.WhenAll(RunCpe("P1"), RunCpe("P2"));
        await h.WaitForEndedAsync(2);
        var all = h.Store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.All(all, s => Assert.Equal(SessionOutcome.Completed, s.Outcome));
        Assert.All(all, s => Assert.Equal(2, s.Exchanges.Count));
        Assert.Equal(new[] { "00D09E-P1", "00D09E-P2" }, all.Select(s => s.DeviceId!.Key).Order());
    }

    [Fact]
    public async Task PipelinedRequests_ProcessedInOrder()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        var inform = RawHttp.Request("POST", "/acs", "pc", ProxyHarness.Inform("PIPE"), false, false, "Content-Type: text/xml");
        var empty = RawHttp.Request("POST", "/acs", "pc", [], false, true);
        var resp = await RawHttp.SendAsync(h.Server.BoundPort, [.. inform, .. empty]);
        var first = resp.IndexOf("HTTP/1.1 200", StringComparison.Ordinal);
        var second = resp.IndexOf("HTTP/1.1 204", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, resp);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(new[] { CwmpMessageKind.Inform, CwmpMessageKind.Empty }, s.Exchanges.Select(e => e.RequestSummary.Kind));
    }

    [Fact]
    public async Task SetCookieDomain_StrippedAndCookieReplayedOnNewConnection()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.SetCookie = "fakeacs-sid={n}; Domain=acs.example; Secure; Path=/");
        await using var h = await ProxyHarness.StartAsync(acs);
        using var c1 = ProxyHarness.CpeClient(useCookies: false);
        using var r1 = await c1.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()));
        var setCookie = r1.Headers.GetValues("Set-Cookie").Single();
        // Secure is only stripped for an HTTPS upstream behind an HTTP CPE leg; Domain always (default settings)
        Assert.Equal("fakeacs-sid=1; Secure; Path=/", setCookie);
        // CPE reconnects on a new TCP connection and replays the cookie: same proxy session
        using var c2 = ProxyHarness.CpeClient(useCookies: false);
        using var r2 = await c2.SendAsync(ProxyHarness.Post(h.Url(), null, ("Cookie", "fakeacs-sid=1")));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Single(h.Store.GetAll());
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Contains("Set-Cookie Domain stripped", s.Exchanges[0].Notes);
    }

    [Fact]
    public async Task DigestAuth_RelayedTransparently_SingleSession()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Digest = ("cpe", "pw", "acs"));
        await using var h = await ProxyHarness.StartAsync(acs);
        var handler = new SocketsHttpHandler { UseProxy = false, Credentials = new NetworkCredential("cpe", "pw"), PreAuthenticate = false };
        using var client = new HttpClient(handler);
        using var r1 = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Single(h.Store.GetAll());
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(401, s.Exchanges[0].Response!.StatusCode);
        Assert.Contains("AuthChallenge", s.Exchanges[0].Notes);
        Assert.Contains(s.Exchanges, e => e.Response!.StatusCode == 200);
    }

    [Fact]
    public async Task NonCwmpGet_RecordedAsOneShotSession()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Override = r => r.Method == "GET" ? new FakeAcsResponse { Status = 200, Body = "hi"u8.ToArray() } : null);
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        Assert.Equal("hi", await client.GetStringAsync(h.Url("/status")));
        var s = await h.WaitForEndedAsync();
        Assert.Equal("Non-CWMP request", s.Label);
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
    }

    [Fact]
    public async Task OversizedBody_413AndProtocolError()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { MaxRequestBodyBytes = 1000 });
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), new byte[5000]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal(SessionOutcome.ProtocolError, (await h.WaitForEndedAsync()).Outcome);
        Assert.Empty(acs.Received);
    }

    [Fact]
    public async Task IdleSession_AbandonedBySweeper()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { SessionIdleTimeoutSeconds = 1 });
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()))).Dispose();
        h.Tracker.Sweep(); // too early: still active
        Assert.Equal(1, h.Tracker.ActiveCount);
        await Task.Delay(1200);
        h.Tracker.Sweep();
        Assert.Equal(SessionOutcome.Abandoned, (await h.WaitForEndedAsync()).Outcome);
    }

    [Fact]
    public async Task CpeClosesWithoutTrailingEmpty_CompletedAfterGrace()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
            .ExpectResponse<GetParameterValuesResponseMessage>().End204());
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()))).Dispose();
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        var id = ProxyHarness.IdOf(await r2.Content.ReadAsByteArrayAsync());
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Soap(new GetParameterValuesResponseMessage([]), id)))).Dispose();
        h.Tracker.Sweep(); // within the grace period
        Assert.Equal(1, h.Tracker.ActiveCount);
        await Task.Delay(CpeSessionTracker.CompletionGrace + TimeSpan.FromMilliseconds(200));
        h.Tracker.Sweep();
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(3, s.Exchanges.Count);
    }

    [Fact]
    public async Task UpstreamUrlWithCredentials_NotForwarded_RedactedInSessionTarget()
    {
        await using var acs = await FakeAcs.StartAsync();
        var withCreds = new UriBuilder(acs.BaseUri) { UserName = "user", Password = "secret" }.Uri.ToString();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { UpstreamUrl = withCreds });
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()))).Dispose();
        Assert.True(await TestUtil.WaitUntilAsync(() => acs.Received.Count == 1));
        Assert.Null(acs.Received[0].Header("Authorization"));
        var s = Assert.Single(h.Store.GetAll());
        Assert.DoesNotContain("secret", s.TargetUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("user", s.TargetUrl, StringComparison.Ordinal);
    }
}

