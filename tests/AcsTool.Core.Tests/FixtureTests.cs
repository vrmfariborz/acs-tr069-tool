using System.Xml;
using AcsTool.Testing;

namespace AcsTool.Core.Tests;

public class FixtureTests
{
    public static readonly string[] Expected =
    [
        "inform-boot-cwmp10.xml", "inform-bootstrap-cwmp12-tr181.xml", "inform-periodic-unqualified-noprefix.xml",
        "inform-nonstandard-soapns.xml", "inform-arraytype-mismatch.xml", "informresponse-cwmp10.xml",
        "gpv-request.xml", "gpv-response.xml", "spv-request.xml", "spv-response.xml", "spv-fault.xml", "fault-9005.xml",
        "reboot.xml", "download.xml", "transfercomplete.xml", "gpn-request.xml", "getrpcmethods.xml", "unknown-method.xml",
        "not-xml.txt", "xxe-attempt.xml", "empty.txt", "whitespace.txt", "utf8-bom-inform.xml", "utf16-inform.xml",
    ];

    private static readonly HashSet<string> DeliberatelyBroken = ["xxe-attempt.xml"];

    [Fact]
    public void Fixtures_AllPresent()
    {
        var all = Fixtures.All();
        foreach (var name in Expected)
        {
            Assert.Contains(name, all);
        }
    }

    [Fact]
    public void Fixtures_XmlFiles_AreWellFormedExceptDeliberatelyBroken()
    {
        foreach (var name in Expected.Where(n => n.EndsWith(".xml", StringComparison.Ordinal)))
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var ok = true;
            try
            {
                using var reader = XmlReader.Create(Fixtures.PathOf(name), settings);
                while (reader.Read())
                {
                }
            }
            catch (XmlException)
            {
                ok = false;
            }
            Assert.True(ok != DeliberatelyBroken.Contains(name), $"{name}: well-formed={ok}");
        }
    }
}
