using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AcsTool.Core.Net;

namespace AcsTool.Core.Tests;

/// <summary>
/// TesterBot, chore/net10 (42e3723): custom-CA load matrix beyond PEM/DER/password-less PFX.
/// Certificates are generated with CertificateRequest (no openssl dependency).
/// </summary>
public sealed class TesterCaMatrixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("acs-ca-matrix-").FullName;
    private readonly RSA _rootKey = RSA.Create(2048);
    private readonly RSA _intKey = RSA.Create(2048);
    private readonly RSA _leafKey = RSA.Create(2048);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _int;
    private readonly X509Certificate2 _leaf;

    public TesterCaMatrixTests()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(30);
        var rootReq = new CertificateRequest("CN=Tester Root CA", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        _root = rootReq.CreateSelfSigned(from, to);

        var intReq = new CertificateRequest("CN=Tester Intermediate CA", _intKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using (var intPub = intReq.Create(_root, from, to, [1, 2, 3, 4]))
        {
            _int = intPub.CopyWithPrivateKey(_intKey);
        }

        var leafReq = new CertificateRequest("CN=acs.test", _leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var leafPub = leafReq.Create(_int, from, to, [5, 6, 7, 8]);
        _leaf = leafPub.CopyWithPrivateKey(_leafKey);
    }

    public void Dispose()
    {
        _leaf.Dispose();
        _int.Dispose();
        _root.Dispose();
        _leafKey.Dispose();
        _intKey.Dispose();
        _rootKey.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string WriteText(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    [Fact]
    public void PasswordProtectedPfx_ThrowsCryptographicException()
    {
        var path = Write("pw.pfx", _root.Export(X509ContentType.Pkcs12, "secret"));
        Assert.ThrowsAny<CryptographicException>(() => TlsValidatorFactory.LoadCertificate(path));
    }

    [Fact]
    public void EmptyFile_ThrowsCryptographicException_NotNullReference()
    {
        // The old new X509Certificate2(byte[0]) threw NullReferenceException, which PrepareTls did not catch.
        var path = Write("empty.pem", []);
        Assert.ThrowsAny<CryptographicException>(() => TlsValidatorFactory.LoadCertificate(path));
    }

    [Fact]
    public void WhitespaceOnlyFile_ThrowsCryptographicException()
    {
        Assert.ThrowsAny<CryptographicException>(() => TlsValidatorFactory.LoadCertificate(WriteText("ws.pem", "  \r\n\n")));
    }

    [Fact]
    public void TruncatedPem_ThrowsCryptographicException()
    {
        var pem = _root.ExportCertificatePem();
        Assert.ThrowsAny<CryptographicException>(() => TlsValidatorFactory.LoadCertificate(WriteText("trunc.pem", pem[..(pem.Length / 2)])));
    }

    [Fact]
    public void TruncatedDer_ThrowsCryptographicException()
    {
        var der = _root.Export(X509ContentType.Cert);
        Assert.ThrowsAny<CryptographicException>(() => TlsValidatorFactory.LoadCertificate(Write("trunc.der", der[..(der.Length / 3)])));
    }

    [Fact]
    public void CrlfPem_Loads()
    {
        var pem = _root.ExportCertificatePem().Replace("\n", "\r\n", StringComparison.Ordinal);
        using var c = TlsValidatorFactory.LoadCertificate(WriteText("crlf.pem", pem));
        Assert.Equal(_root.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void PemWithPrivateKeyBeforeCertificate_LoadsCertificate()
    {
        var pem = _rootKey.ExportPkcs8PrivateKeyPem() + "\n" + _root.ExportCertificatePem();
        using var c = TlsValidatorFactory.LoadCertificate(WriteText("key-cert.pem", pem));
        Assert.Equal(_root.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void PemBundle_RootFirst_LoadsRoot()
    {
        var pem = _root.ExportCertificatePem() + "\n" + _int.ExportCertificatePem();
        using var c = TlsValidatorFactory.LoadCertificate(WriteText("bundle.pem", pem));
        Assert.Equal(_root.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void PasswordlessPfx_LeafWithChain_PicksCaNotLeaf()
    {
        // The old new X509Certificate2(byte[]) returned the leaf (the key holder); the loader now prefers a CA.
        var coll = new X509Certificate2Collection { _leaf, _int, _root };
        var path = Write("leafchain.pfx", coll.Export(X509ContentType.Pkcs12)!);
        using var c = TlsValidatorFactory.LoadCertificate(path);
        Assert.NotEqual(_leaf.Thumbprint, c.Thumbprint);
        Assert.Contains(c.Extensions.OfType<X509BasicConstraintsExtension>(), b => b.CertificateAuthority);
    }

    [Fact]
    public void PasswordlessPfx_CertificateOnly_Loads()
    {
        var coll = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(_root.RawData) };
        using var c = TlsValidatorFactory.LoadCertificate(Write("certonly.pfx", coll.Export(X509ContentType.Pkcs12)!));
        Assert.Equal(_root.Thumbprint, c.Thumbprint);
    }

    [Fact]
    public void LeafUsedAsCustomCa_LoadsWithoutWarning()
    {
        // Characterisation: a non-CA certificate is accepted at load/start time; the mistake only surfaces as a TLS failure later.
        using var c = TlsValidatorFactory.LoadCertificate(WriteText("leaf.pem", _leaf.ExportCertificatePem()));
        Assert.Equal(_leaf.Thumbprint, c.Thumbprint);
    }

    [Fact(Skip = "NET10-01 (Low/Medium, pre-existing): a PEM saved with a UTF-8 BOM (e.g. by some Windows editors) fails with 'The certificate contents do not contain a PEM with a CERTIFICATE label, or the content is malformed.'; the obsolete byte[] ctor accepted it")]
    public void Utf8BomPem_Loads()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes(_root.ExportCertificatePem())).ToArray();
        using var c = TlsValidatorFactory.LoadCertificate(Write("bom.pem", bytes));
        Assert.Equal(_root.Thumbprint, c.Thumbprint);
    }

    [Fact(Skip = "NET10-02 (Low/Medium, pre-existing): a multi-certificate PEM bundle only uses its first certificate; intermediate-first bundles load the intermediate, the proxy starts, and the chain to the root then fails at TLS time")]
    public void PemBundle_IntermediateFirst_TrustsTheRoot()
    {
        var pem = _int.ExportCertificatePem() + "\n" + _root.ExportCertificatePem();
        using var c = TlsValidatorFactory.LoadCertificate(WriteText("bundle-int-first.pem", pem));
        Assert.Equal(_root.Thumbprint, c.Thumbprint);
    }
}
