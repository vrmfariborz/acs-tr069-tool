using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AcsTool.Core.Net;

public enum TlsValidationMode { Strict, IgnoreErrors, PinnedThumbprint, CustomCa }

public sealed record TlsOptions(TlsValidationMode Mode = TlsValidationMode.Strict, IReadOnlyList<string>? PinnedSha256 = null,
    string? CustomCaPath = null, SslProtocols Protocols = SslProtocols.None,
    string? ClientCertPfxPath = null, string? ClientCertPasswordProtected = null)
{
    public static TlsOptions Default { get; } = new();
}

public sealed record TlsInfo(string? Subject, string? Issuer, string? ThumbprintSha256, DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter, string PolicyErrors, string? ChainStatus, bool AcceptedByOverride);

/// <summary>
/// Certificate validation per TLS mode (plan §3.5.5). MVP tests cover Strict; the other modes are implemented
/// but their integration matrix belongs to M6.
/// </summary>
public static class TlsValidatorFactory
{
    public static string NormalizePin(string pin) =>
        new string((pin ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    public static RemoteCertificateValidationCallback Create(TlsOptions options, Action<TlsInfo> onTls, Func<PhaseTracker>? tracker = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onTls);
        X509Certificate2? customCa = null;
        if (options.Mode == TlsValidationMode.CustomCa && !string.IsNullOrEmpty(options.CustomCaPath))
        {
            customCa = LoadCertificate(options.CustomCaPath);
        }
        var pins = (options.PinnedSha256 ?? []).Select(NormalizePin).ToHashSet(StringComparer.Ordinal);

        return (_, certificate, chain, errors) =>
        {
            using var leaf = certificate is null ? null : new X509Certificate2(certificate);
            var thumb = leaf is null ? null : Convert.ToHexString(SHA256.HashData(leaf.RawData)).ToLowerInvariant();
            var chainStatus = chain is null ? null : string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()).Distinct());
            bool ok;
            switch (options.Mode)
            {
                case TlsValidationMode.IgnoreErrors:
                    ok = true;
                    break;
                case TlsValidationMode.PinnedThumbprint:
                    ok = thumb is not null && pins.Contains(thumb);
                    break;
                case TlsValidationMode.CustomCa:
                    ok = leaf is not null && customCa is not null
                        && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                        && BuildWithCustomRoot(leaf, customCa, chain);
                    break;
                default:
                    ok = errors == SslPolicyErrors.None;
                    break;
            }
            onTls(new TlsInfo(leaf?.Subject, leaf?.Issuer, thumb,
                leaf is null ? null : new DateTimeOffset(leaf.NotBefore.ToUniversalTime()),
                leaf is null ? null : new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
                errors.ToString(), string.IsNullOrEmpty(chainStatus) ? null : chainStatus,
                ok && errors != SslPolicyErrors.None));
            if (ok)
            {
                tracker?.Invoke().MarkTlsDone();
            }
            return ok;
        };
    }

    private static bool BuildWithCustomRoot(X509Certificate2 leaf, X509Certificate2 ca, X509Chain? original)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (original is not null)
        {
            foreach (var el in original.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(el.Certificate);
            }
        }
        return chain.Build(leaf);
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        var data = File.ReadAllBytes(path);
        var text = System.Text.Encoding.ASCII.GetString(data);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            return X509Certificate2.CreateFromPem(text);
        }
#pragma warning disable SYSLIB0057 // net8 API; X509CertificateLoader is net9+
        return new X509Certificate2(data);
#pragma warning restore SYSLIB0057
    }
}
