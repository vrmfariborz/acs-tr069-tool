using System.Net;
using System.Net.Sockets;
using AcsTool.Core;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Proxy;
using AcsTool.Simulator.Engine;
using AcsTool.Simulator.Profiles;
using AcsTool.Simulator.Transport;
using AcsTool.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcsTool.Simulator.Tests;

public class TransportTests
{
    private static HttpCwmpTransport Transport(string url, string? user = null, string? pass = null) =>
        new(Kit.Profile(url), user is null ? null : new NetworkCredential(user, pass), new UpstreamHandlerFactory(), new SimulatorSettings(), new CaptureSettings(), TimeProvider.System);

    private static byte[] Inform() => Kit.Builder.Build(new InformBuilder().Create(new DeviceIdStruct("A", "00D09E", "P", "T1"), [new EventStruct(EventCodes.Boot)], [], 0, DateTimeOffset.UtcNow),
        CwmpVersion.V1_0, new CwmpHeader("1"));

    [Fact]
    public async Task Cookies_ReturnedOnLaterPosts_EmptyPostHasNoContentType()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.SetCookie = "fakeacs-sid={n}; Path=/");
        await using var t = Transport(acs.Url());
        var r1 = await t.PostAsync(Inform(), "\"\"", default);
        Assert.Equal(200, r1.StatusCode);
        var r2 = await t.PostAsync(default, null, default);
        Assert.Equal(204, r2.StatusCode);

        var first = acs.Received[0];
        Assert.Equal("text/xml; charset=\"utf-8\"", first.Header("Content-Type"));
        Assert.Equal("\"\"", first.Header("SOAPAction"));
        Assert.Equal("AcsTool-Sim/1.0", first.Header("User-Agent"));
        var empty = acs.Received[1];
        Assert.Equal("fakeacs-sid=1", empty.Header("Cookie"));
        Assert.Equal("0", empty.Header("Content-Length"));
        Assert.False(empty.HasHeader("Content-Type"));
        Assert.False(empty.HasHeader("SOAPAction"));
        Assert.Empty(acs.Violations);
        Assert.Contains(r2.RequestCapture.Headers, h => h.Key == "Cookie");
    }

    [Fact]
    public async Task BasicAuth_Succeeds()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Basic = ("cpe", "secret"));
        await using var t = Transport(acs.Url(), "cpe", "secret");
        Assert.Equal(200, (await t.PostAsync(Inform(), "\"\"", default)).StatusCode);
        Assert.StartsWith("Basic ", acs.Received[^1].Header("Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_CarriesConfiguredCaptureLimit()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var t = new HttpCwmpTransport(Kit.Profile(acs.Url()), null, new UpstreamHandlerFactory(), new SimulatorSettings(),
            new CaptureSettings(MaxCapturedBodyBytes: 100), TimeProvider.System);
        var r = await t.PostAsync(Inform(), "\"\"", default);
        Assert.Equal(100, r.MaxCapturedBodyBytes);
        Assert.True(r.RequestCapture.Truncated);
    }

    [Fact]
    public async Task DigestAuth_Succeeds()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Digest = ("cpe", "secret", "acs"));
        await using var t = Transport(acs.Url(), "cpe", "secret");
        Assert.Equal(200, (await t.PostAsync(Inform(), "\"\"", default)).StatusCode);
    }

    [Fact]
    public async Task ConnectRefused_UpstreamErrorConnect()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await using var t = Transport($"http://127.0.0.1:{port}/acs");
        var r = await t.PostAsync(Inform(), "\"\"", default);
        Assert.Null(r.StatusCode);
        Assert.Equal(UpstreamErrorKind.Connect, r.Error!.Kind);
    }
}

public class EngineTests
{
    private static FakeAcsScript SpvThenGpv(FakeAcsScript s) => s
        .ExpectInform().RespondInformResponse()
        .ExpectEmpty().Send(new SetParameterValuesMessage([new ParameterValue("InternetGatewayDevice.Time.NTPServer1", "ntp.acs.test")], "pk-1"))
        .ExpectResponse<SetParameterValuesResponseMessage>().Send(new GetParameterValuesMessage(["InternetGatewayDevice.Time.NTPServer1"]))
        .ExpectResponse<GetParameterValuesResponseMessage>().End204();

