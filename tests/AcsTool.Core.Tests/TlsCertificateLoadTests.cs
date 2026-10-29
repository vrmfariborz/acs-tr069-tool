using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AcsTool.Core.Net;

namespace AcsTool.Core.Tests;

/// <summary>Custom-CA file formats after the move to X509CertificateLoader (.NET 10 migration; SYSLIB0057).</summary>
public sealed class TlsCertificateLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("acs-ca-").FullName;
    private readonly X509Certificate2 _ca;

    public TlsCertificateLoadTests()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=AcsTool Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public void Dispose()
    {
        _ca.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Pem_Loads()
    {
        using var c = TlsValidatorFactory.LoadCertificate(Write("ca.pem", System.Text.Encoding.ASCII.GetBytes(_ca.ExportCertificatePem())));
        Assert.Equal(_ca.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void Der_Loads()
    {
        using var c = TlsValidatorFactory.LoadCertificate(Write("ca.cer", _ca.Export(X509ContentType.Cert)));
        Assert.Equal(_ca.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void PasswordlessPfx_Loads()
    {
        using var c = TlsValidatorFactory.LoadCertificate(Write("ca.pfx", _ca.Export(X509ContentType.Pkcs12)));
        Assert.Equal(_ca.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void Garbage_ThrowsCryptographicException()
    {
        Assert.ThrowsAny<CryptographicException>(() => TlsValidatorFactory.LoadCertificate(Write("junk.bin", [1, 2, 3, 4, 5])));
    }
}
