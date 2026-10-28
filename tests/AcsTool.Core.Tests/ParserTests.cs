using System.Diagnostics;
using System.Text;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Parsing;
using AcsTool.Testing;

namespace AcsTool.Core.Tests;

public class ParserTests
{
    private readonly CwmpParser _parser = new();

    public static TheoryData<string, CwmpMessageKind> FixtureKinds => new()
    {
        { "inform-boot-cwmp10.xml", CwmpMessageKind.Inform },
        { "inform-bootstrap-cwmp12-tr181.xml", CwmpMessageKind.Inform },
        { "inform-periodic-unqualified-noprefix.xml", CwmpMessageKind.Inform },
        { "inform-nonstandard-soapns.xml", CwmpMessageKind.Inform },
        { "inform-arraytype-mismatch.xml", CwmpMessageKind.Inform },
        { "informresponse-cwmp10.xml", CwmpMessageKind.InformResponse },
        { "gpv-request.xml", CwmpMessageKind.GetParameterValues },
        { "gpv-response.xml", CwmpMessageKind.GetParameterValuesResponse },
        { "spv-request.xml", CwmpMessageKind.SetParameterValues },
        { "spv-response.xml", CwmpMessageKind.SetParameterValuesResponse },
        { "spv-fault.xml", CwmpMessageKind.Fault },
        { "fault-9005.xml", CwmpMessageKind.Fault },
        { "reboot.xml", CwmpMessageKind.Reboot },
        { "download.xml", CwmpMessageKind.Download },
        { "transfercomplete.xml", CwmpMessageKind.TransferComplete },
        { "gpn-request.xml", CwmpMessageKind.GetParameterNames },
        { "getrpcmethods.xml", CwmpMessageKind.GetRPCMethods },
        { "unknown-method.xml", CwmpMessageKind.Unknown },
        { "not-xml.txt", CwmpMessageKind.NotXml },
        { "xxe-attempt.xml", CwmpMessageKind.NotXml },
        { "empty.txt", CwmpMessageKind.Empty },
        { "whitespace.txt", CwmpMessageKind.Empty },
        { "utf8-bom-inform.xml", CwmpMessageKind.Inform },
        { "utf16-inform.xml", CwmpMessageKind.Inform },
    };

    [Theory]
    [MemberData(nameof(FixtureKinds))]
    public void Parse_Fixture_ExpectedKind(string fixture, CwmpMessageKind kind)
    {
        var r = _parser.Parse(Fixtures.Load(fixture));
        Assert.Equal(kind, r.Kind);
        if (kind is not (CwmpMessageKind.NotXml or CwmpMessageKind.Empty or CwmpMessageKind.Unknown))
        {
            Assert.NotNull(r.Envelope!.Message);
            Assert.Empty(r.Errors);
        }
    }

