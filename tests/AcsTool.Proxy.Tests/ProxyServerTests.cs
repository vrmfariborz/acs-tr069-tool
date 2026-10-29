using System.Net;
using System.Net.Sockets;
using AcsTool.Core;
using AcsTool.Core.Settings;
using AcsTool.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AcsTool.Proxy.Tests;

public class ProxyServerTests
{
    [Fact]
    public async Task Start_Port0_ReportsBoundUrl()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        Assert.Equal(ProxyState.Running, h.Server.State);
        Assert.True(h.Server.BoundPort > 0);
        Assert.Contains(h.Server.BoundUrls, u => u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartStop_TenCycles_StateAndSessionsClean()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs);
        var states = new List<ProxyState>();
        h.Server.StateChanged += (_, s) => states.Add(s);
        using var client = ProxyHarness.CpeClient();
        for (var i = 0; i < 10; i++)
        {
            using var resp = await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("C" + i)));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            await h.Server.StopAsync(default);
            Assert.Equal(ProxyState.Stopped, h.Server.State);
            Assert.Equal(0, h.Tracker.ActiveCount);
            await h.Server.StartAsync(default);
            Assert.Equal(ProxyState.Running, h.Server.State);
        }
        Assert.Equal(40, states.Count);
        // every in-flight session was ended as Canceled by the stops
        Assert.All(h.Store.GetAll(), s => Assert.Equal(Core.Sessions.SessionOutcome.Canceled, s.Outcome));
    }

    [Fact]
    public async Task Start_PortInUse_ThrowsPortInUse()
    {
        await using var acs = await FakeAcs.StartAsync();
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var ex = await Assert.ThrowsAsync<ProxyBindException>(() => ProxyHarness.StartAsync(acs, s => s with { Port = port }));
            Assert.Equal(ProxyBindFailure.PortInUse, ex.Reason);
            Assert.Contains($"Port {port} is already in use", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Start_Localhost_BindsLoopback()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { BindAddress = "localhost" });
        Assert.Equal(ProxyState.Running, h.Server.State);
        Assert.Contains(h.Server.BoundUrls, u => u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_UnexpectedFailure_FaultedAndCleanedUp_ThenRestartable()
    {
        await using var acs = await FakeAcs.StartAsync();
        var factory = new ServiceCollection().AddLogging().AddAcsToolCore().AddAcsToolProxy().BuildServiceProvider().GetRequiredService<ProxyServerFactory>();
        await using var bad = factory.Create(new ProxySettings(BindAddress: "127.0.0.1", Port: 70_000, UpstreamUrl: acs.BaseUri.ToString()), new CaptureSettings());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bad.StartAsync(default));
        Assert.Equal(ProxyState.Faulted, bad.State);
        await bad.StopAsync(default); // nothing leaked: stop is a clean no-op
        Assert.Equal(ProxyState.Stopped, bad.State);
        await using var good = factory.Create(new ProxySettings(BindAddress: "127.0.0.1", Port: 0, UpstreamUrl: acs.BaseUri.ToString()), new CaptureSettings());
        await good.StartAsync(default);
        Assert.Equal(ProxyState.Running, good.State);
        await good.StopAsync(default);
    }

    [Fact]
    public async Task Start_CustomCaFileMissing_ProxyStartExceptionWithClearMessage()
    {
        await using var acs = await FakeAcs.StartAsync();
        var missing = Path.Combine(Path.GetTempPath(), "acstool-missing-" + Guid.NewGuid().ToString("N") + ".pem");
        var ex = await Assert.ThrowsAsync<ProxyStartException>(() => ProxyHarness.StartAsync(acs,
            s => s with { Tls = AcsTool.Core.Net.TlsOptions.Default with { Mode = AcsTool.Core.Net.TlsValidationMode.CustomCa, CustomCaPath = missing } }));
        Assert.Contains("could not be loaded", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("garbage")]
    public async Task Start_CustomCaFileEmptyOrGarbage_ProxyStartExceptionWithClearMessage(string kind)
    {
        // TesterBot chore/net10: with the old byte[] ctor an empty file threw NullReferenceException past PrepareTls' catch.
        await using var acs = await FakeAcs.StartAsync();
        var path = Path.Combine(Path.GetTempPath(), "acstool-ca-" + kind + "-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllBytesAsync(path, kind == "empty" ? [] : [0x30, 0x82, 0x01, 0x02, 0xFF, 0x00, 0x13]);
        try
        {
            var ex = await Assert.ThrowsAsync<ProxyStartException>(() => ProxyHarness.StartAsync(acs,
                s => s with { Tls = AcsTool.Core.Net.TlsOptions.Default with { Mode = AcsTool.Core.Net.TlsValidationMode.CustomCa, CustomCaPath = path } }));
            Assert.Contains("could not be loaded", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Start_InvalidBindAddress_AddressNotAvailable()
    {
        await using var acs = await FakeAcs.StartAsync();
        var ex = await Assert.ThrowsAsync<ProxyBindException>(() => ProxyHarness.StartAsync(acs, s => s with { BindAddress = "not-an-ip" }));
        Assert.Equal(ProxyBindFailure.AddressNotAvailable, ex.Reason);
    }

    [Fact]
    public async Task ServiceProvider_CorePlusProxy_StartsAndStopsProxy()
    {
        await using var acs = await FakeAcs.StartAsync();
        var services = new ServiceCollection().AddLogging().AddAcsToolProxy();
        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<ProxyServerFactory>();
        await using var server = factory.Create(new ProxySettings(BindAddress: "127.0.0.1", Port: 0, UpstreamUrl: acs.BaseUri.ToString()), new CaptureSettings());
        await server.StartAsync(default);
        Assert.Equal(ProxyState.Running, server.State);
        await server.StopAsync(default);
        Assert.Equal(ProxyState.Stopped, server.State);
        Assert.Same(sp.GetRequiredService<Core.Sessions.ISessionStore>(), sp.GetRequiredService<Core.Sessions.ISessionSink>());
    }
}
