using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AcsTool.Proxy.Tests;

public class TrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _store;
    private readonly CpeSessionTracker _tracker;
    private readonly CwmpParser _parser = new();
    private readonly MessageSummarizer _summ = new();

    public TrackerTests()
    {
        _store = new InMemorySessionStore(new CaptureSettings(), _time);
        _tracker = new CpeSessionTracker(new ProxySettings(), new UpstreamHandlerFactory(), _store, _time, NullLogger<CpeSessionTracker>.Instance);
    }

    private ProxySession Resolve(byte[] body, string conn, string ip = "192.168.1.50", string? cookie = null, string method = "POST")
    {
        var headers = cookie is null ? new List<KeyValuePair<string, string>>() : [new("Cookie", cookie)];
        var req = new IncomingRequest(method, "/acs", headers, body, ip, 50000, conn, false);
        var parsed = _parser.Parse(body);
        return _tracker.Resolve(req, parsed, _summ.Summarize(parsed));
    }

    private static ForwardResult Resp(int status, byte[]? body = null, params (string, string)[] headers) =>
        new(new Uri("http://acs/acs"), status, headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)).ToList(), body ?? [], ErrorClassifier.FromStatus(status),
            new ExchangeTimings(DateTimeOffset.UnixEpoch));

    private static readonly byte[] InformA = ProxyHarness.Inform("A");
    private static readonly byte[] InformB = ProxyHarness.Inform("B");

    [Fact]
    public void Resolve_SameConnection_SameSession()
    {
        var s1 = Resolve(InformA, "c1");
        var s2 = Resolve([], "c1");
        Assert.Same(s1, s2);
        Assert.Equal(1, _tracker.ActiveCount);
        Assert.Equal("00D09E-A", s1.DeviceKey);
    }

    [Fact]
    public void Resolve_ReconnectWithCookie_SameSession()
    {
        var s1 = Resolve(InformA, "c1");
        _tracker.ObserveResponse(s1, Resp(200, [1], ("Set-Cookie", "sid=xyz; Path=/")), CwmpMessageKind.Inform, false);
        var other = Resolve(InformB, "c9");
        var s2 = Resolve([], "c2", cookie: "foo=bar; sid=xyz");
        Assert.Same(s1, s2);
        Assert.NotSame(other, s2);
    }

    [Fact]
    public void Resolve_IpFallback_MostRecentWithAmbiguityNote()
    {
        var a = Resolve(InformA, "c1");
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = Resolve(InformB, "c2");
        var s = Resolve([], "c3");
        Assert.Same(b, s);
        Assert.Contains("ambiguous correlation by IP", _store.Get(b.SessionId)!.Notes);
        Assert.DoesNotContain("ambiguous correlation by IP", _store.Get(a.SessionId)!.Notes);
    }

    [Fact]
    public void Resolve_SingleSessionOnIp_NoAmbiguityNote()
    {
        var a = Resolve(InformA, "c1");
        Assert.Same(a, Resolve([], "c2"));
        Assert.Empty(_store.Get(a.SessionId)!.Notes);
    }

    [Fact]
    public void Resolve_NewInformSameIpAndDevice_SupersedesOld()
    {
        var s1 = Resolve(InformA, "c1");
        var s2 = Resolve(InformA, "c2");
        Assert.NotSame(s1, s2);
        Assert.Equal(SessionOutcome.Superseded, _store.Get(s1.SessionId)!.Outcome);
        Assert.True(s1.IsDisposed);
        Assert.Equal(1, _tracker.ActiveCount);
    }

    [Fact]
    public void Resolve_InformAfter401_ReusesSession()
    {
        var s1 = Resolve(InformA, "c1");
        _tracker.ObserveResponse(s1, Resp(401, null, ("WWW-Authenticate", "Digest realm=\"x\"")), CwmpMessageKind.Inform, false);
        var s2 = Resolve(InformA, "c1");
        Assert.Same(s1, s2);
        // also when the CPE reconnects for the retry
        var s3 = Resolve(InformA, "c7");
        Assert.Same(s1, s3);
    }

    [Fact]
    public void Resolve_UnknownRequest_CreatesOrphan()
    {
        var s = Resolve([], "c1", ip: "10.9.9.9");
        Assert.True(s.IsOrphan);
        Assert.Equal("Unknown session from 10.9.9.9", _store.Get(s.SessionId)!.Label);
        var g = Resolve([], "c2", ip: "10.9.9.8", method: "GET");
        Assert.True(g.IsOneShot);
        Assert.Equal("Non-CWMP request", _store.Get(g.SessionId)!.Label);
    }

    [Fact]
    public void Sweeper_IdleTimeout_AbandonsAndDisposesHandler()
    {
        _tracker.StartSweeper();
        var s = Resolve(InformA, "c1");
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SessionOutcome.InProgress, _store.Get(s.SessionId)!.Outcome);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionOutcome.Abandoned, _store.Get(s.SessionId)!.Outcome);
        Assert.True(s.IsDisposed);
        Assert.Equal(0, _tracker.ActiveCount);
        // indexes cleaned: the same connection now yields a new orphan session
        Assert.NotSame(s, Resolve([], "c1"));
    }

    [Theory]
    [InlineData(CwmpMessageKind.Empty, 204, 0, SessionOutcome.Completed)]
    [InlineData(CwmpMessageKind.Empty, 200, 0, SessionOutcome.Completed)]
    [InlineData(CwmpMessageKind.Inform, 204, 0, SessionOutcome.ProtocolError)]
    [InlineData(CwmpMessageKind.GetParameterValuesResponse, 204, 0, null)]
    [InlineData(CwmpMessageKind.Inform, 200, 10, null)]
    [InlineData(CwmpMessageKind.Inform, 403, 0, SessionOutcome.AuthFailed)]
    [InlineData(CwmpMessageKind.Inform, 500, 0, SessionOutcome.HttpError)]
    [InlineData(CwmpMessageKind.Inform, 401, 0, null)]
    public void EvaluateEnd_Rules(CwmpMessageKind requestKind, int status, int bodyLen, SessionOutcome? expected)
    {
        var s = Resolve(InformA, "c1");
        var notes = new List<string>();
        Assert.Equal(expected, CpeSessionTracker.EvaluateEnd(s, requestKind, Resp(status, new byte[bodyLen]), null, notes));
    }

    [Fact]
    public void EvaluateEnd_TwoConsecutive401_AuthFailed()
    {
        var s = Resolve(InformA, "c1");
        var notes = new List<string>();
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Inform, Resp(401), null, notes));
        Assert.Contains("AuthChallenge", notes);
        _tracker.ObserveResponse(s, Resp(401), CwmpMessageKind.Inform, false);
        Assert.Equal(SessionOutcome.AuthFailed, CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Inform, Resp(401), null, notes));
    }

    [Fact]
    public void EvaluateEnd_FaultSeen_EndsWithFaultOutcome()
    {
        var s = Resolve(InformA, "c1");
        var notes = new List<string>();
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Fault, Resp(200, [1]), CwmpMessageKind.GetParameterValues, notes));
        Assert.Equal(SessionOutcome.CpeFault, CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Empty, Resp(204), null, notes));
    }

    [Fact]
    public void EvaluateEnd_EmptyAcsResponseToCpeResponseAfterEmptyPost_AwaitsTrailingEmpty()
    {
        var s = Resolve(InformA, "c1");
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Inform, Resp(200, [1]), CwmpMessageKind.InformResponse, []));
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Empty, Resp(200, [1]), CwmpMessageKind.GetParameterValues, []));
        var notes = new List<string>();
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.GetParameterValuesResponse, Resp(204), null, notes));
        Assert.True(s.AwaitingFinalEmpty);
        Assert.Empty(notes);
        Assert.Equal(SessionOutcome.Completed, CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Empty, Resp(204), null, []));
    }

    [Fact]
    public void Sweeper_AwaitingFinalEmpty_CompletesAfterGrace()
    {
        _tracker.StartSweeper();
        var s = Resolve(InformA, "c1");
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Empty, Resp(200, [1]), CwmpMessageKind.GetParameterValues, []));
        Assert.Null(CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.GetParameterValuesResponse, Resp(204), null, []));
        _time.Advance(CpeSessionTracker.SweepInterval);
        Assert.Equal(0, _tracker.ActiveCount);
        Assert.Equal(SessionOutcome.Completed, _store.Get(s.SessionId)!.Outcome);
    }

    [Fact]
    public void EvaluateEnd_UpstreamFailure_UpstreamError()
    {
        var s = Resolve(InformA, "c1");
        var r = new ForwardResult(new Uri("http://x"), null, [], default, new UpstreamError(UpstreamErrorKind.Connect, RequestPhase.Connect, "refused"), new ExchangeTimings(DateTimeOffset.UnixEpoch));
        Assert.Equal(SessionOutcome.UpstreamError, CpeSessionTracker.EvaluateEnd(s, CwmpMessageKind.Inform, r, null, []));
    }
}

