using System.Text;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Proxy;
using AcsTool.Simulator.Engine;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcsTool.Simulator.Tests;

/// <summary>
/// TesterBot, fix/doc-findings (b8d33bb), doc review (a): simulator outcome for SOAP Faults / auth statuses over real HTTP
/// (FakeAcs + HttpCwmpTransport), directly and through the proxy, so simulator and proxy can be compared.
/// </summary>
public class TesterDocFindingsTests
{
    private static string FaultXml(string id = "1") =>
        Encoding.UTF8.GetString(new CwmpMessageBuilder().Build(CwmpMessageBuilder.CreateFault(FaultCodes.AcsRetryRequest), CwmpVersion.V1_0, new CwmpHeader(id)));

    // A SOAP 1.1 Fault without the cwmp:Fault detail (some ACS stacks send this on internal errors).
    private const string PlainSoapFault = """
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body><soap:Fault><faultcode>soap:Server</faultcode><faultstring>Internal error</faultstring></soap:Fault></soap:Body>
        </soap:Envelope>
        """;

    public static TheoryData<string, int, string?, string?, SessionOutcome> InformAnswers => new()
    {
        { "500 Fault text/xml", 500, "fault", "text/xml; charset=\"utf-8\"", SessionOutcome.AcsFault },
        { "500 Fault text/html", 500, "fault", "text/html", SessionOutcome.AcsFault },
        { "500 Fault no content-type", 500, "fault", null, SessionOutcome.AcsFault },
        { "500 plain SOAP Fault (no cwmp detail)", 500, PlainSoapFault, "text/xml", SessionOutcome.AcsFault },
        { "500 empty body", 500, null, null, SessionOutcome.HttpError },
        { "500 HTML body", 500, "<html><body>Internal Server Error</body></html>", "text/html", SessionOutcome.HttpError },
        { "200 Fault", 200, "fault", "text/xml", SessionOutcome.AcsFault },
        { "407 Fault", 407, "fault", "text/xml", SessionOutcome.HttpError },
        { "407 empty", 407, null, null, SessionOutcome.HttpError },
    };

    private static void InformAnswer(FakeAcsScript s, int status, string? body, string? contentType) =>
        s.ExpectInform().RespondWith((_, r) => new FakeAcsResponse
        {
            Status = status,
            Body = body is null ? null : Encoding.UTF8.GetBytes(body == "fault" ? FaultXml(r.Parsed.Envelope?.Header.Id ?? "1") : body),
            Headers = contentType is null ? [] : [new("Content-Type", contentType)],
        });

    private static async Task<(SessionOutcome Outcome, IReadOnlyList<string> Notes, CpeState State)> RunDirect(Action<FakeAcsScript> script)
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = script);
        var store = Kit.NewStore();
        await using var engine = Kit.Engine(store);
        var cpe = (await engine.StartProfileAsync(Kit.Profile(acs.Url()), sendBootNow: true, default)).Single();
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 1 && cpe.State.Kind != CpeStateKind.InSession), "session did not finish");
        return (cpe.LastResult!.Outcome, store.Get(cpe.LastResult.SessionId)!.Notes, cpe.State); // state read before the engine stops the CPE
    }

    [Theory]
    [MemberData(nameof(InformAnswers))]
    public async Task InformAnswer_Direct(string name, int status, string? body, string? contentType, SessionOutcome expected)
    {
        var (outcome, notes, state) = await RunDirect(s => InformAnswer(s, status, body, contentType));
        Assert.True(expected == outcome, $"{name}: expected {expected}, got {outcome}; notes: {string.Join(" | ", notes)}");
        Assert.Equal(CpeStateKind.Error, state.Kind);
        Assert.Equal(1u, state.RetryCount);
    }

    [Fact]
    public async Task Inform500Fault_NoteShowsCodeAndString()
    {
        var (outcome, notes, _) = await RunDirect(s => InformAnswer(s, 500, "fault", "text/xml"));
        Assert.Equal(SessionOutcome.AcsFault, outcome);
        Assert.Contains(notes, n => n.StartsWith("ACS fault 8005", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inform401_FaultBody_NoCredentials_AuthFailed()
    {
        var (outcome, notes, _) = await RunDirect(s => InformAnswer(s, 401, "fault", "text/xml"));
        Assert.Equal(SessionOutcome.AuthFailed, outcome);
        Assert.Contains("ACS rejected the credentials (401)", notes);
    }

    [Fact]
    public async Task Inform401_DigestWrongPassword_AuthFailed()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Digest = ("cpe", "right", "acs"));
        var store = Kit.NewStore();
        await using var engine = Kit.Engine(store);
        var profile = Kit.Profile(acs.Url(), p => p with { Username = "cpe", PasswordProtected = "plain:wrong" });
        var cpe = (await engine.StartProfileAsync(profile, sendBootNow: true, default)).Single();
        Assert.True(await TestUtil.WaitUntilAsync(() => cpe.SessionsRun == 1 && cpe.State.Kind != CpeStateKind.InSession), "session did not finish");
        Assert.Equal(SessionOutcome.AuthFailed, cpe.LastResult!.Outcome);
    }

    [Theory]
    [InlineData(500, "fault", SessionOutcome.HttpError)]
    [InlineData(500, null, SessionOutcome.HttpError)]
    [InlineData(401, null, SessionOutcome.AuthFailed)]
    [InlineData(407, "fault", SessionOutcome.HttpError)]
    public async Task AfterInform_EmptyPostAnswered(int status, string? body, SessionOutcome expected)
    {
        var (outcome, notes, _) = await RunDirect(s =>
        {
            s.ExpectInform().RespondInformResponse();
            s.ExpectEmpty().RespondWith((_, _) => new FakeAcsResponse
            {
                Status = status,
                Body = body is null ? null : Encoding.UTF8.GetBytes(FaultXml()),
                Headers = body is null ? [] : [new("Content-Type", "text/xml")],
            });
        });
        Assert.True(expected == outcome, $"expected {expected}, got {outcome}; notes: {string.Join(" | ", notes)}");
    }

    [Theory]
    [MemberData(nameof(InformAnswers))]
    public async Task InformAnswer_ThroughProxy_SimulatorAndProxyAgree(string name, int status, string? body, string? contentType, SessionOutcome expected)
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => InformAnswer(s, status, body, contentType));
        var store = Kit.NewStore();
        var factory = new ProxyServerFactory(new CwmpParser(), new MessageSummarizer(), new UpstreamHandlerFactory(), store, TimeProvider.System, NullLoggerFactory.Instance);
        await using var proxy = factory.Create(new ProxySettings(BindAddress: "127.0.0.1", Port: 0, UpstreamUrl: acs.BaseUri.ToString()), new CaptureSettings());
        await proxy.StartAsync(default);
        await using var engine = Kit.Engine(store);
        Assert.Single(await engine.StartProfileAsync(Kit.Profile($"http://127.0.0.1:{proxy.BoundPort}/acs"), sendBootNow: true, default));
        Assert.True(await TestUtil.WaitUntilAsync(() => store.GetAll().Count(s => s.Ended is not null) == 2, timeoutMs: 15_000), $"{name}: both sessions should end");
        var sim = store.GetAll().Single(s => s.Source == SessionSource.Simulator);
        var prx = store.GetAll().Single(s => s.Source == SessionSource.Proxy);
        Assert.True(expected == sim.Outcome && expected == prx.Outcome,
            $"{name}: expected {expected}, simulator {sim.Outcome}, proxy {prx.Outcome}; sim notes: {string.Join(" | ", sim.Notes)}; proxy notes: {string.Join(" | ", prx.Notes)}");
    }
}
