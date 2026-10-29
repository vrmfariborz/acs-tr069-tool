using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AcsTool.Core.Net;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Feed;
using AcsTool.Proxy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AcsTool.Presentation.Proxy;

/// <summary>Proxy tab top bar (plan §4.1): bind/port/upstream with validation, Start/Stop, bound URLs, bind-error banners.</summary>
public sealed partial class ProxyViewModel : ObservableValidator, IAsyncDisposable
{
    public const string BindBannerKey = "proxy-bind";
    public const string DigestBannerKey = "proxy-digest-path";
    public const string TlsChipKey = "proxy-tls-disabled";

    private readonly ProxyServerFactory _factory;
    private readonly SettingsHolder _settings;
    private readonly IBannerService _banners;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ProxyViewModel> _log;
    private ProxyServer? _server;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Bind address is required")]
    [CustomValidation(typeof(ProxyViewModel), nameof(ValidateBindAddress))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private string _bindAddress;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535")]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private int _port;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Upstream URL is required")]
    [CustomValidation(typeof(ProxyViewModel), nameof(ValidateUpstreamUrl))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private string _upstreamUrl;

    [ObservableProperty]
    private TlsValidationMode _tlsMode;

    [ObservableProperty]
    private CookieMode _cookieMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanEdit), nameof(StateText), nameof(StartStopText))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private ProxyState _state = ProxyState.Stopped;

    public ProxyViewModel(ProxyServerFactory factory, SettingsHolder settings, IBannerService banners, IUiDispatcher dispatcher,
        SessionFeedViewModel feed, ILogger<ProxyViewModel> log)
    {
        _factory = factory;
        _settings = settings;
        _banners = banners;
        _dispatcher = dispatcher;
        _log = log;
        Feed = feed;
        var p = settings.Current.Proxy;
        _bindAddress = p.BindAddress;
        _port = p.Port;
        _upstreamUrl = p.UpstreamUrl;
        _tlsMode = (p.Tls ?? TlsOptions.Default).Mode;
        _cookieMode = p.CookieMode;
        ValidateAllProperties();
        // Settings may be loaded (or edited in the Settings window) after this VM was created: follow them while the proxy is stopped.
        settings.Changed += (_, _) => _ = _dispatcher.InvokeAsync(() =>
        {
            if (CanEdit && _server is null)
            {
                LoadFrom(_settings.Current.Proxy);
            }
        });
    }

    /// <summary>Copies persisted proxy settings into the editable fields (only meaningful while stopped).</summary>
    public void LoadFrom(ProxySettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        BindAddress = p.BindAddress;
        Port = p.Port;
        UpstreamUrl = p.UpstreamUrl;
        TlsMode = (p.Tls ?? TlsOptions.Default).Mode;
        CookieMode = p.CookieMode;
        ValidateAllProperties();
    }

    public SessionFeedViewModel Feed { get; }

    public ObservableCollection<string> BoundUrls { get; } = [];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML binding")]
    public IReadOnlyList<string> BindAddressChoices => ["0.0.0.0", "127.0.0.1", .. LocalIPv4()];

    public IReadOnlyList<TlsValidationMode> TlsModes { get; } = Enum.GetValues<TlsValidationMode>();

    public IReadOnlyList<CookieMode> CookieModes { get; } = Enum.GetValues<CookieMode>();

    public bool IsRunning => State == ProxyState.Running;

    public bool CanEdit => State == ProxyState.Stopped || State == ProxyState.Faulted;

    public string StateText => State switch
    {
        ProxyState.Running => BoundUrls.Count > 0 ? "● Running on " + string.Join("  ", BoundUrls) : "● Running",
        ProxyState.Faulted => "✖ Failed to start",
        _ => State.ToString(),
    };

    public string StartStopText => IsRunning ? "⏹ Stop" : "▶ Start";

    public int ActiveSessions => _server?.Tracker.ActiveCount ?? 0;

    public static ValidationResult? ValidateUpstreamUrl(string? value, ValidationContext context) =>
        !(Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0)
            ? new ValidationResult("Upstream must be an absolute http:// or https:// URL")
            : u.UserInfo.Length > 0 ? new ValidationResult(UrlUtil.UserInfoRejectedMessage) : ValidationResult.Success;

    public static ValidationResult? ValidateBindAddress(string? value, ValidationContext context) =>
        IPAddress.TryParse(value, out _) || string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Success
            : new ValidationResult("Bind address must be an IP address (0.0.0.0 = all interfaces)");

    private bool CanStartStop() => State switch
    {
        ProxyState.Running => true,
        ProxyState.Stopped or ProxyState.Faulted => !HasErrors,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    public async Task<bool> StartAsync()
    {
        ValidateAllProperties();
        if (HasErrors || _server is not null)
        {
            return false;
        }
        var current = _settings.Current;
        var proxy = current.Proxy with
        {
            BindAddress = BindAddress.Trim(),
            Port = Port,
            UpstreamUrl = UpstreamUrl.Trim(),
            CookieMode = CookieMode,
            Tls = (current.Proxy.Tls ?? TlsOptions.Default) with { Mode = TlsMode },
        };
        _settings.Current = current with { Proxy = proxy };
        UpdateAdvisories(proxy);
        State = ProxyState.Starting;
        var server = _factory.Create(proxy, current.Capture);
        server.StateChanged += OnServerStateChanged;
        try
        {
            await server.StartAsync(CancellationToken.None);
            _server = server;
            _banners.Dismiss(BindBannerKey);
            BoundUrls.Clear();
            foreach (var url in DisplayUrls(proxy.BindAddress, server.BoundPort))
            {
                BoundUrls.Add(url);
            }
            State = ProxyState.Running;
            return true;
        }
        catch (ProxyBindException ex)
        {
            _log.LogWarning(1101, ex, "Proxy failed to bind {Address}:{Port}", proxy.BindAddress, proxy.Port);
            server.StateChanged -= OnServerStateChanged;
            await server.DisposeAsync();
            _banners.Show(BannerKind.Error, ex.Message, BindBannerKey);
            State = ProxyState.Faulted;
            return false;
        }
#pragma warning disable CA1031 // any start failure must leave the tab usable (Faulted + banner), never stuck in Starting (review m11)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError(1102, ex, "Proxy failed to start on {Address}:{Port}", proxy.BindAddress, proxy.Port);
            server.StateChanged -= OnServerStateChanged;
            await server.DisposeAsync();
            _banners.Show(BannerKind.Error, "Proxy failed to start: " + ex.Message, BindBannerKey);
            State = ProxyState.Faulted;
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (_server is not { } server)
        {
            return;
        }
        State = ProxyState.Stopping;
        _server = null;
        server.StateChanged -= OnServerStateChanged;
        await server.StopAsync(CancellationToken.None);
        await server.DisposeAsync();
        BoundUrls.Clear();
        State = ProxyState.Stopped;
    }

    private void OnServerStateChanged(object? sender, ProxyState s)
    {
        if (s == ProxyState.Faulted)
        {
            _dispatcher.Post(() => State = ProxyState.Faulted);
        }
    }

    private void UpdateAdvisories(ProxySettings p)
    {
        if (Uri.TryCreate(p.UpstreamUrl, UriKind.Absolute, out var u) && u.AbsolutePath.Length > 1 && p.PathMode == PathMode.PassThrough)
        {
            _banners.Show(BannerKind.Warning, "Upstream base URL has a path; Digest auth may fail (uri mismatch)", DigestBannerKey);
        }
        else
        {
            _banners.Dismiss(DigestBannerKey);
        }
        if (u?.Scheme == Uri.UriSchemeHttps && TlsMode == TlsValidationMode.IgnoreErrors)
        {
            _banners.Show(BannerKind.Warning, "TLS validation disabled for the upstream ACS", TlsChipKey);
        }
        else
        {
            _banners.Dismiss(TlsChipKey);
        }
    }

    public static IReadOnlyList<string> DisplayUrls(string bindAddress, int port)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        if (bindAddress is "0.0.0.0" or "::" or "[::]")
        {
            var ips = LocalIPv4().ToList();
            return ips.Count == 0 ? [$"http://127.0.0.1:{p}"] : ips.Select(ip => $"http://{ip}:{p}").ToList();
        }
        return [$"http://{bindAddress}:{p}"];
    }

    private static IEnumerable<string> LocalIPv4()
    {
        IEnumerable<string> result;
        try
        {
            result = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            result = [];
        }
        return result;
    }

    partial void OnStateChanged(ProxyState value) => OnPropertyChanged(nameof(ActiveSessions));

    public async ValueTask DisposeAsync() => await StopAsync();
}
