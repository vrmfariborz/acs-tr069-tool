using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AcsTool.Testing;

namespace AcsTool.Proxy.Tests;

/// <summary>PT0.4: the proxy records downstream and upstream transport endpoints on every exchange.</summary>
public class EndpointTests
{
    /// <summary>A CPE client that remembers the local endpoint of every socket it opens.</summary>
    private sealed class RecordingCpe : IDisposable
    {
        private readonly HttpClient _client;

        public RecordingCpe()
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (ctx, ct) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                    Locals.Enqueue((IPEndPoint)socket.LocalEndPoint!);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        }

        public ConcurrentQueue<IPEndPoint> Locals { get; } = new();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage m) => _client.SendAsync(m);

        public void Dispose() => _client.Dispose();
    }

    private static FakeAcsScript InformThenEmpty(FakeAcsScript s) => s.ExpectInform().RespondInformResponse().ExpectEmpty().End204();

    [Fact]
    public async Task FullSession_EveryExchangeHasDownstreamAndUpstreamEndpoints()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => InformThenEmpty(s));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var cpe = new RecordingCpe();

        (await cpe.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()))).Dispose();
        (await cpe.SendAsync(ProxyHarness.Post(h.Url(), null))).Dispose();

        var s = await h.WaitForEndedAsync();
        Assert.Equal(2, s.Exchanges.Count);
        var cpeLocal = Assert.Single(cpe.Locals);
        for (var i = 0; i < s.Exchanges.Count; i++)
        {
            var e = s.Exchanges[i].Endpoints;
            Assert.NotNull(e);
            Assert.False(string.IsNullOrEmpty(e.DownstreamConnectionId));
            Assert.Equal(cpeLocal, e.DownstreamRemote);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, h.Server.BoundPort), e.DownstreamLocal);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, acs.Port), e.UpstreamRemote);
            Assert.NotNull(e.UpstreamLocal);
            Assert.Equal(acs.Received[i].RemoteEndPoint, e.UpstreamLocal); // the port FakeAcs saw as remote for this request
            Assert.Equal(1, e.UpstreamConnectionSerial);
        }
    }

    [Fact]
    public async Task CpeReconnectsMidSession_LaterExchangeHasDifferentDownstreamRemote()
    {
        await using var acs = await FakeAcs.StartAsync(o => o.Script = s => InformThenEmpty(s));
        await using var h = await ProxyHarness.StartAsync(acs);
        using var cpe = new RecordingCpe();

        // "Connection: close" on the Inform makes the CPE client open a new socket for the empty POST.
        (await cpe.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform(), ("Connection", "close")))).Dispose();
        (await cpe.SendAsync(ProxyHarness.Post(h.Url(), null))).Dispose();

        var s = await h.WaitForEndedAsync();
        Assert.Single(h.Store.GetAll());
        Assert.Equal(2, s.Exchanges.Count);
        Assert.Equal(2, cpe.Locals.Count);
        var first = s.Exchanges[0].Endpoints!;
        var second = s.Exchanges[1].Endpoints!;
        Assert.Equal(cpe.Locals.ElementAt(0), first.DownstreamRemote);
        Assert.Equal(cpe.Locals.ElementAt(1), second.DownstreamRemote);
        Assert.NotEqual(first.DownstreamRemote, second.DownstreamRemote);
        Assert.NotEqual(first.DownstreamConnectionId, second.DownstreamConnectionId);
        Assert.Equal(acs.Received[1].RemoteEndPoint, second.UpstreamLocal);
    }

    [Fact]
    public async Task UpstreamConnectRefused_DownstreamOnly_UpstreamNull()
    {
        await using var acs = await FakeAcs.StartAsync();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var h = await ProxyHarness.StartAsync(acs, upstreamOverride: $"http://127.0.0.1:{deadPort}");
        using var cpe = new RecordingCpe();

        (await cpe.SendAsync(ProxyHarness.Post(h.Url(), ProxyHarness.Inform()))).Dispose();

        var e = (await h.WaitForEndedAsync()).Exchanges.Single().Endpoints;
        Assert.NotNull(e);
        Assert.Equal(cpe.Locals.Single(), e.DownstreamRemote);
        Assert.Equal(h.Server.BoundPort, e.DownstreamLocal!.Port);
        Assert.Null(e.UpstreamLocal);
        Assert.Null(e.UpstreamRemote);
        Assert.Null(e.UpstreamConnectionSerial);
    }

    [Fact]
    public async Task OversizedBody_413Orphan_DownstreamOnly()
    {
        await using var acs = await FakeAcs.StartAsync();
        await using var h = await ProxyHarness.StartAsync(acs, s => s with { MaxRequestBodyBytes = 1000 });
        using var cpe = new RecordingCpe();

        using var r = await cpe.SendAsync(ProxyHarness.Post(h.Url(), new byte[5000]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        var e = (await h.WaitForEndedAsync()).Exchanges.Single().Endpoints;
        Assert.NotNull(e);
        Assert.Equal(cpe.Locals.Single(), e.DownstreamRemote);
        Assert.Equal(h.Server.BoundPort, e.DownstreamLocal!.Port);
        Assert.Null(e.UpstreamLocal);
        Assert.Null(e.UpstreamRemote);
        Assert.Null(e.UpstreamConnectionSerial);
    }

    [Fact]
    public void IncomingRequest_EndPoints_ParseAndUnknown()
    {
        var known = new IncomingRequest("POST", "/acs", [], default, "192.0.2.5", 40000, "c1", false) { LocalIp = "192.0.2.1", LocalPort = 7547 };
        var unknown = new IncomingRequest("POST", "/acs", [], default, "unknown", 0, "c2", false);

        Assert.Equal(IPEndPoint.Parse("192.0.2.5:40000"), known.RemoteEndPoint);
        Assert.Equal(IPEndPoint.Parse("192.0.2.1:7547"), known.LocalEndPoint);
        Assert.Null(unknown.RemoteEndPoint);
        Assert.Null(unknown.LocalEndPoint);
    }
}
