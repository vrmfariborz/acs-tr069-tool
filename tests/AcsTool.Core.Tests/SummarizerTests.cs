using AcsTool.Core.Model;
using AcsTool.Core.Parsing;
using AcsTool.Testing;

namespace AcsTool.Core.Tests;

public class SummarizerTests
{
    private readonly CwmpParser _parser = new();
    private readonly MessageSummarizer _summ = new();

    [Theory]
    [InlineData("inform-boot-cwmp10.xml", "Inform 00D09E-SN123456789 [1 BOOT, M Reboot]", "Inform 00D09E-SN123456789 [1 BOOT, M Reboot] 8 params")]
    [InlineData("inform-bootstrap-cwmp12-tr181.xml", "Inform A1B2C3-CT0001 [0 BOOTSTRAP, 1 BOOT]", "Inform A1B2C3-CT0001 [0 BOOTSTRAP, 1 BOOT] 8 params")]
    [InlineData("informresponse-cwmp10.xml", "InformResponse", "InformResponse")]
    [InlineData("gpv-request.xml", "GPV ×2", "GPV ×2")]
    [InlineData("gpv-response.xml", "GPVResponse ×2", "GPVResponse ×2")]
    [InlineData("spv-request.xml", "SPV ×3 key=abc", "SPV ×3 key=abc")]
    [InlineData("spv-response.xml", "SPVResponse status=0", "SPVResponse status=0")]
    [InlineData("fault-9005.xml", "Fault 9005 Invalid parameter name", "Fault 9005 Invalid parameter name")]
    [InlineData("spv-fault.xml", "Fault 9003 Invalid arguments", "Fault 9003 Invalid arguments")]
    [InlineData("reboot.xml", "Reboot key=rb1", "Reboot key=rb1")]
    [InlineData("gpn-request.xml", "GPN InternetGatewayDevice. (next level)", "GPN InternetGatewayDevice. (next level)")]
    [InlineData("getrpcmethods.xml", "GetRPCMethods", "GetRPCMethods")]
    [InlineData("unknown-method.xml", "Unknown Upload", "Unknown Upload")]
    [InlineData("empty.txt", "Empty", "Empty")]
    [InlineData("whitespace.txt", "Empty", "Empty")]
    [InlineData("transfercomplete.xml", "TransferComplete key=dl-1", "TransferComplete key=dl-1")]
    [InlineData("download.xml", "Download 1 Firmware Upgrade Image", "Download 1 Firmware Upgrade Image")]
    public void Summarize_Fixture_TitleAndOneLine(string fixture, string title, string oneLine)
    {
        var s = _summ.Summarize(_parser.Parse(Fixtures.Load(fixture)));
        Assert.Equal(title, s.Title);
        Assert.Equal(oneLine, s.OneLine);
    }

    [Fact]
    public void Summarize_NotXml_IncludesLine()
    {
        var s = _summ.Summarize(_parser.Parse(Fixtures.Load("not-xml.txt")));
        Assert.StartsWith("Not XML (line ", s.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_Inform_FieldsStableAndFactsExtracted()
    {
        var s = _summ.Summarize(_parser.Parse(Fixtures.Load("inform-boot-cwmp10.xml")));
        Assert.Equal(new[] { "Method", "cwmp:ID", "Version", "DeviceId", "Events", "RetryCount", "MaxEnvelopes", "CurrentTime", "Params (8)" },
            s.Fields.Take(9).Select(f => f.Key));
        Assert.Equal("1001", s.CwmpId);
        Assert.Equal("00D09E-SN123456789", s.DeviceId!.Key);
        Assert.Equal(2, s.Events!.Count);
        Assert.Equal("1 BOOT; M Reboot (rb-20261028-01)", s.Fields.Single(f => f.Key == "Events").Value);
    }

    [Fact]
    public void Summarize_Fault_ExposesFault()
    {
        var s = _summ.Summarize(_parser.Parse(Fixtures.Load("spv-fault.xml")));
        Assert.Equal(CwmpMessageKind.Fault, s.Kind);
        Assert.Equal(9003u, s.Fault!.FaultCode);
        Assert.Contains(s.Fields, f => f.Key == "SPV fault" && f.Value.Contains("9008", StringComparison.Ordinal));
    }

    [Fact]
    public void Summarize_HugeGpvResponse_FieldsCappedWithMoreMarker()
    {
        var values = Enumerable.Range(0, 5000).Select(i => new ParameterValue($"Device.X.{i}.Value", new string('v', 50))).ToList();
        var body = new AcsTool.Core.Building.CwmpMessageBuilder().Build(new GetParameterValuesResponseMessage(values), AcsTool.Core.Cwmp.CwmpVersion.V1_0, new CwmpHeader("1"));
        var sum = _summ.Summarize(_parser.Parse(body));
        Assert.Equal("GPVResponse ×5000", sum.Title);
        Assert.Equal(MessageSummarizer.MaxListedItems, sum.Fields.Count(f => f.Key.StartsWith("Device.X.", StringComparison.Ordinal)));
        Assert.Single(sum.Fields, f => f.Key == "… and 4800 more");
        Assert.True(sum.Fields.Count < MessageSummarizer.MaxListedItems + 10);
    }
}

