using System.Net;
using AcsTool.Core.Model;
using AcsTool.Core.Sessions;
using AcsTool.Testing;

namespace AcsTool.Proxy.Tests;

/// <summary>TesterBot: independent probes of the review fixes on fix/review-m0-m4 (M3/BUG-02 guards, m1, m13 negative cases).</summary>
public class TesterFixBranchTests
{
    private static async Task RunUntilAwaitingFinalEmpty(ProxyHarness h, HttpClient c, string serial, CwmpMessage answer)
    {
        (await c.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform(serial)))).Dispose();
        using var r2 = await c.SendAsync(ProxyHarness.Post(h.Url(), null));
        var id = ProxyHarness.IdOf(await r2.Content.ReadAsByteArrayAsync());
        (await c.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Soap(answer, id)))).Dispose();
    }

    private static FakeAcsScript GpvScript(FakeAcsScript s) => s
        .ExpectInform().RespondInformResponse()
        .ExpectEmpty().Send(new GetParameterValuesMessage(["InternetGatewayDevice.DeviceInfo."]))
        .ExpectAny().End204();

    [Fact]
    public async Task M3Guard_NewInformWhileOldSessionGenuinelyMidSession_OldStillSuperseded()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => GpvScript(s));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("MID")))).Dispose();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), null))).Dispose(); // ACS sent GPV; CPE never answers
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("MID")))).Dispose(); // CPE restarts
        var first = h.Store.GetAll().OrderBy(s => s.Started).First();
        Assert.Equal(SessionOutcome.Superseded, first.Outcome);
    }

    [Fact]
    public async Task M3Fix_NewInformDuringGrace_AfterCpeFault_OldEndsCpeFault()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => GpvScript(s));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var client = ProxyHarness.CpeClient();
        await RunUntilAwaitingFinalEmpty(h, client, "GFLT", new CwmpFault("Client", "CWMP fault", 9005, "Invalid parameter name", []));
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("GFLT")))).Dispose();
        var first = h.Store.GetAll().OrderBy(s => s.Started).First();
        Assert.Equal(SessionOutcome.CpeFault, first.Outcome);
    }

    [Fact]
    public async Task M1Fix_ProxyStoppedDuringMidSessionExchange_SessionCanceled_FirstExchangeClean()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => s
            .ExpectInform().RespondInformResponse()
            .ExpectEmpty().Delay(TimeSpan.FromSeconds(10)).End204());
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { RequestTimeoutSeconds = 20 });
        using var client = ProxyHarness.CpeClient();
        (await client.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("STOP")))).EnsureSuccessStatusCode();
        var pending = client.SendAsync(ProxyHarness.Post(h.Url(), null));
        Assert.True(await TestUtil.WaitUntilAsync(() => acs.Received.Count == 2));
        await h.Server.StopAsync(CancellationToken.None);
        try { (await pending).Dispose(); } catch (HttpRequestException) { } catch (TaskCanceledException) { }
        var s = await h.WaitForEndedAsync();
        Assert.Equal(SessionOutcome.Canceled, s.Outcome);
        Assert.Null(s.Exchanges[0].Error);
    }

    [Theory]
    [InlineData("/", "fakeacs-sid=1; Path=/")]
    [InlineData("/acs", "fakeacs-sid=1; Path=/acs")]
    public async Task M13Fix_MatchingCookiePath_NotRewritten(string cookiePath, string expected)
    {
        await using var acs = await FakeAcs.StartAsync(o => o.SetCookie = "fakeacs-sid={n}; Path=" + cookiePath);
        await using var h = await ProxyHarness.StartAsync(acs);
        using var c = ProxyHarness.CpeClient(useCookies: false);
        using var r = await c.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform("PATH")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(expected, r.Headers.GetValues("Set-Cookie").Single());
        Assert.DoesNotContain(h.Store.GetAll().Single().Exchanges[0].Notes, n => n.Contains("Path", StringComparison.Ordinal));
    }
}
