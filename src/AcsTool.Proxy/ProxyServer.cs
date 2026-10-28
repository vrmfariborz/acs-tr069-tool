using System.Net;
using System.Net.Sockets;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AcsTool.Proxy;

/// <summary>In-process Kestrel listener (D1) with start/stop and bind-error mapping (T2.2).</summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private readonly ProxySettings _settings;
    private readonly ProxyRequestHandler _handler;
    private readonly CpeSessionTracker _tracker;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProxyServer> _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private CancellationTokenSource? _stopping;
    private ProxyState _state = ProxyState.Stopped;

    public ProxyServer(ProxySettings settings, ProxyRequestHandler handler, CpeSessionTracker tracker, ILoggerFactory lf)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _loggerFactory = lf ?? throw new ArgumentNullException(nameof(lf));
        _log = lf.CreateLogger<ProxyServer>();
    }

    public ProxySettings Settings => _settings;

    public CpeSessionTracker Tracker => _tracker;

    public ProxyState State => _state;

    public event EventHandler<ProxyState>? StateChanged;

    public IReadOnlyList<string> BoundUrls { get; private set; } = [];

    /// <summary>The actual port (useful when configured with port 0).</summary>
    public int BoundPort { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == ProxyState.Running)
            {
                return;
            }
            SetState(ProxyState.Starting);
            if (!IPAddress.TryParse(_settings.BindAddress.Trim('[', ']'), out var ip))
            {
                SetState(ProxyState.Faulted);
                throw new ProxyBindException(ProxyBindFailure.AddressNotAvailable, _settings.BindAddress, _settings.Port, null);
            }
            _stopping = new CancellationTokenSource();
            var app = Build(ip);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AddressInUseException or InvalidOperationException or UnauthorizedAccessException)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                _stopping.Dispose();
                _stopping = null;
                SetState(ProxyState.Faulted);
                var reason = MapBindFailure(ex);
                _log.LogWarning(1010, ex, "Proxy bind failed on {Address}:{Port}: {Reason}", _settings.BindAddress, _settings.Port, reason);
                throw new ProxyBindException(reason, _settings.BindAddress, _settings.Port, ex);
            }
            _app = app;
            BoundUrls = [.. app.Urls];
            BoundPort = BoundUrls.Select(u => Uri.TryCreate(u.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal).Replace("[::]", "[::1]", StringComparison.Ordinal), UriKind.Absolute, out var x) ? x.Port : 0)
                .FirstOrDefault(p => p > 0);
            _tracker.StartSweeper();
            _log.LogInformation(1011, "Proxy listening on {Urls} → {Upstream}", string.Join(", ", BoundUrls), _settings.UpstreamUrl);
            SetState(ProxyState.Running);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is null)
            {
                if (_state != ProxyState.Stopped)
                {
                    SetState(ProxyState.Stopped);
                }
                return;
            }
            SetState(ProxyState.Stopping);
            await _stopping!.CancelAsync().ConfigureAwait(false);
            using (var graceful = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                graceful.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await _app.StopAsync(graceful.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _log.LogWarning(1012, "Graceful proxy stop timed out; connections aborted");
                }
            }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
            _stopping.Dispose();
            _stopping = null;
            _tracker.StopSweeper();
            await _tracker.EndAllAsync(SessionOutcome.Canceled).ConfigureAwait(false);
            BoundUrls = [];
            SetState(ProxyState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _tracker.DisposeAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private WebApplication Build(IPAddress ip)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = "AcsTool.Proxy",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.Services.Replace(ServiceDescriptor.Singleton(_loggerFactory));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.AllowSynchronousIO = false;
            k.Limits.MaxRequestBodySize = null; // enforced by the handler (MaxRequestBodyBytes → 413 + recorded)
            k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            k.Listen(ip, _settings.Port);
        });
        var app = builder.Build();
        var stopping = _stopping!.Token;
        app.Run(ctx => _handler.HandleAsync(ctx, stopping));
        return app;
    }

    internal static ProxyBindFailure MapBindFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AddressInUseException)
            {
                return ProxyBindFailure.PortInUse;
            }
            if (e is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => ProxyBindFailure.PortInUse,
                    SocketError.AccessDenied => ProxyBindFailure.AccessDenied,
                    SocketError.AddressNotAvailable => ProxyBindFailure.AddressNotAvailable,
                    _ => ProxyBindFailure.Other,
                };
            }
            if (e is UnauthorizedAccessException)
            {
                return ProxyBindFailure.AccessDenied;
            }
        }
        return ProxyBindFailure.Other;
    }

    private void SetState(ProxyState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
