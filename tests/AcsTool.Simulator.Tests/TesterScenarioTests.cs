using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Proxy;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcsTool.Simulator.Tests;

/// <summary>TesterBot M0–M4: plan §5.1 scenario 11 as an integration test (real HTTP to FakeAcs), direct and through the proxy.</summary>
public class TesterScenarioTests
{
    private const string Upload = """
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:cwmp="urn:dslforum-org:cwmp-1-0">
          <soap:Header><cwmp:ID soap:mustUnderstand="1">up-7</cwmp:ID></soap:Header>
          <soap:Body><cwmp:Upload><CommandKey>x</CommandKey><FileType>1 Vendor Configuration File</FileType><URL>http://x</URL></cwmp:Upload></soap:Body>
        </soap:Envelope>
        """;

    private static void UnknownThenGpv(FakeAcsScript s) => s
        .ExpectInform().RespondInformResponse()
        .ExpectEmpty().RespondRaw(200, Upload, "text/xml; charset=\"utf-8\"")
        .ExpectFault(FaultCodes.MethodNotSupported).Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo.SoftwareVersion"]))
        .ExpectResponse<GetParameterValuesResponseMessage>().End204();

    [Fact]
    public async Task Scenario11_UnknownRpc_Fault9000EchoedId_SessionContinuesTo204()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = UnknownThenGpv);
        var store = Kit.NewStore();
        await using var engine = Kit.Engine(store);
        var cpe = (await engine.StartProfileAsync(Kit.Profile(acs.Url()), sendBootNow: true, default)).Single();
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 1), "session did not finish");
        Assert.Empty(acs.Violations);
        Assert.Equal(SessionOutcome.Completed, cpe.LastResult!.Outcome);
        var fault = acs.Received[2];
        Assert.Equal(CwmpMessageKind.Fault, fault.Kind);
        Assert.Equal("up-7", fault.Parsed.Envelope!.Header.Id);
        Assert.Equal(FaultCodes.MethodNotSupported, ((CwmpFault)fault.Parsed.Message!).FaultCode);
        Assert.Equal(CwmpMessageKind.GetParameterValuesResponse, acs.Received[3].Kind);
    }

    [Fact]
    public async Task Scenario11_ThroughProxy_ProxyRecordsCpeFaultOutcome()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = UnknownThenGpv);
        var store = Kit.NewStore();
        var factory = new ProxyServerFactory(new CwmpParser(), new MessageSummarizer(), new UpstreamHandlerFactory(), store, TimeProvider.System, NullLoggerFactory.Instance);
        await using var proxy = factory.Create(new ProxySettings(BindAddress: "127.0.0.1", Port: 0, UpstreamUrl: acs.BaseUri.ToString()), new CaptureSettings());
        await proxy.StartAsync(default);
        await using var engine = Kit.Engine(store);
        Assert.Single(await engine.StartProfileAsync(Kit.Profile($"http://127.0.0.1:{proxy.BoundPort}/acs"), sendBootNow: true, default));
        Assert.True(await TestUtil.WaitUntilAsync(() => store.GetAll().Count(s => s.Ended is not null) == 2, timeoutMs: 15_000), "both sessions should end");
        Assert.Empty(acs.Violations);
        var sim = store.GetAll().Single(s => s.Source == SessionSource.Simulator);
        var prx = store.GetAll().Single(s => s.Source == SessionSource.Proxy);
        Assert.Equal(SessionOutcome.Completed, sim.Outcome);
        // the proxy saw a CPE Fault during the session, so per §3.6.4 its outcome is CpeFault
        Assert.Equal(SessionOutcome.CpeFault, prx.Outcome);
        Assert.Equal(sim.Exchanges.Select(e => e.RequestSummary.Kind), prx.Exchanges.Select(e => e.RequestSummary.Kind));
    }
}
