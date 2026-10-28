using System.ComponentModel.DataAnnotations;
using AcsTool.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcsTool.Presentation.Settings;

/// <summary>Settings window (plan §4.6, MVP: Proxy + Capture pages). Edits a copy; Save writes atomically and publishes.</summary>
public sealed partial class SettingsViewModel : ObservableValidator
{
    private readonly ISettingsStore _store;
    private readonly SettingsHolder _holder;
    private AppSettings _base = AppSettings.Default;

    // Proxy
    [ObservableProperty] [NotifyDataErrorInfo] [Required] private string _bindAddress = "";
    [ObservableProperty] [NotifyDataErrorInfo] [Range(1, 65535)] private int _port;
    [ObservableProperty] [NotifyDataErrorInfo] [Required] [CustomValidation(typeof(SettingsViewModel), nameof(ValidateUrl))] private string _upstreamUrl = "";
    [ObservableProperty] private PathMode _pathMode;
    [ObservableProperty] private string? _fixedUpstreamPath;
    [ObservableProperty] private HostHeaderMode _hostHeader;
    [ObservableProperty] private CookieMode _cookieMode;
    [ObservableProperty] private bool _stripCookieDomain;
    [ObservableProperty] private bool _stripCookieSecureOnHttp;
    [ObservableProperty] private bool _addXForwardedFor;
    [ObservableProperty] private FailureMode _failureMode;
    [ObservableProperty] [NotifyDataErrorInfo] [Range(1, 300)] private int _connectTimeoutSeconds;
    [ObservableProperty] [NotifyDataErrorInfo] [Range(1, 3600)] private int _requestTimeoutSeconds;
    [ObservableProperty] [NotifyDataErrorInfo] [Range(1, 3600)] private int _sessionIdleTimeoutSeconds;
    [ObservableProperty] private bool _useSystemProxy;
    [ObservableProperty] private bool _startOnLaunch;

    // Capture
    [ObservableProperty] [NotifyDataErrorInfo] [Range(10, 1_000_000)] private int _maxSessions;
    [ObservableProperty] [NotifyDataErrorInfo] [Range(1, 100 * 1024)] private int _maxCapturedBodyKiB;
    [ObservableProperty] [NotifyDataErrorInfo] [Range(16, 16 * 1024)] private int _captureBudgetMiB;
    [ObservableProperty] [NotifyDataErrorInfo] [Range(100, 100_000)] private int _maxFeedItems;

    public SettingsViewModel(ISettingsStore store, SettingsHolder holder)
    {
        _store = store;
        _holder = holder;
        LoadFrom(holder.Current);
    }

    public IReadOnlyList<PathMode> PathModes { get; } = Enum.GetValues<PathMode>();
    public IReadOnlyList<HostHeaderMode> HostHeaderModes { get; } = Enum.GetValues<HostHeaderMode>();
    public IReadOnlyList<CookieMode> CookieModes { get; } = Enum.GetValues<CookieMode>();
    public IReadOnlyList<FailureMode> FailureModes { get; } = Enum.GetValues<FailureMode>();

    public static ValidationResult? ValidateUrl(string? value, ValidationContext context) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? ValidationResult.Success
            : new ValidationResult("Must be an absolute http:// or https:// URL");

    public void LoadFrom(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _base = s;
        var p = s.Proxy;
        BindAddress = p.BindAddress;
        Port = p.Port;
        UpstreamUrl = p.UpstreamUrl;
        PathMode = p.PathMode;
        FixedUpstreamPath = p.FixedUpstreamPath;
        HostHeader = p.HostHeader;
        CookieMode = p.CookieMode;
        StripCookieDomain = p.StripCookieDomain;
        StripCookieSecureOnHttp = p.StripCookieSecureOnHttp;
        AddXForwardedFor = p.AddXForwardedFor;
        FailureMode = p.FailureMode;
        ConnectTimeoutSeconds = p.ConnectTimeoutSeconds;
        RequestTimeoutSeconds = p.RequestTimeoutSeconds;
        SessionIdleTimeoutSeconds = p.SessionIdleTimeoutSeconds;
        UseSystemProxy = p.UseSystemProxy;
        StartOnLaunch = p.StartOnLaunch;
        var c = s.Capture;
        MaxSessions = c.MaxSessions;
        MaxCapturedBodyKiB = c.MaxCapturedBodyBytes / 1024;
        CaptureBudgetMiB = (int)(c.CaptureBudgetBytes / (1024 * 1024));
        MaxFeedItems = c.MaxFeedItems;
        ValidateAllProperties();
    }

    public AppSettings Build() => _base with
    {
        Proxy = _base.Proxy with
        {
            BindAddress = BindAddress.Trim(),
            Port = Port,
            UpstreamUrl = UpstreamUrl.Trim(),
            PathMode = PathMode,
            FixedUpstreamPath = string.IsNullOrWhiteSpace(FixedUpstreamPath) ? null : FixedUpstreamPath.Trim(),
            HostHeader = HostHeader,
            CookieMode = CookieMode,
            StripCookieDomain = StripCookieDomain,
            StripCookieSecureOnHttp = StripCookieSecureOnHttp,
            AddXForwardedFor = AddXForwardedFor,
            FailureMode = FailureMode,
            ConnectTimeoutSeconds = ConnectTimeoutSeconds,
            RequestTimeoutSeconds = RequestTimeoutSeconds,
            SessionIdleTimeoutSeconds = SessionIdleTimeoutSeconds,
            UseSystemProxy = UseSystemProxy,
            StartOnLaunch = StartOnLaunch,
        },
        Capture = _base.Capture with
        {
            MaxSessions = MaxSessions,
            MaxCapturedBodyBytes = MaxCapturedBodyKiB * 1024,
            CaptureBudgetBytes = (long)CaptureBudgetMiB * 1024 * 1024,
            MaxFeedItems = MaxFeedItems,
        },
    };

    /// <summary>Validates, saves to disk and publishes to <see cref="SettingsHolder"/>. Proxy changes apply on the next Start.</summary>
    [RelayCommand]
    public async Task<bool> SaveAsync()
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            return false;
        }
        var s = Build();
        await _store.SaveAsync(s, CancellationToken.None);
        _holder.Current = s;
        _base = _holder.Current;
        return true;
    }

    [RelayCommand]
    private void Cancel() => LoadFrom(_holder.Current);

    [RelayCommand]
    private void RestoreDefaults() => LoadFrom(AppSettings.Default with { Simulator = _base.Simulator, Ui = _base.Ui, Logging = _base.Logging });
}
