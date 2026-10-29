using System.Net;
using System.Net.Sockets;
using AcsTool.Core;
using AcsTool.Core.Model;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Proxy;
using AcsTool.Presentation.Settings;
using AcsTool.Presentation.Shell;
using AcsTool.Presentation.Simulator;
using AcsTool.Simulator.Profiles;
using AcsTool.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AcsTool.Presentation.Tests;

/// <summary>The same DI graph as the App, with Linux-friendly platform services.</summary>
public sealed class AppHarness : IAsyncDisposable
{
    private AppHarness(ServiceProvider sp, TempAppPaths paths)
    {
        Services = sp;
        Paths = paths;
    }

    public ServiceProvider Services { get; }
    public TempAppPaths Paths { get; }
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static AppHarness Create(Action<SettingsHolder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var paths = new TempAppPaths();
        var collection = new ServiceCollection();
        services?.Invoke(collection);
        var built = collection
            .AddLogging()
            .AddSingleton<IAppPaths>(paths)
            .AddSingleton<IUiDispatcher, SynchronousUiDispatcher>()
            .AddSingleton<IClipboardService, FakeClipboard>()
            .AddAcsToolPresentation();
        var sp = built.BuildServiceProvider();
        configure?.Invoke(sp.GetRequiredService<SettingsHolder>());
        return new AppHarness(sp, paths);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Paths.Dispose();
    }
}