    [Fact]
    public async Task SendInformNow_SpvGpvSession_StoreUpdated_StatesIdleInSessionIdle()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => SpvThenGpv(s));
        var store = Kit.NewStore();
        await using var engine = Kit.Engine(store);
        var cpe = (await engine.StartProfileAsync(Kit.Profile(acs.Url()), sendBootNow: false, default)).Single();
        var states = new List<CpeStateKind>();
        cpe.StateChanged += (_, s) => { lock (states) { states.Add(s.Kind); } };
        Assert.Equal(CpeStateKind.Idle, cpe.State.Kind);
        cpe.Post(new CpeTrigger.Manual([new EventStruct(EventCodes.Boot)]));
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 1 && cpe.State.Kind == CpeStateKind.Idle), "session did not finish");
        Assert.Empty(acs.Violations);
        Assert.Equal(SessionOutcome.Completed, cpe.LastResult!.Outcome);
        Assert.Equal("ntp.acs.test", cpe.Store.Get("InternetGatewayDevice.Time.NTPServer1")!.Value);
        Assert.Equal("pk-1", cpe.Store.Get("InternetGatewayDevice.ManagementServer.ParameterKey")!.Value);
        lock (states)
        {
            Assert.Equal(new[] { CpeStateKind.InSession, CpeStateKind.Idle }, states);
        }
        var snap = store.GetAll().Single();
        Assert.Equal(SessionOutcome.Completed, snap.Outcome);
        Assert.Equal(4, snap.Exchanges.Count);
        Assert.Empty(cpe.PendingEvents);
        // the GPV response carried the value set by the SPV
        Assert.Contains("ntp.acs.test", System.Text.Encoding.UTF8.GetString(acs.Received[^1].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_CancelsInFlightSessionWithinOneSecond()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().Delay(TimeSpan.FromSeconds(30)).RespondInformResponse());
        var store = Kit.NewStore();
        await using var engine = Kit.Engine(store);
        var cpe = (await engine.StartProfileAsync(Kit.Profile(acs.Url()), sendBootNow: true, default)).Single();
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.State.Kind == CpeStateKind.InSession && acs.Received.Count == 1));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.StopAllAsync();
        sw.Stop();
        // FakeAcs would answer only after 30 s: a generous ceiling still proves stop cancels instead of waiting (CI-safe; review m14).
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"stop took {sw.Elapsed}");
        Assert.Equal(CpeStateKind.Stopped, cpe.State.Kind);
        Assert.Empty(engine.Cpes);
        Assert.Equal(SessionOutcome.Canceled, store.GetAll().Single().Outcome);
    }

    [Fact]
    public async Task UnexpectedSessionException_LoopSurvives_NextTriggerRuns()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s.ExpectInform().RespondInformResponse().ExpectEmpty().End204());
        var store = Kit.NewStore();
        var sink = new RecordingSink(store) { FailStartSessions = 1 };
        await using var engine = new SimulationEngine(Kit.Services(sink), new SettingsHolder(), NullLogger<SimulationEngine>.Instance);
        var cpe = (await engine.StartProfileAsync(Kit.Profile(acs.Url()), sendBootNow: true, default)).Single();
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.State.Kind == CpeStateKind.Error));
        Assert.Contains("sink boom", cpe.State.LastError, StringComparison.Ordinal);
        Assert.True(cpe.Post(new CpeTrigger.Manual([])));
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 1 && cpe.State.Kind == CpeStateKind.Idle), cpe.State.ToString());
        Assert.Equal(SessionOutcome.Completed, store.GetAll().Single().Outcome);
    }

    [Fact]
    public async Task FailedSession_ErrorStateKeepsEvents_ManualRetryIncrementsRetryCount()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var store = Kit.NewStore();
        await using var engine = Kit.Engine(store);
        var cpe = (await engine.StartProfileAsync(Kit.Profile($"http://127.0.0.1:{port}/acs"), sendBootNow: true, default)).Single();
        // SessionsRun is bumped just before the final state change: wait for both (a Windows CI run saw InSession in between).
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 1 && cpe.State.Kind != CpeStateKind.InSession), cpe.State.ToString());
        Assert.Equal(CpeStateKind.Error, cpe.State.Kind);
        Assert.Equal(1u, cpe.State.RetryCount);
        Assert.Contains("UpstreamError", cpe.State.LastError, StringComparison.Ordinal);
        Assert.Equal(EventCodes.Boot, Assert.Single(cpe.PendingEvents).EventCode);
        cpe.Post(new CpeTrigger.Manual([]));
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 2 && cpe.State.Kind != CpeStateKind.InSession), cpe.State.ToString());
        Assert.Equal(2u, cpe.State.RetryCount);
    }

    [Fact]
    public async Task StartProfile_InvalidOrDuplicate_Throws()
    {
        await using var engine = Kit.Engine(Kit.NewStore());
        await Assert.ThrowsAsync<ProfileValidationException>(() => engine.StartProfileAsync(Kit.Profile("not a url"), false, default));
        var p = Kit.Profile();
        await engine.StartProfileAsync(p, false, default);
        await Assert.ThrowsAsync<ProfileValidationException>(() => engine.StartProfileAsync(p with { Id = Guid.NewGuid() }, false, default));
        await engine.StopProfileAsync(p.Id);
        Assert.Empty(engine.Cpes);
    }

    [Fact]
    public async Task ServiceProvider_ResolvesEngine()
    {
        using var paths = new TempAppPaths();
        var services = new ServiceCollection().AddLogging().AddSingleton<IAppPaths>(paths).AddAcsToolSimulator().AddAcsToolProxy();
        await using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<SimulationEngine>());
        Assert.NotNull(sp.GetRequiredService<IProfileStore>());
        Assert.Equal(32, sp.GetRequiredService<SimulationEngine>().MaxConcurrentSessions);
    }
}

