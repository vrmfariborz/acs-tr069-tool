using AcsTool.Core.Net;
using Microsoft.Extensions.Logging;

namespace AcsTool.Core.Settings;

public enum PathMode { PassThrough, Fixed }

public enum HostHeaderMode { Upstream, Preserve }

public enum CookieMode { PassThrough, ManagedPerCpe }

public enum FailureMode { StatusCode, Abort }

public enum SoapActionMode { Empty, MethodName, Omit }

public enum ThemeMode { System, Light, Dark }

public sealed record ProxySettings(string BindAddress = "0.0.0.0", int Port = 7547, string UpstreamUrl = "http://localhost:7557",
    PathMode PathMode = PathMode.PassThrough, string? FixedUpstreamPath = null, HostHeaderMode HostHeader = HostHeaderMode.Upstream,
    CookieMode CookieMode = CookieMode.PassThrough, bool StripCookieDomain = true, bool StripCookieSecureOnHttp = true,
    bool AddXForwardedFor = false, FailureMode FailureMode = FailureMode.StatusCode, int ConnectTimeoutSeconds = 10,
    int RequestTimeoutSeconds = 30, int SessionIdleTimeoutSeconds = 30, TlsOptions? Tls = null, bool UseSystemProxy = false,
    bool StartOnLaunch = false, int MaxRequestBodyBytes = 10_485_760);

public sealed record CaptureSettings(int MaxSessions = 5000, int MaxCapturedBodyBytes = 1_048_576,
    long CaptureBudgetBytes = 268_435_456, int MaxFeedItems = 2000);

public sealed record SimulatorSettings(int MaxConcurrentSessions = 32, int DefaultRequestTimeoutSeconds = 30,
    int RebootDelaySeconds = 10, int DownloadDurationSeconds = 5, string UserAgent = "AcsTool-Sim/1.0",
    SoapActionMode SoapAction = SoapActionMode.Empty);

public sealed record UiSettings(ThemeMode Theme = ThemeMode.System, bool NewestOnTop = true, bool AutoScroll = true, bool ShowRawByDefault = false);

public sealed record LoggingSettings(LogLevel MinimumLevel = LogLevel.Information, bool FileLoggingEnabled = true);

public sealed record AppSettings(int SchemaVersion, ProxySettings Proxy, CaptureSettings Capture,
    SimulatorSettings Simulator, UiSettings Ui, LoggingSettings Logging)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings Default { get; } = new(CurrentSchemaVersion, new ProxySettings(), new CaptureSettings(),
        new SimulatorSettings(), new UiSettings(), new LoggingSettings());

    /// <summary>Replaces sections missing from a partial JSON file with defaults.</summary>
    public AppSettings Normalize() => new(
        SchemaVersion <= 0 ? CurrentSchemaVersion : SchemaVersion,
        Proxy ?? Default.Proxy,
        Capture ?? Default.Capture,
        Simulator ?? Default.Simulator,
        Ui ?? Default.Ui,
        Logging ?? Default.Logging);
}

/// <summary>Holds the live settings instance shared by services and view models.</summary>
public sealed class SettingsHolder
{
    private AppSettings _current = AppSettings.Default;

    public AppSettings Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            Volatile.Write(ref _current, (value ?? throw new ArgumentNullException(nameof(value))).Normalize());
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;
}