public class ProxyViewModelTests
{
    [Fact]
    public async Task Start_Running_ThenSecondVmOnSamePort_PortInUseBanner()
    {
        await using var acs = await FakeAcs.StartAsync();
        var port = AppHarness.FreePort();
        await using var h1 = AppHarness.Create();
        await using var h2 = AppHarness.Create();
        var vm1 = h1.Get<ProxyViewModel>();
        vm1.BindAddress = "127.0.0.1";
        vm1.Port = port;
        vm1.UpstreamUrl = acs.BaseUri.ToString();
        Assert.True(vm1.StartStopCommand.CanExecute(null));
        await vm1.StartStopCommand.ExecuteAsync(null);
        Assert.Equal(AcsTool.Proxy.ProxyState.Running, vm1.State);
        Assert.Equal($"http://127.0.0.1:{port}", Assert.Single(vm1.BoundUrls));
        Assert.Contains("Running on", vm1.StateText, StringComparison.Ordinal);

        var vm2 = h2.Get<ProxyViewModel>();
        vm2.BindAddress = "127.0.0.1";
        vm2.Port = port;
        vm2.UpstreamUrl = acs.BaseUri.ToString();
        Assert.False(await vm2.StartAsync());
        Assert.Equal(AcsTool.Proxy.ProxyState.Faulted, vm2.State);
        var banner = Assert.Single(h2.Get<IBannerService>().Banners, b => b.Key == ProxyViewModel.BindBannerKey);
        Assert.Equal(BannerKind.Error, banner.Kind);
        Assert.Contains($"Port {port} is already in use", banner.Text, StringComparison.Ordinal);

        await vm1.StartStopCommand.ExecuteAsync(null);
        Assert.Equal(AcsTool.Proxy.ProxyState.Stopped, vm1.State);
        Assert.True(await vm2.StartAsync()); // port is free again
        Assert.DoesNotContain(h2.Get<IBannerService>().Banners, b => b.Key == ProxyViewModel.BindBannerKey);
        await vm2.StopAsync();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://acs")]
    [InlineData("")]
    public async Task InvalidUpstream_DisablesStart(string url)
    {
        await using var h = AppHarness.Create();
        var vm = h.Get<ProxyViewModel>();
        vm.UpstreamUrl = url;
        Assert.True(vm.HasErrors);
        Assert.False(vm.StartStopCommand.CanExecute(null));
        vm.UpstreamUrl = "http://acs.example:7557";
        Assert.True(vm.StartStopCommand.CanExecute(null));
        vm.Port = 0;
        Assert.False(vm.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpstreamWithPath_ShowsDigestWarning()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = AppHarness.Create();
        var vm = h.Get<ProxyViewModel>();
        vm.BindAddress = "127.0.0.1";
        vm.Port = AppHarness.FreePort();
        vm.UpstreamUrl = acs.BaseUri + "base/path";
        Assert.True(await vm.StartAsync());
        Assert.Contains(h.Get<IBannerService>().Banners, b => b.Key == ProxyViewModel.DigestBannerKey && b.Kind == BannerKind.Warning);
        await vm.StopAsync();
    }
}

public class SimulatorViewModelTests
{
    [Fact]
    public async Task CreateEditSave_ReloadEquals()
    {
        await using var h = AppHarness.Create();
        var vm = h.Get<SimulatorViewModel>();
        vm.NewFromTr181Command.Execute(null);
        var ed = vm.SelectedProfile!;
        Assert.True(ed.IsDirty);
        ed.Name = "Lab router";
        ed.Oui = "a1b2c3";
        ed.SerialNumber = "LAB-1";
        ed.Username = "cpe";
        ed.Password = "pw";
        ed.Events.Single(e => e.Code == "M Reboot").IsSelected = true;
        ed.Events.Single(e => e.Code == "M Reboot").CommandKey = "rb1";
        ed.Parameters.Single(p => p.Name == "Device.Time.NTPServer1").Value = "ntp.lab";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(ed.IsDirty);
        Assert.Empty(ed.Errors);
        var saved = ed.ToProfile();

        await using var h2 = AppHarness.Create();
        // reuse the same profile dir
        var store = new JsonProfileStore(h.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonProfileStore>.Instance);
        var reloaded = Assert.Single(await store.LoadAllAsync(default));
        var json = System.Text.Json.JsonSerializer.Serialize(saved, SimulatorJsonContext.Default.CpeProfile);
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(reloaded, SimulatorJsonContext.Default.CpeProfile));
        Assert.Equal("A1B2C3", reloaded.DeviceId.OUI);
        Assert.Equal("plain:pw", reloaded.PasswordProtected);
        Assert.Contains(reloaded.DefaultEvents, e => e.EventCode == "M Reboot" && e.CommandKey == "rb1");

        await vm.LoadAsync();
        var again = Assert.Single(vm.Profiles);
        Assert.Equal("Lab router", again.Name);
        Assert.Equal("pw", again.Password);
        Assert.False(again.IsDirty);
    }

    [Fact]
    public async Task Validation_SurfacesErrors_NotSaved()
    {
        await using var h = AppHarness.Create();
        var vm = h.Get<SimulatorViewModel>();
        vm.NewFromTr098Command.Execute(null);
        var ed = vm.SelectedProfile!;
        ed.Oui = "XYZ";
        ed.SerialNumber = "";
        ed.Parameters[0].Name = ed.Parameters[1].Name;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(ed.HasErrors);
        Assert.Contains(ed.Errors, e => e.StartsWith("DeviceId.OUI", StringComparison.Ordinal));
        Assert.Contains(ed.Errors, e => e.StartsWith("DeviceId.SerialNumber", StringComparison.Ordinal));
        Assert.Contains(ed.Errors, e => e.Contains("Duplicate parameter name", StringComparison.Ordinal));
        Assert.Contains(h.Get<IBannerService>().Banners, b => b.Kind == BannerKind.Error);
        Assert.False(Directory.Exists(h.Paths.ProfilesDir) && Directory.EnumerateFiles(h.Paths.ProfilesDir).Any());
    }

    [Fact]
    public async Task ForcedParameter_InformFlagLocked()
    {
        await using var h = AppHarness.Create();
        var vm = h.Get<SimulatorViewModel>();
        vm.NewFromTr098Command.Execute(null);
        var forced = vm.SelectedProfile!.Parameters.First(p => p.IsForced);
        forced.IncludeInInform = false;
        Assert.True(forced.IncludeInInform);
    }

    [Fact]
    public async Task SendInformNow_AgainstFakeAcs_CreatesSimulatorSession()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = AppHarness.Create();
        var vm = h.Get<SimulatorViewModel>();
        vm.NewFromTr098Command.Execute(null);
        vm.SelectedProfile!.AcsUrl = acs.Url();
        await vm.SendInformNowCommand.ExecuteAsync(null);
        var store = h.Get<ISessionStore>();
        Assert.True(await TestUtil.WaitUntilAsync(() => store.GetAll().Any(s => s.Ended is not null)));
        var s = Assert.Single(store.GetAll());
        Assert.Equal(SessionSource.Simulator, s.Source);
        Assert.Equal(SessionOutcome.Completed, s.Outcome);
        var row = Assert.Single(vm.RunningCpes);
        Assert.True(await TestUtil.WaitUntilAsync(() => row.Sessions == 1));
        Assert.Equal(AcsTool.Simulator.Engine.CpeStateKind.Idle, row.State);
        Assert.Contains("1 running", vm.Summary, StringComparison.Ordinal);
        await vm.StopAllCommand.ExecuteAsync(null);
        Assert.Empty(vm.RunningCpes);
    }
}