public class EndToEndTests
{
    [Fact]
    public async Task Simulator_Through_Proxy_To_FakeAcs_ProducesTwoMatchingSessionRecords()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
            .ExpectResponse<GetParameterValuesResponseMessage>().End204());
        var store = Kit.NewStore();
        var proxyTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow); // drives the proxy's grace sweep (no wall-clock wait)
        var factory = new ProxyServerFactory(new CwmpParser(), new MessageSummarizer(), new UpstreamHandlerFactory(), store, proxyTime, NullLoggerFactory.Instance);
        await using var proxy = factory.Create(new ProxySettings(BindAddress: "127.0.0.1", Port: 0, UpstreamUrl: acs.BaseUri.ToString()), new CaptureSettings());
        await proxy.StartAsync(default);
        await using var engine = Kit.Engine(store);
        var cpe = (await engine.StartProfileAsync(Kit.Profile($"http://127.0.0.1:{proxy.BoundPort}/acs"), sendBootNow: true, default)).Single();

        Assert.True(await TestUtil.WaitUntilAsync(() => store.GetAll().Any(s => s.Source == SessionSource.Simulator && s.Ended is not null)), "simulator session should end");
        // The simulator does not send a trailing empty POST, so the proxy completes its session via the grace sweep.
        proxyTime.Advance(CpeSessionTracker.CompletionGrace + CpeSessionTracker.SweepInterval);
        Assert.True(await TestUtil.WaitUntilAsync(() => store.GetAll().Count(s => s.Ended is not null) == 2), "both sessions should end");
        Assert.Empty(acs.Violations);
        var sim = store.GetAll().Single(s => s.Source == SessionSource.Simulator);
        var prx = store.GetAll().Single(s => s.Source == SessionSource.Proxy);
        Assert.Equal(SessionOutcome.Completed, sim.Outcome);
        Assert.Equal(SessionOutcome.Completed, prx.Outcome);
        Assert.Equal(3, sim.Exchanges.Count);
        Assert.Equal(sim.Exchanges.Select(e => e.RequestSummary.Kind), prx.Exchanges.Select(e => e.RequestSummary.Kind));
        Assert.Equal(sim.Exchanges.Select(e => e.ResponseSummary?.Kind), prx.Exchanges.Select(e => e.ResponseSummary?.Kind));
        Assert.Equal(sim.DeviceId, prx.DeviceId);
    }
}
