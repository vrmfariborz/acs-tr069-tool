using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Sessions;
using AcsTool.Simulator.Profiles;
using AcsTool.Simulator.Session;

namespace AcsTool.Simulator.Tests;

public class ClientSessionTests
{
    private readonly InMemorySessionStore _store = Kit.NewStore();
    private readonly RecordingSink _sink;
    private readonly FakeTransport _t = new();

    public ClientSessionTests() => _sink = new RecordingSink(_store);

    private FakeTransport AcceptInform(bool? hold = null) =>
        _t.ThenMessage(_ => new InformResponseMessage(1), p => new CwmpHeader(p.Envelope!.Header.Id, HoldRequests: hold));

    private Task<ClientSessionResult> Run(Func<ClientSessionContext, ClientSessionContext>? f = null, CancellationToken ct = default)
    {
        var ctx = Kit.Context(_t, _sink);
        return new CwmpClientSession().RunAsync(f?.Invoke(ctx) ?? ctx, ct);
    }

    [Fact]
    public async Task HappyPath_InformRpcEmpty_Completed_SinkOrder()
    {
        AcceptInform()
            .ThenMessage(_ => new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo.SoftwareVersion"]), _ => new CwmpHeader("acs-7"))
            .ThenEmpty();
        var r = await Run();
        Assert.Equal(SessionOutcome.Completed, r.Outcome);
        Assert.True(r.InformAccepted);
        Assert.Equal(3, _t.Sent.Count);
        Assert.Equal(CwmpMessageKind.Inform, _t.Sent[0].Parsed.Kind);
        Assert.Equal("\"\"", _t.Sent[0].SoapAction);
        Assert.Empty(_t.Sent[1].Body);
        Assert.Null(_t.Sent[1].SoapAction);
        var gpvr = _t.Sent[2].Parsed;
        Assert.Equal(CwmpMessageKind.GetParameterValuesResponse, gpvr.Kind);
        Assert.Equal("acs-7", gpvr.Envelope!.Header.Id);
        Assert.Equal("1.0.0", ((GetParameterValuesResponseMessage)gpvr.Message!).Values.Single().Value);
        Assert.Equal(new[]
        {
            "Start", "Add:Inform", "Complete:InformResponse", "Add:Empty", "Complete:GetParameterValues",
            "Add:GetParameterValuesResponse", "Complete:Empty", "End:Completed",
        }, _sink.Calls);
        var snap = _store.Get(r.SessionId)!;
        Assert.Equal(SessionSource.Simulator, snap.Source);
        Assert.Equal(3, snap.Exchanges.Count);
        Assert.Equal(SessionOutcome.Completed, snap.Outcome);
    }

    public static TheoryData<CwmpMessage, CwmpMessageKind> Rpcs() => new()
    {
        { new GetRpcMethodsMessage(), CwmpMessageKind.GetRPCMethodsResponse },
        { new GetParameterNamesMessage("InternetGatewayDevice.", true), CwmpMessageKind.GetParameterNamesResponse },
        { new GetParameterValuesMessage(["InternetGatewayDevice.Time."]), CwmpMessageKind.GetParameterValuesResponse },
        { new SetParameterValuesMessage([new ParameterValue("InternetGatewayDevice.Time.NTPServer1", "a")], "k"), CwmpMessageKind.SetParameterValuesResponse },
        { new GetParameterValuesMessage(["InternetGatewayDevice.Nope"]), CwmpMessageKind.Fault },
        { new RebootMessage("ck"), CwmpMessageKind.Fault },
    };

    [Theory]
    [MemberData(nameof(Rpcs))]
    public async Task EachRpc_AnsweredWithEchoedId(CwmpMessage rpc, CwmpMessageKind expected)
    {
        AcceptInform().ThenMessage(_ => rpc, _ => new CwmpHeader("id-XYZ")).ThenEmpty();
        var r = await Run();
        Assert.Equal(SessionOutcome.Completed, r.Outcome);
        Assert.Equal(expected, _t.Sent[2].Parsed.Kind);
        Assert.Equal("id-XYZ", _t.Sent[2].Parsed.Envelope!.Header.Id);
    }

    [Fact]
    public async Task UnknownMethod_Upload_Fault9000WithEchoedId()
    {
        const string upload = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:cwmp="urn:dslforum-org:cwmp-1-0">
              <soap:Header><cwmp:ID soap:mustUnderstand="1">up-1</cwmp:ID></soap:Header>
              <soap:Body><cwmp:Upload><CommandKey>x</CommandKey><FileType>1 Vendor Configuration File</FileType><URL>http://x</URL></cwmp:Upload></soap:Body>
            </soap:Envelope>
            """;
        AcceptInform().Then(_ => FakeTransport.Ok(System.Text.Encoding.UTF8.GetBytes(upload))).ThenEmpty();
        var r = await Run();
        Assert.Equal(SessionOutcome.Completed, r.Outcome);
        var fault = (CwmpFault)_t.Sent[2].Parsed.Message!;
        Assert.Equal(FaultCodes.MethodNotSupported, fault.FaultCode);
        Assert.Equal("up-1", _t.Sent[2].Parsed.Envelope!.Header.Id);
    }

    private static TransferCompleteMessage Tc() => new("ck", 0, "", "0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z");

    [Fact]
    public async Task PendingTransferComplete_SentBeforeEmptyPost()
    {
        AcceptInform().ThenMessage(_ => new EmptyResponseMessage(CwmpMessageKind.TransferCompleteResponse)).ThenEmpty();
        var r = await Run(c => c with { PendingCpeRequests = [Tc()] });
        Assert.Equal(SessionOutcome.Completed, r.Outcome);
        Assert.Equal(CwmpMessageKind.TransferComplete, _t.Sent[1].Parsed.Kind);
        Assert.Equal(1, r.DeliveredCpeRequests);
    }

    [Fact]
    public async Task HoldRequests_SuppressesTransferComplete()
    {
        AcceptInform(hold: true).ThenEmpty();
        var r = await Run(c => c with { PendingCpeRequests = [Tc()] });
        Assert.Equal(SessionOutcome.Completed, r.Outcome);
        Assert.Equal(2, _t.Sent.Count);
        Assert.True(_t.Sent[1].Parsed.IsEmpty);
        Assert.Equal(0, r.DeliveredCpeRequests);
        Assert.Contains(_store.Get(r.SessionId)!.Notes, n => n.Contains("HoldRequests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FaultInformResponse_AcsFault()
    {
        _t.ThenMessage(_ => CwmpMessageBuilder.CreateFault(FaultCodes.AcsRetryRequest));
        var r = await Run();
        Assert.Equal(SessionOutcome.AcsFault, r.Outcome);
        Assert.False(r.InformAccepted);
    }

    [Fact]
    public async Task EmptyResponseToInform_ProtocolError()
    {
        _t.ThenEmpty();
        Assert.Equal(SessionOutcome.ProtocolError, (await Run()).Outcome);
    }

    [Fact]
    public async Task Status401_AuthFailed_And500_HttpError()
    {
        _t.ThenEmpty(401);
        Assert.Equal(SessionOutcome.AuthFailed, (await Run()).Outcome);
        _t.ThenEmpty(500);
        Assert.Equal(SessionOutcome.HttpError, (await Run()).Outcome);
    }

    [Fact]
    public async Task MessageCap_ProtocolError()
    {
        AcceptInform();
        _t.Fallback = p => FakeTransport.Ok(Kit.Builder.Build(new GetRpcMethodsMessage(), CwmpVersion.V1_0, new CwmpHeader("loop")));
        var r = await Run(c => c with { MaxMessages = 10 });
        Assert.Equal(SessionOutcome.ProtocolError, r.Outcome);
        Assert.Equal(10, _t.Sent.Count);
    }

    [Fact]
    public async Task RequestTimeout_UpstreamErrorTimeout()
    {
        _t.Then(_ => FakeTransport.Error(UpstreamErrorKind.Timeout, RequestPhase.AwaitingResponse));
        var r = await Run();
        Assert.Equal(SessionOutcome.UpstreamError, r.Outcome);
        Assert.Equal(UpstreamErrorKind.Timeout, r.Error!.Kind);
        Assert.Equal(UpstreamErrorKind.Timeout, _store.Get(r.SessionId)!.LastError!.Kind);
    }

    [Fact]
    public async Task InformResponseIdMismatch_NoteOnly()
    {
        _t.ThenMessage(_ => new InformResponseMessage(1), _ => new CwmpHeader("other")).ThenEmpty();
        var r = await Run();
        Assert.Equal(SessionOutcome.Completed, r.Outcome);
        Assert.Contains(_store.Get(r.SessionId)!.Notes, n => n.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RpcBehaviorOverrides_FaultAndNoResponse()
    {
        AcceptInform().ThenMessage(_ => new GetRpcMethodsMessage()).ThenEmpty();
        var profile = Kit.Profile(configure: p => p with
        {
            RpcBehaviors = new Dictionary<string, RpcBehavior> { ["GetRPCMethods"] = new(RpcBehaviorKind.Fault, 9004) },
        });
        var r = await Run(c => c with { Profile = profile });
        Assert.Equal(9004u, ((CwmpFault)_t.Sent[2].Parsed.Message!).FaultCode);
        Assert.Equal(SessionOutcome.Completed, r.Outcome);

        AcceptInform().ThenMessage(_ => new GetRpcMethodsMessage());
        var hang = Kit.Profile(configure: p => p with
        {
            RpcBehaviors = new Dictionary<string, RpcBehavior> { ["GetRPCMethods"] = new(RpcBehaviorKind.NoResponse, DelaySeconds: 0) },
        });
        var r2 = await Run(c => c with { Profile = hang });
        Assert.Equal(SessionOutcome.Canceled, r2.Outcome);
        Assert.Contains("simulated hang", _store.Get(r2.SessionId)!.Notes);
    }

    [Fact]
    public async Task Canceled_RecordedAsCanceled()
    {
        using var cts = new CancellationTokenSource();
        AcceptInform().Then(_ =>
        {
            cts.Cancel();
            return FakeTransport.Ok([], 204);
        });
        var r = await Run(ct: cts.Token);
        Assert.Equal(SessionOutcome.Canceled, r.Outcome);
        Assert.Equal(SessionOutcome.Canceled, _store.Get(r.SessionId)!.Outcome);
    }
}

public class EventQueueTests
{
    [Fact]
    public void Dedupes_Orders_KeepsCommandKeys()
    {
        var q = new PendingEventQueue();
        q.Add([new EventStruct("X ACME Custom"), new EventStruct(EventCodes.MReboot, "a"), new EventStruct(EventCodes.Boot)]);
        q.Add([new EventStruct(EventCodes.Boot), new EventStruct(EventCodes.MReboot, "b"), new EventStruct(EventCodes.Bootstrap), new EventStruct(EventCodes.MReboot, "a")]);
        Assert.Equal(new[] { "0 BOOTSTRAP", "1 BOOT", "M Reboot/a", "M Reboot/b", "X ACME Custom" },
            q.Snapshot().Select(e => e.CommandKey.Length > 0 ? $"{e.EventCode}/{e.CommandKey}" : e.EventCode));
    }

    [Fact]
    public void Remove_KeepsBootstrapUnlessCompleted()
    {
        var q = new PendingEventQueue();
        q.Add([new EventStruct(EventCodes.Bootstrap), new EventStruct(EventCodes.Boot)]);
        q.Remove(q.Snapshot(), sessionCompleted: false);
        Assert.Equal(EventCodes.Bootstrap, Assert.Single(q.Snapshot()).EventCode);
        q.Remove(q.Snapshot(), sessionCompleted: true);
        Assert.Equal(0, q.Count);
    }
}
