using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AcsTool.Core.Net;

public sealed record UpstreamHandlerOptions(TimeSpan ConnectTimeout, TlsOptions Tls, bool UseCookies,
    CookieContainer? Cookies, ICredentials? Credentials, bool AllowAutoRedirect, bool UseSystemProxy)
{
    public bool PreAuthenticate { get; init; }

    public int MaxAutomaticRedirections { get; init; } = 5;
}

public interface IUpstreamHandlerFactory
{
    SocketsHttpHandler Create(UpstreamHandlerOptions options, Func<PhaseTracker> currentTracker, Action<TlsInfo> onTls);
}

/// <summary>Creates per-session SocketsHttpHandlers (plan §3.6.1 / D6): one connection, no auto-decompression, own ConnectCallback.</summary>
public sealed class UpstreamHandlerFactory : IUpstreamHandlerFactory
{
    public SocketsHttpHandler Create(UpstreamHandlerOptions options, Func<PhaseTracker> currentTracker, Action<TlsInfo> onTls)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = options.AllowAutoRedirect,
            MaxAutomaticRedirections = Math.Max(1, options.MaxAutomaticRedirections),
            UseCookies = options.UseCookies,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = options.UseSystemProxy,
            MaxConnectionsPerServer = 1,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectCallbackFactory.Create(options.ConnectTimeout, currentTracker),
            Credentials = options.Credentials,
            PreAuthenticate = options.PreAuthenticate,
            Expect100ContinueTimeout = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = TlsValidatorFactory.Create(options.Tls, onTls, currentTracker),
                EnabledSslProtocols = options.Tls.Protocols,
                // Strict keeps the platform default; Online is never used (R10: avoid hangs on isolated lab networks)
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
        };
        if (options.UseCookies)
        {
            handler.CookieContainer = options.Cookies ?? new CookieContainer();
        }
        return handler;
    }
}
