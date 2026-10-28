using System.Text;
using System.Text.Json;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Parsing;
using AcsTool.Testing;

namespace AcsTool.Core.Tests;

public class BuilderTests
{
    private readonly CwmpMessageBuilder _builder = new();
    private readonly CwmpParser _parser = new();

    internal static InformMessage SampleInform() => new InformBuilder().Create(
        new DeviceIdStruct("Acme Networks", "00D09E", "IGD", "SN123456789"),
        [new EventStruct("1 BOOT"), new EventStruct("M Reboot", "rb-20261028-01")],
        [
            new ParameterValue("InternetGatewayDevice.DeviceSummary", "InternetGatewayDevice:1.4[](Baseline:2)"),
            new ParameterValue("InternetGatewayDevice.DeviceInfo.SoftwareVersion", "FW-2.3.4"),
            new ParameterValue("InternetGatewayDevice.ManagementServer.PeriodicInformInterval", "300", "xsd:unsignedInt"),
        ],
        0, new DateTimeOffset(2026, 10, 28, 23, 10, 4, TimeSpan.FromHours(3.5)));

    public static IEnumerable<CwmpMessage> AllMessages()
    {
        yield return SampleInform();
        yield return new InformResponseMessage(1);
        yield return new GetRpcMethodsMessage();
        yield return new GetRpcMethodsResponseMessage(["GetRPCMethods", "GetParameterValues"]);
        yield return new GetParameterNamesMessage("InternetGatewayDevice.", true);
        yield return new GetParameterNamesResponseMessage([new ParameterInfo("InternetGatewayDevice.DeviceInfo.", false), new ParameterInfo("InternetGatewayDevice.X.Y", true)]);
        yield return new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo.SoftwareVersion", "InternetGatewayDevice.ManagementServer."]);
        yield return new GetParameterValuesResponseMessage([new ParameterValue("A.B", "1", "xsd:unsignedInt"), new ParameterValue("A.C", "x<&>\"'y")]);
        yield return new SetParameterValuesMessage([new ParameterValue("A.B", "true", "xsd:boolean")], "key-1");
        yield return new SetParameterValuesResponseMessage(0);
        yield return new GetParameterAttributesMessage(["A.B"]);
        yield return new GetParameterAttributesResponseMessage([new ParameterAttribute("A.B", 2, ["Subscriber"])]);
        yield return new SetParameterAttributesMessage([new SetParameterAttributeStruct("A.B", true, 1, false, [])]);
        yield return new EmptyResponseMessage(CwmpMessageKind.SetParameterAttributesResponse);
        yield return new AddObjectMessage("A.Table.", "k");
        yield return new AddObjectResponseMessage(3, 0);
        yield return new DeleteObjectMessage("A.Table.3.", "k");
        yield return new DeleteObjectResponseMessage(0);
        yield return new RebootMessage("rb1");
        yield return new EmptyResponseMessage(CwmpMessageKind.RebootResponse);
        yield return new FactoryResetMessage();
        yield return new EmptyResponseMessage(CwmpMessageKind.FactoryResetResponse);
        yield return new DownloadMessage("dl", "1 Firmware Upgrade Image", "http://h/f?a=1&b=2", "u", "p", 123456789012, "f.bin", 5, "", "");
        yield return new DownloadResponseMessage(1, "0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z");
        yield return new ScheduleInformMessage(30, "si");
        yield return new EmptyResponseMessage(CwmpMessageKind.ScheduleInformResponse);
        yield return new TransferCompleteMessage("dl", 9010, "Download failure", "2026-10-28T20:00:00Z", "2026-10-28T20:00:05Z");
        yield return new EmptyResponseMessage(CwmpMessageKind.TransferCompleteResponse);
        yield return CwmpMessageBuilder.CreateFault(9003, null, [new SetParameterValuesFaultItem("A.B", 9008, "Attempt to set a non-writable parameter")]);
    }

