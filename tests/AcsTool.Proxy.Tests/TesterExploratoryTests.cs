using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Sessions;
using AcsTool.Testing;

namespace AcsTool.Proxy.Tests;

/// <summary>
/// TesterBot M0–M4 verification: crafted CPE traffic through a real ProxyServer (127.0.0.1:0) in front of FakeAcs (127.0.0.1:0).
/// Tests named *_Bug* encode the expected (plan/TR-069) behavior for a defect listed in docs/TEST_REPORT_M0-M4.md and are skipped
/// so that ./build.sh stays green; remove the Skip to reproduce.
/// </summary>
public class TesterExploratoryTests
{
    private static int DeadPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static byte[] InformWith(string serial, params string[] events) =>
        new CwmpMessageBuilder().Build(new InformBuilder().Create(new DeviceIdStruct("Acme", "00D09E", "IGD", serial),
            events.Select(e => new EventStruct(e)), [new ParameterValue("InternetGatewayDevice.DeviceInfo.SoftwareVersion", "1.0")], 0,
            DateTimeOffset.UtcNow), CwmpVersion.V1_0, new CwmpHeader("1"));

    // ---- crafted Informs: BOOTSTRAP / BOOT / PERIODIC -------------------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_Cwmp12Tr181Fixture_ForwardedByteIdentical_SessionCompletedWithEvents()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => { s.Version = CwmpVersion.V1_2; s.ExpectInform().RespondInformResponse().ExpectEmpty().End204(); });
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        var body = Fixtures.Load("inform-bootstrap-cwmp12-tr181.xml");
        using var r1 = await client.SendAsync(ProxyHarness.Post(h.Url(), body));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);

        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(SHA256.HashData(body), SHA256.HashData(acs.Received[0].Body));
        Assert.Equal("A1B2C3-CT0001", s.DeviceId!.Key);
        Assert.Equal(CwmpVersion.V1_2, s.Version);
        Assert.Equal(new[] { EventCodes.Bootstrap, EventCodes.Boot }, s.Events.Select(e => e.EventCode));
        Assert.Empty(acs.Violations);
    }

    [Fact]
    public async Task Boot_Cwmp10Fixture_ForwardedByteIdentical_SessionCompleted()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        var body = Fixtures.Load("inform-boot-cwmp10.xml");
        (await client.SendAsync(ProxyHarness.Post(h.Url(), body))).EnsureSuccessStatusCode();
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal(SHA256.HashData(body), SHA256.HashData(acs.Received[0].Body));
        Assert.Contains(s.Events, e => e.EventCode == EventCodes.Boot);
        Assert.Equal(CwmpVersion.V1_0, s.Version);
    }

    [Fact]
    public async Task Periodic_DefaultNamespaceFixture_SessionCompletedWithPeriodicEvent()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), Fixtures.Load("inform-periodic-unqualified-noprefix.xml")))).EnsureSuccessStatusCode();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), null))).Dispose();
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Equal("112233-NP-9", s.DeviceId!.Key);
        Assert.Equal(EventCodes.Periodic, Assert.Single(s.Events).EventCode);
    }

    // ---- malformed / empty ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MalformedXml_FirstRequest_ForwardedVerbatim_RecordedAsOrphanNotXml_AcsStatusRelayed()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        var body = Fixtures.Load("not-xml.txt");
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), body));
        // FakeAcs answers a non-Inform without a session with 400; the proxy must relay it (fidelity) and classify it HttpStatus.
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(body, acs.Received.Single().Body);
        var s = await h.WaitForEndedAsync();
        Assert.StartsWith("Unknown session from", s.Label, StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.HttpError, s.Outcome);
        Assert.Equal(CwmpMessageKind.NotXml, s.Exchanges.Single().RequestSummary.Kind);
        Assert.Equal(UpstreamErrorKind.HttpStatus, s.Exchanges.Single().Error!.Kind);
    }

    [Fact]
    public async Task MalformedXml_MidSession_StaysInSameSession()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().RespondInformResponse().ExpectAny().End204());
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("MAL")))).EnsureSuccessStatusCode();
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), "<soap:Envelope><unclosed>"u8.ToArray()));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);
        Assert.Single(h.Store.GetAll());
        var s = h.Store.GetAll().Single();
        Assert.Equal(2, s.Exchanges.Count);
        Assert.Equal(CwmpMessageKind.NotXml, s.Exchanges[1].RequestSummary.Kind);
    }

    [Fact]
    public async Task XxeBody_ForwardedVerbatim_EntityNotResolved_ProxyKeepsServing()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Override = _ => new FakeAcsResponse { Status = 204 });
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        var body = Fixtures.Load("xxe-attempt.xml");
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), body));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(body, acs.Received.Single().Body);
        var ex = h.Store.GetAll().Single().Exchanges.Single();
        Assert.NotEqual(CwmpMessageKind.Inform, ex.RequestSummary.Kind);
        Assert.DoesNotContain("root:", ex.RequestSummary.OneLine, StringComparison.Ordinal);
        // An orphan answered with an empty 2xx stays open (rule "empty ACS response" -> keep open) until the idle sweep.
        Assert.Equal(1, h.Tracker.ActiveCount);
        using var again = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("AFTERXXE")));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task EmptyPost_WithoutSession_OrphanForwardedWithContentLengthZero()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Override = r => r.Body.Length == 0 ? new FakeAcsResponse { Status = 204 } : null);
        await using var h = await ProxyHarness.StartAsync(acs);
        var resp = await RawHttp.SendAsync(h.Server.BoundPort, RawHttp.Request("POST", "/acs", "pc", [], false, true));
        Assert.StartsWith("HTTP/1.1 204", resp, StringComparison.Ordinal);
        Assert.Equal("0", acs.Received.Single().Header("Content-Length"));
        var s = await h.WaitForEndedAsync();
        Assert.StartsWith("Unknown session from", s.Label, StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
    }

    [Fact]
    public async Task EmptyAcsResponseToInform_ProtocolError()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().End200Empty());
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("EMPTYIR")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.ProtocolError, s.Outcome);
    }

    // ---- upstream failures ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpstreamDown_ConnectionRefusedSocketError_RecordedOnExchange()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs, upstreamOverride: $"http://127.0.0.1:{DeadPort()}");
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("DOWN")));
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal(0, r.Content.Headers.ContentLength);
        Assert.True(r.Headers.ConnectionClose);
        var err = (await h.WaitForEndedAsync()).Exchanges.Single().Error!;
        Assert.Equal(UpstreamErrorKind.Connect, err.Kind);
        Assert.Equal("ConnectionRefused", err.SocketError);
    }

    [Fact]
    public async Task UpstreamDns_InvalidTld_502DnsOrSinkholeFallback()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { ConnectTimeoutSeconds = 2, RequestTimeoutSeconds = 3 },
            upstreamOverride: "http://nonexistent.invalid:7547");
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("DNS")));
        var s = await h.WaitForEndedAsync();
        var err = s.Exchanges.Single().Error!;
        if (err.Kind == UpstreamErrorKind.HttpStatus)
        {
            // Sandbox egress resolves every name and an intercepting HTTP proxy answers; the real Dns path cannot be exercised here.
            Assert.Equal(SessionOutcome.HttpError, s.Outcome);
            return;
        }
        Assert.Equal(SessionOutcome.UpstreamError, s.Outcome);
        Assert.Contains(err.Kind, new[] { UpstreamErrorKind.Dns, UpstreamErrorKind.Connect, UpstreamErrorKind.Timeout });
        Assert.Equal(err.Kind == UpstreamErrorKind.Timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway, r.StatusCode);
    }

    [Fact]
    public async Task UpstreamAcceptsButNeverAnswers_504Timeout()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var accepted = silent.AcceptTcpClientAsync();
            await using var acs = await FakeAcs.StartAsync();
            await using var h = await ProxyHarness.StartAsync(acs, s => s with { RequestTimeoutSeconds = 1 }, upstreamOverride: $"http://127.0.0.1:{port}");
            using var client = ProxyHarness.CpeClient();
            using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("SILENT")));
            Assert.Equal(HttpStatusCode.GatewayTimeout, r.StatusCode);
            var err = (await h.WaitForEndedAsync()).Exchanges.Single().Error!;
            Assert.Equal(UpstreamErrorKind.Timeout, err.Kind);
            Assert.Equal(RequestPhase.AwaitingResponse, err.Phase);
            (await accepted).Dispose();
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task UpstreamAbortsConnection_502Protocol()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().AbortConnection());
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("RST")));
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.UpstreamError, s.Outcome);
        Assert.Equal(UpstreamErrorKind.Protocol, s.Exchanges.Single().Error!.Kind);
    }

    [Fact]
    public async Task UpstreamTimeoutMidSession_SessionEndsUpstreamError_CpeGets504()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Delay(TimeSpan.FromSeconds(4)).End204());
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { RequestTimeoutSeconds = 1 });
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("MIDTO")))).EnsureSuccessStatusCode();
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.Equal(HttpStatusCode.GatewayTimeout, r2.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.UpstreamError, s.Outcome);
        Assert.Equal(2, s.Exchanges.Count);
        Assert.Null(s.Exchanges[0].Error);
        Assert.Equal(UpstreamErrorKind.Timeout, s.Exchanges[1].Error!.Kind);
    }

    // ---- auth --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Digest_ChallengeRecorded_WwwAuthenticateRelayedVerbatim()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Digest = ("cpe", "pw", "acs"));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("DIG")));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var www = r.Headers.WwwAuthenticate.ToString();
        Assert.StartsWith("Digest realm=\"acs\", qop=\"auth\", nonce=\"", www, StringComparison.Ordinal);
        Assert.Equal(1, h.Tracker.ActiveCount); // challenge alone does not end the session
        var s = h.Store.GetAll().Single();
        Assert.Contains("AuthChallenge", s.Exchanges.Single().Notes);
        Assert.Null(s.Exchanges.Single().Error); // 401 is not an error (ErrorClassifier.FromStatus)
    }

    [Fact]
    public async Task Digest_WrongPassword_TwoConsecutive401_AuthFailedSingleSession()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Digest = ("cpe", "pw", "acs"));
        await using var h = await ProxyHarness.StartAsync(acs);
        var handler = new SocketsHttpHandler { UseProxy = false, Credentials = new NetworkCredential("cpe", "WRONG"), PreAuthenticate = false };
        using var client = new HttpClient(handler);
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("BADPW")));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Single(h.Store.GetAll());
        Assert.Equal(SessionOutcome.AuthFailed, s.Outcome);
        Assert.Equal(2, s.Exchanges.Count);
        Assert.StartsWith("Digest ", s.Exchanges[1].Request.Headers.First(x => x.Key == "Authorization").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Basic_RelayedAndAccepted()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Basic = ("cpe", "pw"));
        await using var h = await ProxyHarness.StartAsync(acs);
        var handler = new SocketsHttpHandler { UseProxy = false, Credentials = new NetworkCredential("cpe", "pw"), PreAuthenticate = false };
        using var client = new HttpClient(handler);
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("BAS")))).EnsureSuccessStatusCode();
        using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.Equal(HttpStatusCode.NoContent, r2.StatusCode);
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        Assert.Single(h.Store.GetAll());
    }

    [Fact]
    public async Task Forbidden403_AuthFailed()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().RespondStatus(403));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("FORB")));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(SessionOutcome.AuthFailed, (await h.WaitForEndedAsync()).Outcome);
    }

    // ---- scenario 9: two CPEs concurrently, with cookies ----------------------------------------------------------------------

    [Fact]
    public async Task TwoCpesConcurrently_WithAcsCookies_NoCrossTalk()
    {
        await using var acs = await FakeAcs.StartAsync(o =>
        {
            o.SetCookie = "fakeacs-sid={n}; Path=/";
            o.Script = s => s.ExpectInform().RespondInformResponse()
                .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
                .ExpectResponse<GetParameterValuesResponseMessage>().End204()
                .ExpectEmpty().End204();
        });
        await using var h = await ProxyHarness.StartAsync(acs);
        using var gate = new SemaphoreSlim(0, 2);
        async Task RunCpe(string serial)
        {
            using var client = ProxyHarness.CpeClient(useCookies: true);
            (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform(serial)))).EnsureSuccessStatusCode();
            using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
            var id = ProxyHarness.IdOf(await r2.Content.ReadAsByteArrayAsync());
            (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Soap(new GetParameterValuesResponseMessage([new ParameterValue("X", serial)]), id)))).Dispose();
            (await client.SendAsync(ProxyHarness.Post(h.Url(), null))).Dispose();
        }
        await Task.WhenAll(RunCpe("C1"), RunCpe("C2"));
        await h.WaitForEndedAsync(2);
        Assert.Empty(acs.Violations);
        var all = h.Store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.All(all, s => Assert.Equal(SessionOutcome.Completed, s.Outcome));
        Assert.All(all, s => Assert.Equal(4, s.Exchanges.Count));
        foreach (var s in all)
        {
            var serial = s.DeviceId!.SerialNumber;
            // the GPVResponse recorded in this session carries this CPE's own serial
            Assert.Contains(serial, System.Text.Encoding.UTF8.GetString(s.Exchanges[2].Request.Body.Span), StringComparison.Ordinal);
        }
        // each FakeAcs session saw requests from only one cookie value
        Assert.All(acs.Sessions, fs => Assert.Single(fs.Requests.Skip(1).Select(r => r.Header("Cookie")).Distinct()));
    }

    // ---- defects (expected behavior; skipped) ---------------------------------------------------------------------------------

    [Fact(Skip = "BUG-01: ACS SOAP Fault to Inform leaves the proxy session InProgress until the idle sweep (Abandoned) instead of ending AcsFault")]
    public async Task AcsFaultToInform_Bug01_SessionEndsAcsFaultPromptly()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform()
            .Respond(new CwmpFault("Server", "CWMP fault", FaultCodes.AcsRetryRequest, "Retry request", [])));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("FLT")));
        // TR-069 §3.7.1.4/A.3.2.1: a CPE receiving a Fault to its Inform ends the session (and retries later). It sends nothing more.
        Assert.True(await TestUtil.WaitUntilAsync(() => h.Store.GetAll().Any(x => x.Ended is not null), 5000),
            "still " + h.Store.GetAll().Single().Outcome + " after 5 s");
        Assert.Equal(SessionOutcome.AcsFault, h.Store.GetAll().Single().Outcome);
    }

    [Fact]
    public async Task AcsFaultToInform_ActualBehavior_OpenPastGrace_ThenAbandonedNotAcsFault()
    {
        // Characterization of BUG-01 (passes today; flips when the bug is fixed).
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform()
            .Respond(new CwmpFault("Server", "CWMP fault", FaultCodes.AcsRetryRequest, "Retry request", [])));
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { SessionIdleTimeoutSeconds = 4 });
        using var client = ProxyHarness.CpeClient();
        using var r = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("FLT2")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await Task.Delay(CpeSessionTracker.CompletionGrace + TimeSpan.FromMilliseconds(500));
        h.Tracker.Sweep();
        Assert.Equal(1, h.Tracker.ActiveCount);
        Assert.Equal(SessionOutcome.InProgress, h.Store.GetAll().Single().Outcome);
        Assert.True(await TestUtil.WaitUntilAsync(() => h.Store.GetAll().Single().Ended is not null, 6000));
        Assert.Equal(SessionOutcome.Abandoned, h.Store.GetAll().Single().Outcome); // the ACS fault is lost from the outcome
    }

    [Fact(Skip = "BUG-02: a new Inform within the 2 s completion grace marks the previous, already-finished session Superseded instead of Completed")]
    public async Task NewInformDuringCompletionGrace_Bug02_PreviousSessionCompleted()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
            .ExpectResponse<GetParameterValuesResponseMessage>().End204());
        await using var h = await ProxyHarness.StartAsync(acs);
        async Task OneSession(HttpClient c)
        {
            (await c.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("GRACE")))).Dispose();
            using var r2 = await c.SendAsync(ProxyHarness.Post(h.Url(), null));
            var id = ProxyHarness.IdOf(await r2.Content.ReadAsByteArrayAsync());
            (await c.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Soap(new GetParameterValuesResponseMessage([]), id)))).Dispose();
        }
        using var client = ProxyHarness.CpeClient();
        await OneSession(client);           // ends in AwaitingFinalEmpty (no trailing empty POST, like the built-in simulator)
        await OneSession(client);           // user clicks "Send Inform now" again within 2 s
        var first = h.Store.GetAll().OrderBy(s => s.Started).First();
        Assert.Equal(SessionOutcome.Completed, first.Outcome);
    }

    [Fact]
    public async Task NewInformDuringCompletionGrace_ActualBehavior_PreviousSessionSuperseded()
    {
        // Characterization of BUG-02.
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
            .ExpectResponse<GetParameterValuesResponseMessage>().End204());
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        for (var i = 0; i < 2; i++)
        {
            (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("GRACE2")))).Dispose();
            using var r2 = await client.SendAsync(ProxyHarness.Post(h.Url(), null));
            var id = ProxyHarness.IdOf(await r2.Content.ReadAsByteArrayAsync());
            (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Soap(new GetParameterValuesResponseMessage([]), id)))).Dispose();
        }
        var first = h.Store.GetAll().OrderBy(s => s.Started).First();
        Assert.Equal(SessionOutcome.Superseded, first.Outcome);
    }
}
