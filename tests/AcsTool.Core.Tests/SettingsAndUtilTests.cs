using System.Text;
using AcsTool.Core.Export;
using AcsTool.Core.Net;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using AcsTool.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AcsTool.Core.Tests;

public class SettingsTests
{
    private static JsonSettingsStore Store(TempAppPaths p) =>
        new(p, new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)), NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public async Task Load_Missing_ReturnsDefaults()
    {
        using var p = new TempAppPaths();
        Assert.Equal(AppSettings.Default, await Store(p).LoadAsync(default));
    }

    [Fact]
    public async Task SaveLoad_RoundTrip()
    {
        using var p = new TempAppPaths();
        var store = Store(p);
        var s = AppSettings.Default with
        {
            Proxy = new ProxySettings(Port: 8000, UpstreamUrl: "https://acs.example.com:7548", FailureMode: FailureMode.Abort,
                Tls: new TlsOptions(TlsValidationMode.PinnedThumbprint, ["aabb"])),
            Capture = new CaptureSettings(MaxSessions: 10),
        };
        await store.SaveAsync(s, default);
        var loaded = await store.LoadAsync(default);
        Assert.Equal(s.Proxy with { Tls = null }, loaded.Proxy with { Tls = null });
        Assert.Equal(["aabb"], loaded.Proxy.Tls!.PinnedSha256);
        Assert.Equal(s.Capture, loaded.Capture);
        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.Contains("\"failureMode\": \"Abort\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public async Task Load_Corrupt_BacksUpAndReturnsDefaults()
    {
        using var p = new TempAppPaths();
        var store = Store(p);
        Directory.CreateDirectory(p.ConfigDir);
        await File.WriteAllTextAsync(store.FilePath, "{ not json");
        Assert.Equal(AppSettings.Default, await store.LoadAsync(default));
        Assert.True(File.Exists(store.FilePath + ".bad-20261028120000"));
        Assert.NotNull(store.LastLoadProblem);
    }

    [Fact]
    public async Task Load_PartialWithUnknownProperties_IgnoresUnknownAndDefaultsMissing()
    {
        using var p = new TempAppPaths();
        var store = Store(p);
        Directory.CreateDirectory(p.ConfigDir);
        await File.WriteAllTextAsync(store.FilePath, "{ \"schemaVersion\": 1, \"futureThing\": 5, \"proxy\": { \"port\": 9000, \"whatever\": true } }");
        var s = await store.LoadAsync(default);
        Assert.Equal(9000, s.Proxy.Port);
        Assert.Equal("0.0.0.0", s.Proxy.BindAddress);
        Assert.Equal(30, s.Proxy.RequestTimeoutSeconds);
        Assert.Equal(AppSettings.Default.Capture, s.Capture);
    }

    [Fact]
    public void PlainSecretProtector_RoundTrip()
    {
        var p = new PlainSecretProtector();
        Assert.Equal("plain:pw", p.Protect("pw"));
        Assert.Equal("pw", p.Unprotect(p.Protect("pw")));
    }
}

public class UtilTests
{
    [Fact]
    public void AuthRedactor_Basic_ShowsUserOnly()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));
        Assert.Equal("Basic <redacted user=alice>", AuthRedactor.Redact("Basic " + token));
    }

    [Fact]
    public void AuthRedactor_Digest_ShowsUsernameOnly()
    {
        var r = AuthRedactor.Redact("Digest username=\"alice\", realm=\"acs\", nonce=\"n\", uri=\"/acs\", response=\"deadbeef\"");
        Assert.Equal("Digest username=\"alice\" <redacted>", r);
        Assert.DoesNotContain("deadbeef", r, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlPretty_FormatsAndRejectsGarbage()
    {
        Assert.True(XmlPretty.TryFormat("<a><b>1</b></a>", out var pretty));
        Assert.Contains("\n", pretty, StringComparison.Ordinal);
        Assert.False(XmlPretty.TryFormat("<a>", out var same));
        Assert.Equal("<a>", same);
    }

    [Fact]
    public void TlsPins_Normalize()
    {
        Assert.Equal("aabb01", TlsValidatorFactory.NormalizePin("AA:BB 01"));
    }

    [Fact]
    public void HeaderUtil_EndToEnd_RemovesHopByHopAndConnectionListed()
    {
        var h = new List<KeyValuePair<string, string>>
        {
            new("Connection", "keep-alive, X-Foo"), new("Keep-Alive", "timeout=5"), new("X-Foo", "1"),
            new("Transfer-Encoding", "chunked"), new("SOAPAction", "\"\""), new("Cookie", "a=b"),
        };
        Assert.Equal(new[] { "SOAPAction", "Cookie" }, HeaderUtil.EndToEnd(h).Select(x => x.Key));
    }

    [Fact]
    public void SeededRandom_Deterministic()
    {
        Assert.Equal(new SeededRandom(1).NextDouble(), new SeededRandom(1).NextDouble());
    }
}