public class UriAndHeaderTests
{
    [Theory]
    [InlineData("http://acs:7557", PathMode.PassThrough, null, "/acs", "http://acs:7557/acs")]
    [InlineData("http://acs:7557/", PathMode.PassThrough, null, "/acs?x=1", "http://acs:7557/acs?x=1")]
    [InlineData("https://acs/base/", PathMode.PassThrough, null, "/acs", "https://acs/base/acs")]
    [InlineData("http://acs", PathMode.PassThrough, null, "/a%20b/c%2Fd", "http://acs/a%20b/c%2Fd")]
    [InlineData("http://acs", PathMode.PassThrough, null, "http://proxy:7547/acs?q", "http://acs/acs?q")]
    [InlineData("http://acs/fixed", PathMode.Fixed, null, "/anything", "http://acs/fixed")]
    [InlineData("http://acs/ignored", PathMode.Fixed, "/tr069", "/anything", "http://acs/tr069")]
    public void BuildUpstreamUri_Combines(string upstream, PathMode mode, string? fixedPath, string target, string expected)
    {
        Assert.Equal(expected, UpstreamForwarder.BuildUpstreamUri(upstream, mode, fixedPath, target).OriginalString);
    }

    [Fact]
    public void RewriteSetCookie_StripsDomainAndSecureOnHttp()
    {
        var notes = new List<string>();
        var r = new HeaderRewriter(new ProxySettings());
        Assert.Equal("sid=1; Path=/; HttpOnly", r.RewriteSetCookie("sid=1; Domain=acs.test; Secure; HttpOnly; Path=/".Replace("HttpOnly; Path=/", "Path=/; HttpOnly", StringComparison.Ordinal), false, true, notes));
        Assert.Contains("Set-Cookie Domain stripped", notes);
        Assert.Contains("Set-Cookie Secure stripped (CPE leg is HTTP)", notes);
        var off = new HeaderRewriter(new ProxySettings(StripCookieDomain: false, StripCookieSecureOnHttp: false));
        Assert.Equal("sid=1; Domain=acs.test; Secure", off.RewriteSetCookie("sid=1; Domain=acs.test; Secure", false, true, []));
    }

    [Fact]
    public void ForUpstreamRequest_DropsHostLengthExpectAndHopByHop()
    {
        var r = new HeaderRewriter(new ProxySettings(AddXForwardedFor: true));
        var req = new IncomingRequest("POST", "/acs",
        [
            new("Host", "pc:7547"), new("Content-Length", "10"), new("Expect", "100-continue"), new("Connection", "keep-alive"),
            new("SOAPAction", "\"\""), new("Authorization", "Basic abc"), new("Cookie", "sid=1"),
        ], default, "192.168.1.50", 1, "c", false);
        var outHeaders = r.ForUpstreamRequest(req, new Uri("http://acs")).ToList();
        Assert.Equal(new[] { "SOAPAction", "Authorization", "Cookie", "X-Forwarded-For" }, outHeaders.Select(h => h.Key));
        Assert.Equal("192.168.1.50", outHeaders[^1].Value);
    }
}
