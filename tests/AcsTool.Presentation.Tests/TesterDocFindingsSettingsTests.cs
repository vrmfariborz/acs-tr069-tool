using AcsTool.Core.Net;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Proxy;
using AcsTool.Presentation.Settings;

namespace AcsTool.Presentation.Tests;

/// <summary>TesterBot, fix/doc-findings (b8d33bb), doc review (d): what Restore defaults keeps and resets, end to end.</summary>
public class TesterDocFindingsSettingsTests
{
    private static AppSettings Customized(AppSettings d) => d with
    {
        Proxy = d.Proxy with
        {
            Port = 17001, UpstreamUrl = "http://acs.lab:9000", CookieMode = CookieMode.ManagedPerCpe, SessionIdleTimeoutSeconds = 99,
            Tls = new TlsOptions(TlsValidationMode.IgnoreErrors, ["ab:cd"], "/etc/ca.pem"), MaxRequestBodyBytes = 3_000_000,
        },
        Capture = d.Capture with { MaxSessions = 123, MaxFeedItems = 77 },
        Simulator = d.Simulator with { MaxConcurrentSessions = 5, SoapAction = SoapActionMode.MethodName, UserAgent = "X/1" },
        Ui = d.Ui with { NewestOnTop = false },
        Logging = d.Logging with { FileLoggingEnabled = false },
    };

    [Fact]
    public async Task RestoreDefaults_Save_ResetsShownFields_KeepsHiddenOnes_AndTheProxyTabFollows()
    {
        await using var h = AppHarness.Create();
        var holder = h.Get<SettingsHolder>();
        holder.Current = Customized(holder.Current);
        var proxyTab = h.Get<ProxyViewModel>();
        Assert.Equal(TlsValidationMode.IgnoreErrors, proxyTab.TlsMode);
        var vm = h.Get<SettingsViewModel>();
        vm.Refresh();

        vm.RestoreDefaultsCommand.Execute(null);
        Assert.True(await vm.SaveAsync());
        var saved = await h.Get<ISettingsStore>().LoadAsync(default);

        // reset: everything the window shows
        Assert.Equal(AppSettings.Default.Proxy with { Tls = saved.Proxy.Tls, MaxRequestBodyBytes = saved.Proxy.MaxRequestBodyBytes }, saved.Proxy);
        Assert.Equal(AppSettings.Default.Capture, saved.Capture);
        // kept: what it doesn't show
        Assert.Equal(TlsValidationMode.IgnoreErrors, saved.Proxy.Tls!.Mode);
        Assert.Equal(["ab:cd"], saved.Proxy.Tls.PinnedSha256!);
        Assert.Equal("/etc/ca.pem", saved.Proxy.Tls.CustomCaPath);
        Assert.Equal(3_000_000, saved.Proxy.MaxRequestBodyBytes);
        Assert.Equal(5, saved.Simulator.MaxConcurrentSessions);
        Assert.Equal(SoapActionMode.MethodName, saved.Simulator.SoapAction);
        Assert.False(saved.Ui.NewestOnTop);
        Assert.False(saved.Logging.FileLoggingEnabled);

        // The Proxy tab follows the saved settings while stopped: port/upstream/cookies reset, the TLS drop-down keeps IgnoreErrors.
        Assert.True(await AcsTool.Testing.TestUtil.WaitUntilAsync(() => proxyTab.Port == 7547), $"proxy tab port {proxyTab.Port}");
        Assert.Equal("http://localhost:7557", proxyTab.UpstreamUrl);
        Assert.Equal(CookieMode.PassThrough, proxyTab.CookieMode);
        Assert.Equal(TlsValidationMode.IgnoreErrors, proxyTab.TlsMode);
    }

    [Fact]
    public async Task RestoreDefaults_ThenCancel_DiscardsTheReset()
    {
        await using var h = AppHarness.Create();
        var holder = h.Get<SettingsHolder>();
        holder.Current = Customized(holder.Current);
        var vm = h.Get<SettingsViewModel>();
        vm.Refresh();
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(7547, vm.Port);
        vm.CancelCommand.Execute(null);
        Assert.Equal(17001, vm.Port);
        var built = vm.Build();
        Assert.Equal("http://acs.lab:9000", built.Proxy.UpstreamUrl);
        Assert.Equal(123, built.Capture.MaxSessions);
        Assert.Equal(TlsValidationMode.IgnoreErrors, built.Proxy.Tls!.Mode);
    }
}