    public static TheoryData<int, CwmpVersion> Cases()
    {
        var data = new TheoryData<int, CwmpVersion>();
        var count = AllMessages().Count();
        foreach (var v in new[] { CwmpVersion.V1_0, CwmpVersion.V1_1, CwmpVersion.V1_2, CwmpVersion.V1_3, CwmpVersion.V1_4 })
        {
            for (var i = 0; i < count; i++)
            {
                data.Add(i, v);
            }
        }
        return data;
    }

    private static string Json(CwmpMessage m) => JsonSerializer.Serialize(m, m.GetType());

    [Theory]
    [MemberData(nameof(Cases))]
    public void Build_ThenParse_RoundTripsEveryMessageAndVersion(int index, CwmpVersion version)
    {
        var msg = AllMessages().ElementAt(index);
        var header = new CwmpHeader("id-" + index, HoldRequests: index % 2 == 0 ? false : null);
        var bytes = _builder.Build(msg, version, header);
        var r = _parser.Parse(bytes);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
        Assert.Equal(version, r.Envelope!.Version);
        Assert.Equal(header, r.Envelope.Header);
        Assert.Equal(msg.KindOf(), r.Kind);
        Assert.Equal(Json(msg), Json(r.Message!));
    }

    [Fact]
    public void Build_Inform_MatchesGoldenFile()
    {
        var xml = _builder.BuildString(SampleInform(), CwmpVersion.V1_0, new CwmpHeader("1001"));
        var golden = Fixtures.LoadString("expected/inform-built-cwmp10.xml");
        Assert.Equal(golden, xml);
    }

    [Fact]
    public void Build_Output_IsUtf8WithoutBomAndDeclaresNamespaces()
    {
        var bytes = _builder.Build(new InformResponseMessage(1), CwmpVersion.V1_0, new CwmpHeader("1"));
        Assert.NotEqual(0xEF, bytes[0]);
        var s = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", s, StringComparison.Ordinal);
        foreach (var ns in new[] { "xmlns:soap-env=", "xmlns:soap-enc=", "xmlns:xsd=", "xmlns:xsi=", "xmlns:cwmp=\"urn:dslforum-org:cwmp-1-0\"" })
        {
            Assert.Contains(ns, s, StringComparison.Ordinal);
        }
        Assert.Contains("<cwmp:ID soap-env:mustUnderstand=\"1\">1</cwmp:ID>", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EscapesValues()
    {
        var s = _builder.BuildString(new GetParameterValuesResponseMessage([new ParameterValue("A", "<&>\"'")]), CwmpVersion.V1_0, new CwmpHeader("1"));
        Assert.Contains("&lt;&amp;&gt;", s, StringComparison.Ordinal);
        Assert.DoesNotContain("<&>", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Arrays_CarryArrayTypeCounts()
    {
        var s = _builder.BuildString(SampleInform(), CwmpVersion.V1_0, new CwmpHeader("1"));
        Assert.Contains("soap-enc:arrayType=\"cwmp:EventStruct[2]\"", s, StringComparison.Ordinal);
        Assert.Contains("soap-enc:arrayType=\"cwmp:ParameterValueStruct[3]\"", s, StringComparison.Ordinal);
        Assert.Contains("<CurrentTime>2026-10-28T23:10:04+03:30</CurrentTime>", s, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(9005u, "Client")]
    [InlineData(9002u, "Server")]
    [InlineData(9000u, "Server")]
    public void BuildFault_FaultCode_ClientOrServer(uint code, string soapCode)
    {
        var r = _parser.Parse(_builder.BuildFault(code, FaultCodes.DefaultString(code), CwmpVersion.V1_0, "abc"));
        var f = Assert.IsType<CwmpFault>(r.Message);
        Assert.Equal(soapCode, f.SoapFaultCode);
        Assert.Equal(code, f.FaultCode);
        Assert.Equal("abc", r.Envelope!.Header.Id);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var a = _builder.Build(SampleInform(), CwmpVersion.V1_2, new CwmpHeader("7"));
        var b = _builder.Build(SampleInform(), CwmpVersion.V1_2, new CwmpHeader("7"));
        Assert.Equal(a, b);
    }
}
