using System.Text;
using AcsTool.Core.Model;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Detail;
using AcsTool.Testing;

namespace AcsTool.Presentation.Tests;

public class DetailTests
{
    private readonly InMemorySessionStore _store = Kit.Store(capture: new CaptureSettings(MaxCapturedBodyBytes: 1024 * 1024));

    private SessionSnapshot SessionWithInform(int maxBody = 1024 * 1024, byte[]? body = null)
    {
        var id = Kit.StartSession(_store, "DET");
        body ??= Fixtures.Load("inform-boot-cwmp10.xml");
        var req = HttpMessageCapture.From("POST", "/acs", null,
            [new("Content-Type", "text/xml"), new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret")))], body, maxBody);
        var seq = _store.AddExchange(id, req, Kit.Summary(body), DateTimeOffset.UtcNow);
        var respBody = Fixtures.Load("informresponse-cwmp10.xml");
        _store.CompleteExchange(id, seq, HttpMessageCapture.From(null, "http://acs/acs", 200, [new("Content-Type", "text/xml")], respBody, maxBody),
            Kit.Summary(respBody), null, new ExchangeTimings(DateTimeOffset.UtcNow, Total: TimeSpan.FromMilliseconds(42)));
        return _store.Get(id)!;
    }

    [Fact]
    public void Load_PopulatesHeaderAndSelectedExchange()
    {
        var clipboard = new FakeClipboard();
        var vm = new SessionDetailViewModel(new CountingFormatter(), clipboard);
        vm.Load(SessionWithInform());
        Assert.True(vm.HasSession);
        Assert.Equal("00D09E-DET", vm.DeviceKey);
        Assert.Equal("cwmp-1-0", vm.VersionText);
        Assert.Contains("1 BOOT", vm.EventsText, StringComparison.Ordinal);
        var ex = Assert.Single(vm.Exchanges);
        Assert.Same(ex, vm.SelectedExchange);
        Assert.Equal(CwmpMessageKind.Inform, ex.Record.RequestSummary.Kind);
        Assert.StartsWith("200", ex.ResponseText, StringComparison.Ordinal);
        Assert.Equal("42", ex.Milliseconds);
        Assert.NotEmpty(ex.RequestFields);
        Assert.Contains("<cwmp:Inform>", ex.RequestRaw, StringComparison.Ordinal);
        Assert.Null(ex.RequestTruncatedBanner);
        vm.CopyRequestCommand.Execute(null);
        Assert.Equal(ex.RequestRaw, clipboard.Text);
    }

    [Fact]
    public void Authorization_MaskedUntilRevealed()
    {
        var vm = new SessionDetailViewModel(new CountingFormatter(), new FakeClipboard());
        vm.Load(SessionWithInform());
        var auth = vm.SelectedExchange!.RequestHeaders.Single(h => h.Name == "Authorization");
        Assert.Equal("Basic <redacted user=alice>", auth.Value);
        vm.RevealAuthorization = true;
        Assert.StartsWith("Basic YWxp", vm.SelectedExchange.RequestHeaders.Single(h => h.Name == "Authorization").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrettyPrint_InvokedOnce_Cached()
    {
        var formatter = new CountingFormatter();
        var vm = new SessionDetailViewModel(formatter, new FakeClipboard());
        vm.Load(SessionWithInform());
        var ex = vm.SelectedExchange!;
        var p1 = await ex.GetRequestPrettyAsync();
        var p2 = await ex.GetRequestPrettyAsync();
        Assert.Same(p1, p2);
        Assert.Equal(1, formatter.Calls);
        Assert.Contains("\n", p1, StringComparison.Ordinal);
        ex.Pretty = true;
        Assert.True(await TestUtil.WaitUntilAsync(() => ex.RequestDisplay == p1 && ex.ResponseDisplay != ex.ResponseRaw));
        Assert.Equal(2, formatter.Calls); // request reused from the cache; response formatted once
        ex.Pretty = false;
        Assert.True(await TestUtil.WaitUntilAsync(() => ex.RequestDisplay == ex.RequestRaw));
        ex.Pretty = true;
        Assert.True(await TestUtil.WaitUntilAsync(() => ex.RequestDisplay == p1));
        Assert.Equal(2, formatter.Calls);
    }

    [Fact]
    public void TruncatedBody_ShowsBanner()
    {
        var big = Encoding.UTF8.GetBytes("<x>" + new string('a', 3 * 1024 * 1024) + "</x>");
        var vm = new SessionDetailViewModel(new CountingFormatter(), new FakeClipboard());
        vm.Load(SessionWithInform(maxBody: 1024 * 1024, body: big));
        Assert.Equal("Captured 1 MiB of 3 MiB", vm.SelectedExchange!.RequestTruncatedBanner);
    }

    [Fact]
    public void Refresh_SameSession_KeepsSelectionAndAppendsExchanges()
    {
        var vm = new SessionDetailViewModel(new CountingFormatter(), new FakeClipboard());
        var s = SessionWithInform();
        vm.Load(s);
        var selected = vm.SelectedExchange;
        _store.AddExchange(s.Id, HttpMessageCapture.From("POST", "/acs", null, [], default, 0), Core.Parsing.MessageSummary.None, DateTimeOffset.UtcNow);
        vm.Refresh(_store.Get(s.Id)!);
        Assert.Equal(2, vm.Exchanges.Count);
        Assert.Same(selected, vm.SelectedExchange);
        Assert.True(vm.Exchanges[1].IsPending);
        Assert.Equal("(empty)", vm.Exchanges[1].RequestText);
    }
}
