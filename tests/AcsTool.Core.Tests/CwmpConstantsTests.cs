using AcsTool.Core.Cwmp;

namespace AcsTool.Core.Tests;

public class CwmpConstantsTests
{
    [Theory]
    [InlineData("urn:dslforum-org:cwmp-1-0", CwmpVersion.V1_0)]
    [InlineData("urn:dslforum-org:cwmp-1-1", CwmpVersion.V1_1)]
    [InlineData("urn:dslforum-org:cwmp-1-2", CwmpVersion.V1_2)]
    [InlineData("urn:dslforum-org:cwmp-1-3", CwmpVersion.V1_3)]
    [InlineData("urn:dslforum-org:cwmp-1-4", CwmpVersion.V1_4)]
    [InlineData("urn:dslforum-org:cwmp-1-5", CwmpVersion.Unknown)]
    [InlineData("urn:dslforum-org:cwmp-2-0", CwmpVersion.Unknown)]
    [InlineData("", CwmpVersion.Unknown)]
    [InlineData(null, CwmpVersion.Unknown)]
    public void Detect_Namespace_MapsVersion(string? ns, CwmpVersion expected)
    {
        Assert.Equal(expected, CwmpNamespaces.Detect(ns));
    }

    [Theory]
    [InlineData(CwmpVersion.V1_0)]
    [InlineData(CwmpVersion.V1_4)]
    public void For_Version_RoundTripsWithDetect(CwmpVersion v)
    {
        Assert.Equal(v, CwmpNamespaces.Detect(CwmpNamespaces.For(v)));
    }

    [Theory]
    [InlineData("0 BOOTSTRAP", true, 0)]
    [InlineData("1 BOOT", true, 1)]
    [InlineData("14 HEARTBEAT", true, 14)]
    [InlineData("M Reboot", false, null)]
    [InlineData("X 00D09E MyEvent", false, null)]
    public void EventCodes_SingleAndNumeric_Classified(string code, bool single, int? numeric)
    {
        Assert.Equal(single, EventCodes.IsSingle(code));
        Assert.Equal(numeric, EventCodes.NumericCode(code));
    }

    [Fact]
    public void EventCodes_OrderKey_NumericThenMethodThenVendor()
    {
        var codes = new[] { "X 00D09E A", "M Reboot", "4 VALUE CHANGE", "1 BOOT", "10 AUTONOMOUS TRANSFER COMPLETE" };
        var ordered = codes.OrderBy(EventCodes.OrderKey).ToArray();
        Assert.Equal(new[] { "1 BOOT", "4 VALUE CHANGE", "10 AUTONOMOUS TRANSFER COMPLETE", "M Reboot", "X 00D09E A" }, ordered);
        Assert.True(EventCodes.IsMethodEvent("M Download"));
        Assert.True(EventCodes.IsVendorEvent("X 00D09E A"));
        Assert.Equal("1", EventCodes.Short("1 BOOT"));
        Assert.Equal(15, EventCodes.Standard.Count);
    }

    [Theory]
    [InlineData(9000u, "Server")]
    [InlineData(9001u, "Client")]
    [InlineData(9002u, "Server")]
    [InlineData(9003u, "Client")]
    [InlineData(9004u, "Server")]
    [InlineData(9005u, "Client")]
    [InlineData(9009u, "Client")]
    [InlineData(9010u, "Server")]
    [InlineData(9011u, "Server")]
    [InlineData(9012u, "Client")]
    [InlineData(9019u, "Client")]
    public void FaultCodes_SoapFaultCode_ClientForArgumentErrors(uint code, string expected)
    {
        Assert.Equal(expected, FaultCodes.SoapFaultCodeFor(code));
    }

    [Theory]
    [InlineData("xsd:unsignedInt", "300", true)]
    [InlineData("xsd:unsignedInt", "-1", false)]
    [InlineData("xsd:int", "-1", true)]
    [InlineData("xsd:boolean", "true", true)]
    [InlineData("xsd:boolean", "yes", false)]
    [InlineData("xsd:dateTime", "2026-10-28T10:00:00Z", true)]
    [InlineData("xsd:dateTime", "nope", false)]
    [InlineData("xsd:hexBinary", "0aFF", true)]
    [InlineData("xsd:hexBinary", "0aF", false)]
    [InlineData("xsd:base64", "AAAA", true)]
    [InlineData("xsd:long", "9223372036854775807", true)]
    [InlineData("xsd:unsignedLong", "18446744073709551615", true)]
    [InlineData("string", "anything", true)]
    public void XsdTypes_IsValidValue_ChecksLexicalForm(string type, string value, bool valid)
    {
        Assert.Equal(valid, XsdTypes.IsValidValue(type, value));
    }

    [Fact]
    public void XsdTypes_Normalize_AddsPrefix()
    {
        Assert.Equal("xsd:string", XsdTypes.Normalize(null));
        Assert.Equal("xsd:int", XsdTypes.Normalize("int"));
        Assert.Equal("xsd:base64", XsdTypes.Normalize("soap-enc:base64Binary"));
    }
}
