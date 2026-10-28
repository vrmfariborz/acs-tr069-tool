using System.Net;
using System.Net.Http;
using System.Text;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcsTool.Proxy.Tests;

/// <summary>Real ProxyServer on 127.0.0.1:0 in front of a FakeAcs, plus an HttpClient acting as the CPE.</summary>
public sealed class ProxyHarness : IAsyncDisposable
{
    private ProxyHarness(ProxyServer server, InMemorySessionStore store, FakeAcs acs)
    {
        Server = server;
        Store = store;
        Acs = acs;
    }

    public ProxyServer Server { get; }
    public InMemorySessionStore Store { get; }
    public FakeAcs Acs { get; }
    public CpeSessionTracker Tracker => Server.Tracker;

    public string Url(string path = "/acs") => $"http://127.0.0.1:{Server.BoundPort}{path}";

    public static async Task<ProxyHarness> StartAsync(FakeAcs acs, Func<ProxySettings, ProxySettings>? configure = null, TimeProvider? time = null,
        string? upstreamOverride = null)
    {
        var settings = new ProxySettings(BindAddress: "127.0.0.1", Port: 0, UpstreamUrl: upstreamOverride ?? acs.BaseUri.ToString().TrimEnd('/'),
            ConnectTimeoutSeconds: 5, RequestTimeoutSeconds: 5);
        settings = configure?.Invoke(settings) ?? settings;
        time ??= TimeProvider.System;
        var store = new InMemorySessionStore(new CaptureSettings(), time);
        var factory = new ProxyServerFactory(new CwmpParser(), new MessageSummarizer(), new UpstreamHandlerFactory(), store, time, NullLoggerFactory.Instance);
        var server = factory.Create(settings, new CaptureSettings());
        await server.StartAsync(CancellationToken.None);
        return new ProxyHarness(server, store, acs);
    }

    public static HttpClient CpeClient(bool useCookies = true)
    {
        var handler = new SocketsHttpHandler { UseCookies = useCookies, CookieContainer = new CookieContainer(), UseProxy = false, AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static readonly CwmpMessageBuilder Builder = new();

    public static byte[] Inform(string serial = "SN1", string id = "1", string oui = "00D09E") =>
        Builder.Build(new InformBuilder().Create(new DeviceIdStruct("Acme", oui, "IGD", serial), [new EventStruct(EventCodes.Boot)],
            [new ParameterValue("InternetGatewayDevice.DeviceInfo.SoftwareVersion", "1.0")], 0, DateTimeOffset.UtcNow), CwmpVersion.V1_0, new CwmpHeader(id));

    public static HttpRequestMessage Post(string url, byte[]? body, params (string Key, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body ?? []) };
        if (body is { Length: > 0 })
        {
            req.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
            req.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
        }
        foreach (var (k, v) in headers)
        {
            if (!req.Headers.TryAddWithoutValidation(k, v))
            {
                req.Content.Headers.TryAddWithoutValidation(k, v);
            }
        }
        return req;
    }

    public static byte[] Soap(CwmpMessage m, string id) => Builder.Build(m, CwmpVersion.V1_0, new CwmpHeader(id));

    public static string IdOf(byte[] body) => new CwmpParser().Parse(body).Envelope?.Header.Id ?? "";

    public async Task<SessionSnapshot> WaitForEndedAsync(int expectedSessions = 1)
    {
        Assert.True(await TestUtil.WaitUntilAsync(() => Store.GetAll().Count(s => s.Ended is not null) >= expectedSessions),
            "sessions: " + string.Join(" | ", Store.GetAll().Select(s => $"{s.Outcome} x{s.Exchanges.Count}")));
        return Store.GetAll().First(s => s.Ended is not null);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
    }

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