public class MainAndSettingsTests
{
    [Fact]
    public async Task Settings_SaveAndReload_RoundTrips()
    {
        await using var h = AppHarness.Create();
        var vm = h.Get<SettingsViewModel>();
        vm.Port = 17547;
        vm.UpstreamUrl = "https://acs.lab:7548/acs";
        vm.CookieMode = CookieMode.ManagedPerCpe;
        vm.FailureMode = FailureMode.Abort;
        vm.MaxFeedItems = 500;
        vm.CaptureBudgetMiB = 64;
        Assert.True(await vm.SaveAsync());
        var loaded = await h.Get<ISettingsStore>().LoadAsync(default);
        Assert.Equal(17547, loaded.Proxy.Port);
        Assert.Equal("https://acs.lab:7548/acs", loaded.Proxy.UpstreamUrl);
        Assert.Equal(CookieMode.ManagedPerCpe, loaded.Proxy.CookieMode);
        Assert.Equal(FailureMode.Abort, loaded.Proxy.FailureMode);
        Assert.Equal(500, loaded.Capture.MaxFeedItems);
        Assert.Equal(64L * 1024 * 1024, loaded.Capture.CaptureBudgetBytes);
        Assert.Equal(loaded, h.Get<SettingsHolder>().Current);

        vm.Port = 0;
        Assert.False(await vm.SaveAsync());
        vm.CancelCommand.Execute(null);
        Assert.Equal(17547, vm.Port);
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(7547, vm.Port);
    }

    [Fact]
    public async Task MainViewModel_StatusAggregates_InitializeAndShutdown()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = AppHarness.Create();
        var main = h.Get<MainViewModel>();
        await main.InitializeAsync();
        Assert.Contains("Proxy: Stopped · active 0 · sessions 0", main.StatusText, StringComparison.Ordinal);
        Assert.Contains(main.Banners.Banners, b => b.Key == "firewall-hint");

        var store = h.Get<ISessionStore>();
        var id = Kit.StartSession(store, "M1");
        Kit.StartSession(store, "M2");
        store.EndSession(id, SessionOutcome.Completed);
        Assert.True(await TestUtil.WaitUntilAsync(() => main.TotalSessions == 2 && main.ActiveSessions == 1), main.StatusText);
        Assert.True(await TestUtil.WaitUntilAsync(() => main.Proxy.Feed.Items.Count == 2));
        Assert.Contains("sessions 2", main.StatusText, StringComparison.Ordinal);
        Assert.Contains("/ 256 MB", main.StatusText, StringComparison.Ordinal);

