using System.Globalization;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Feed;
using AcsTool.Presentation.Proxy;
using AcsTool.Presentation.Settings;
using AcsTool.Presentation.Simulator;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AcsTool.Presentation.Shell;

/// <summary>Main window: tabs, banners, status bar aggregates, startup and orderly shutdown (plan §3.8 rule 9).</summary>
public sealed partial class MainViewModel : ObservableObject, IFeedBatchConsumer
{
    private readonly ISessionStore _store;
    private readonly ISettingsStore _settingsStore;
    private readonly SettingsHolder _settings;
    private readonly SessionFeedPump _pump;
    private readonly ILogger<MainViewModel> _log;
    private bool _shutDown;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _activeSessions;
    [ObservableProperty] private int _totalSessions;
    [ObservableProperty] private double _captureMegabytes;
    [ObservableProperty] private long _droppedNotifications;
    [ObservableProperty] private int _selectedTabIndex;

    public MainViewModel(ProxyViewModel proxy, SimulatorViewModel simulator, SettingsViewModel settingsVm, IBannerService banners,
        ISessionStore store, ISettingsStore settingsStore, SettingsHolder settings, SessionFeedPump pump, ILogger<MainViewModel> log)
    {
        Proxy = proxy;
        Simulator = simulator;
        Settings = settingsVm;
        Banners = banners;
        _store = store;
        _settingsStore = settingsStore;
        _settings = settings;
        _pump = pump;
        _log = log;
        _pump.Subscribe(proxy.Feed);
        _pump.Subscribe(simulator.Feed);
        _pump.Subscribe(this);
        proxy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProxyViewModel.State))
            {
                RefreshStatus();
            }
        };
        RefreshStatus();
    }

    public ProxyViewModel Proxy { get; }

    public SimulatorViewModel Simulator { get; }

    public SettingsViewModel Settings { get; }

    public IBannerService Banners { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML binding")]
    public string Title => "AcsTool — TR-069 Proxy & Inform Simulator";

    /// <summary>Loads settings and profiles, starts the feed pump, and optionally starts the proxy (StartOnLaunch).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _settings.Current = await _settingsStore.LoadAsync(ct);
        if (_settingsStore is JsonSettingsStore { LastLoadProblem: { } problem })
        {
            Banners.Show(BannerKind.Warning, problem, "settings-load");
        }
        Settings.LoadFrom(_settings.Current);
        var capture = _settings.Current.Capture;
        Proxy.Feed.MaxItems = Simulator.Feed.MaxItems = capture.MaxFeedItems;
        Proxy.Feed.NewestOnTop = Simulator.Feed.NewestOnTop = _settings.Current.Ui.NewestOnTop;
        _pump.Start();
        await Simulator.LoadAsync(ct);
        Banners.Show(BannerKind.Info, "Windows Firewall may prompt; allow Private networks so modems can reach the proxy.", "firewall-hint");
        if (_settings.Current.Proxy.StartOnLaunch)
        {
            await Proxy.StartAsync();
        }
        RefreshStatus();
    }

    public void Apply(FeedBatch batch) => RefreshStatus();

    public void RefreshStatus()
    {
        var stats = _store.Stats;
        ActiveSessions = stats.Active;
        TotalSessions = stats.Sessions;
        CaptureMegabytes = Math.Round(stats.CapturedBytes / (1024.0 * 1024.0), 1);
        DroppedNotifications = stats.DroppedNotifications;
        var budget = _settings.Current.Capture.CaptureBudgetBytes / (1024.0 * 1024.0);
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"Proxy: {Proxy.State} · active {stats.Active} · sessions {stats.Sessions} · capture {CaptureMegabytes:0.0} MB / {budget:0} MB · dropped {stats.DroppedNotifications}");
    }

    /// <summary>Stops the proxy and simulator, stops the pump, and saves settings. Safe to call twice.</summary>
    public async Task ShutdownAsync()
    {
        if (_shutDown)
        {
            return;
        }
        _shutDown = true;
        try
        {
            await Proxy.StopAsync();
            await Simulator.StopAllAsync();
            await _pump.StopAsync();
            await _settingsStore.SaveAsync(_settings.Current, CancellationToken.None);
        }
#pragma warning disable CA1031 // shutdown must complete
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError(9001, ex, "Error during shutdown");
        }
    }
}