    [Fact]
    public void Parse_InformBoot_AllFields()
    {
        var r = _parser.Parse(Fixtures.Load("inform-boot-cwmp10.xml"));
        var env = r.Envelope!;
        Assert.Equal(CwmpVersion.V1_0, env.Version);
        Assert.Equal("1001", env.Header.Id);
        Assert.True(env.Header.IdMustUnderstand);
        var inf = Assert.IsType<InformMessage>(env.Message);
        Assert.Equal("00D09E-SN123456789", inf.DeviceId.Key);
        Assert.Equal("Acme Networks", inf.DeviceId.Manufacturer);
        Assert.Equal(new[] { new EventStruct("1 BOOT", ""), new EventStruct("M Reboot", "rb-20261028-01") }, inf.Events);
        Assert.Equal(8, inf.Parameters.Count);
        Assert.Equal(1u, inf.MaxEnvelopes);
        Assert.Equal(0u, inf.RetryCount);
        Assert.Equal("2026-10-28T23:10:04+03:30", inf.CurrentTime);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 23, 10, 4, TimeSpan.FromHours(3.5)), inf.CurrentTimeParsed);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Parse_Tr181Bootstrap_Cwmp12()
    {
        var r = _parser.Parse(Fixtures.Load("inform-bootstrap-cwmp12-tr181.xml"));
        Assert.Equal(CwmpVersion.V1_2, r.Envelope!.Version);
        var inf = Assert.IsType<InformMessage>(r.Message);
        Assert.Equal("0 BOOTSTRAP", inf.Events[0].EventCode);
        Assert.Equal(2u, inf.RetryCount);
        Assert.Contains(inf.Parameters, p => p.Name == "Device.ManagementServer.AliasBasedAddressing" && p.XsdType == "xsd:boolean");
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Parse_DefaultNamespaceStyle_DetectsVersionAndWarnsMissingTypes()
    {
        var r = _parser.Parse(Fixtures.Load("inform-periodic-unqualified-noprefix.xml"));
        Assert.Equal(CwmpVersion.V1_1, r.Envelope!.Version);
        Assert.Equal("42", r.Envelope.Header.Id);
        Assert.False(r.Envelope.Header.IdMustUnderstand);
        var inf = Assert.IsType<InformMessage>(r.Message);
        Assert.Equal("112233-NP-9", inf.DeviceId.Key);
        Assert.Equal("xsd:string", inf.Parameters[0].XsdType);
        Assert.Contains(r.Warnings, w => w.Contains("xsi:type", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_Soap12Envelope_WarnsExplicitly()
    {
        var r = _parser.Parse(Fixtures.Load("inform-nonstandard-soapns.xml"));
        Assert.Equal(CwmpMessageKind.Inform, r.Kind);
        Assert.Contains(r.Warnings, w => w.Contains("SOAP 1.2", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ArrayTypeMismatch_WarnsForEachArrayAndBadTime()
    {
        var r = _parser.Parse(Fixtures.Load("inform-arraytype-mismatch.xml"));
        var inf = Assert.IsType<InformMessage>(r.Message);
        Assert.Single(inf.Events);
        Assert.Equal(2, inf.Parameters.Count);
        Assert.Contains(r.Warnings, w => w.StartsWith("Event: arrayType declares 3", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.StartsWith("ParameterList: arrayType declares 1", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("not-a-time", StringComparison.Ordinal));
        Assert.Null(inf.CurrentTimeParsed);
    }

    [Fact]
    public void Parse_SpvFault_FaultDetailAndItems()
    {
        var r = _parser.Parse(Fixtures.Load("spv-fault.xml"));
        var f = Assert.IsType<CwmpFault>(r.Message);
        Assert.Equal(CwmpVersion.V1_0, r.Envelope!.Version);
        Assert.Equal("Client", f.SoapFaultCode);
        Assert.Equal(9003u, f.FaultCode);
        var item = Assert.Single(f.SpvFaults);
        Assert.Equal(9008u, item.FaultCode);
        Assert.Equal("InternetGatewayDevice.DeviceInfo.SoftwareVersion", item.ParameterName);
    }

    [Fact]
    public void Parse_SpvRequest_UnescapesValues()
    {
        var spv = Assert.IsType<SetParameterValuesMessage>(_parser.Parse(Fixtures.Load("spv-request.xml")).Message);
        Assert.Equal("abc", spv.ParameterKey);
        Assert.Equal("Lab & Test <5G>", spv.Values[2].Value);
        Assert.Equal("xsd:unsignedInt", spv.Values[0].XsdType);
    }

    [Fact]
    public void Parse_GpvRequest_NamesIncludingPartialPath()
    {
        var gpv = Assert.IsType<GetParameterValuesMessage>(_parser.Parse(Fixtures.Load("gpv-request.xml")).Message);
        Assert.Equal(new[] { "InternetGatewayDevice.DeviceInfo.SoftwareVersion", "InternetGatewayDevice.ManagementServer." }, gpv.Names);
    }

    [Fact]
    public void Parse_Download_AllFields()
    {
        var dl = Assert.IsType<DownloadMessage>(_parser.Parse(Fixtures.Load("download.xml")).Message);
        Assert.Equal("1 Firmware Upgrade Image", dl.FileType);
        Assert.Equal(1048576ul, dl.FileSize);
        Assert.Equal("http://files.example.com/fw.bin", dl.Url);
    }

    [Fact]
    public void Parse_UnknownMethod_KeepsLocalNameAndElement()
    {
        var r = _parser.Parse(Fixtures.Load("unknown-method.xml"));
        Assert.Equal("Upload", r.Envelope!.MethodLocalName);
        Assert.NotNull(r.Envelope.BodyElement);
        Assert.Null(r.Envelope.Message);
        Assert.Equal("acs-up", r.Envelope.Header.Id);
    }

    [Fact]
    public void Parse_Xxe_NoExternalResolution()
    {
        var r = _parser.Parse(Fixtures.Load("xxe-attempt.xml"));
        Assert.Equal(CwmpMessageKind.NotXml, r.Kind);
        Assert.NotEmpty(r.Errors);
        Assert.DoesNotContain(r.Errors, e => e.Contains("root:", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NotXml_ReportsLine()
    {
        var r = _parser.Parse(Fixtures.Load("not-xml.txt"));
        Assert.Equal(CwmpMessageKind.NotXml, r.Kind);
        Assert.NotNull(r.ErrorLine);
    }

    [Fact]
    public void Parse_NotSoap_WhenRootIsNotEnvelope()
    {
        var r = _parser.Parse(Encoding.UTF8.GetBytes("<html><body/></html>"));
        Assert.Equal(CwmpMessageKind.NotSoap, r.Kind);
    }

    [Fact]
    public void Parse_HoldRequestsHeader_Extracted()
    {
        var xml = new CwmpMessageBuilder().BuildString(new InformResponseMessage(1), CwmpVersion.V1_0, new CwmpHeader("x", HoldRequests: true));
        var r = _parser.Parse(Encoding.UTF8.GetBytes(xml));
        Assert.True(r.Envelope!.Header.HoldRequests);
    }

    [Fact]
    public void Parse_CharsetDisagreement_IsWarningOnly()
    {
        var r = _parser.Parse(Fixtures.Load("inform-boot-cwmp10.xml"), "text/xml; charset=\"iso-8859-1\"");
        Assert.Equal(CwmpMessageKind.Inform, r.Kind);
        Assert.Contains(r.Warnings, w => w.Contains("charset", StringComparison.Ordinal));
        var ok = _parser.Parse(Fixtures.Load("inform-boot-cwmp10.xml"), "text/xml; charset=\"utf-8\"");
        Assert.Empty(ok.Warnings);
    }

    [Fact]
    public void Parse_MultipleBodyElements_UsesFirstWithWarning()
    {
        var xml = Fixtures.LoadString("reboot.xml").Replace("</cwmp:Reboot>", "</cwmp:Reboot><cwmp:FactoryReset/>", StringComparison.Ordinal);
        var r = _parser.Parse(Encoding.UTF8.GetBytes(xml));
        Assert.Equal(CwmpMessageKind.Reboot, r.Kind);
        Assert.Contains("multiple body elements", r.Warnings);
    }

    [Fact]
    public void Parse_RandomBytes_NeverThrows()
    {
        var rnd = new Random(12345);
        var seed = Fixtures.Load("inform-boot-cwmp10.xml");
        for (var i = 0; i < 1000; i++)
        {
            byte[] data;
            if (i % 2 == 0)
            {
                data = new byte[rnd.Next(0, 512)];
                rnd.NextBytes(data);
            }
            else
            {
                data = (byte[])seed.Clone();
                for (var j = 0; j < 10; j++)
                {
                    data[rnd.Next(data.Length)] = (byte)rnd.Next(256);
                }
            }
            var r = _parser.Parse(data);
            Assert.NotNull(r);
        }
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Parse_LargeInform_Under50ms()
    {
        var ps = Enumerable.Range(0, 5000).Select(i => new ParameterValue($"InternetGatewayDevice.X_Vendor.Table.{i}.SomeLongParameterNameForSize", new string('v', 120))).ToList();
        var inf = new InformBuilder().Create(new DeviceIdStruct("M", "00D09E", "P", "S"), [new EventStruct("1 BOOT")], ps, 0, DateTimeOffset.UnixEpoch);
        var bytes = new CwmpMessageBuilder().Build(inf, CwmpVersion.V1_0, new CwmpHeader("1"));
        Assert.True(bytes.Length > 1_000_000);
        _parser.Parse(bytes); // warm-up
        // Best of 5: robust against GC pauses and CPU contention from other test assemblies running in parallel.
        var best = long.MaxValue;
        ParseResult? r = null;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            r = _parser.Parse(bytes);
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        Assert.Equal(5000, ((InformMessage)r!.Message!).Parameters.Count);
        Assert.True(best < 50, $"best of 5 took {best} ms");
    }
}