        main.Proxy.BindAddress = "127.0.0.1";
        main.Proxy.Port = AppHarness.FreePort();
        main.Proxy.UpstreamUrl = acs.BaseUri.ToString();
        Assert.True(await main.Proxy.StartAsync());
        Assert.StartsWith("Proxy: Running", main.StatusText, StringComparison.Ordinal);
        await main.ShutdownAsync();
        Assert.Equal(AcsTool.Proxy.ProxyState.Stopped, main.Proxy.State);
        Assert.True(File.Exists(Path.Combine(h.Paths.ConfigDir, "settings.json")));
        await main.ShutdownAsync(); // idempotent
    }

    [Fact]
    public async Task CorruptSettings_WarningBannerAndDefaults()
    {
        await using var h = AppHarness.Create();
        Directory.CreateDirectory(h.Paths.ConfigDir);
        await File.WriteAllTextAsync(Path.Combine(h.Paths.ConfigDir, "settings.json"), "{ broken");
        var main = h.Get<MainViewModel>();
        await main.InitializeAsync();
        Assert.Contains(main.Banners.Banners, b => b.Key == "settings-load" && b.Kind == BannerKind.Warning);
        Assert.Equal(7547, h.Get<SettingsHolder>().Current.Proxy.Port);
        await main.ShutdownAsync();
    }

    [Fact]
    public async Task PersistedSettings_ReachProxyVmStoreFeedAndEngine_StartKeepsThem()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = AppHarness.Create();
        var port = AppHarness.FreePort();
        var persisted = AppSettings.Default with
        {
            Proxy = AppSettings.Default.Proxy with { BindAddress = "127.0.0.1", Port = port, UpstreamUrl = acs.BaseUri.ToString(), CookieMode = CookieMode.ManagedPerCpe },
            Capture = AppSettings.Default.Capture with { MaxSessions = 10, MaxFeedItems = 50 },
            Simulator = AppSettings.Default.Simulator with { MaxConcurrentSessions = 3 },
        };
        await h.Get<ISettingsStore>().SaveAsync(persisted, default);

        // Same order as App.OnStartup: the view models (and the store/engine behind them) are resolved before settings load.
        var main = h.Get<MainViewModel>();
        await main.InitializeAsync();

        Assert.Equal("127.0.0.1", main.Proxy.BindAddress);
        Assert.Equal(port, main.Proxy.Port);
        Assert.Equal(acs.BaseUri.ToString(), main.Proxy.UpstreamUrl);
        Assert.Equal(CookieMode.ManagedPerCpe, main.Proxy.CookieMode);
        Assert.Equal(50, main.Proxy.Feed.MaxItems);
        Assert.Equal(50, main.Simulator.Feed.MaxItems);

        var store = h.Get<InMemorySessionStore>();
        Assert.Equal(10, store.Limits.MaxSessions);
        for (var i = 0; i < 30; i++)
        {
            store.EndSession(Kit.StartSession(store, $"E{i}"), SessionOutcome.Completed);
        }
        Assert.Equal(10, store.Stats.Sessions);

        var engine = h.Get<AcsTool.Simulator.Engine.SimulationEngine>();
        await engine.StartProfileAsync(DefaultProfiles.Tr098() with { AcsUrl = acs.BaseUri + "acs" }, sendBootNow: false, default);
        Assert.Equal(3, engine.MaxConcurrentSessions);
        await engine.StopAllAsync();

        Assert.True(await main.Proxy.StartAsync());
        Assert.Equal(port, h.Get<SettingsHolder>().Current.Proxy.Port);
        Assert.Equal(acs.BaseUri.ToString(), h.Get<SettingsHolder>().Current.Proxy.UpstreamUrl);
        await main.ShutdownAsync();
        var reloaded = await h.Get<ISettingsStore>().LoadAsync(default);
        Assert.Equal(port, reloaded.Proxy.Port);
        Assert.Equal(CookieMode.ManagedPerCpe, reloaded.Proxy.CookieMode);
    }

    [Fact]
    public async Task SettingsWindowSave_WhileStopped_UpdatesProxyTabAndLimits()
    {
        await using var h = AppHarness.Create();
        var main = h.Get<MainViewModel>();
        await main.InitializeAsync();
        var settingsVm = h.Get<SettingsViewModel>();
        settingsVm.Port = 18123;
        settingsVm.UpstreamUrl = "http://acs.changed:9000/acs";
        settingsVm.MaxFeedItems = 150;
        Assert.True(await settingsVm.SaveAsync());
        Assert.Equal(18123, main.Proxy.Port);
        Assert.Equal("http://acs.changed:9000/acs", main.Proxy.UpstreamUrl);
        Assert.Equal(150, main.Proxy.Feed.MaxItems);
        await main.ShutdownAsync();
    }
}

/// <summary>Simulates DPAPI data protected by another Windows user: "dpapi:" values cannot be decrypted.</summary>
public sealed class ForeignDpapiProtector : ISecretProtector
{
    public string Protect(string plain) => "plain:" + plain;

    public string Unprotect(string stored) => stored.StartsWith("dpapi:", StringComparison.Ordinal)
        ? throw new SecretUnprotectException("The stored password could not be decrypted for this Windows user; re-enter it.")
        : stored.StartsWith("plain:", StringComparison.Ordinal) ? stored[6..] : stored;
}

public class SecretTests
{
    [Fact]
    public async Task UndecryptablePassword_BannerShown_CiphertextKeptOnSave_StartRefused()
    {
        await using var h = AppHarness.Create(services: s => s.AddSingleton<ISecretProtector, ForeignDpapiProtector>());
        var profiles = h.Get<IProfileStore>();
        var original = DefaultProfiles.Tr098("Foreign", "SIMF0001") with { Username = "cpe", PasswordProtected = "dpapi:AAAA" };
        await profiles.SaveAsync(original, default);

        var sim = h.Get<SimulatorViewModel>();
        await sim.LoadAsync();
        var editor = Assert.Single(sim.Profiles);
        Assert.True(editor.PasswordUnreadable);
        Assert.Null(editor.Password);
        Assert.Contains(h.Get<IBannerService>().Banners, b => b.Key == SimulatorViewModel.PasswordBannerKey && b.Kind == BannerKind.Warning);

        editor.Name = "Foreign (renamed)";
        Assert.Equal("dpapi:AAAA", editor.ToProfile().PasswordProtected); // not wiped by an unrelated edit

        Assert.False(await sim.StartSelectedAsync(sendBootNow: false));
        Assert.Contains(h.Get<IBannerService>().Banners, b => b.Key == "sim-start" && b.Text.Contains("re-enter", StringComparison.Ordinal));

        editor.Password = "new-secret";
        Assert.False(editor.PasswordUnreadable);
        Assert.Equal("plain:new-secret", editor.ToProfile().PasswordProtected);
    }
}

