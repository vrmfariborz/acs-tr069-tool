using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Detail;
using AcsTool.Presentation.Feed;

namespace AcsTool.Presentation.Tests;

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

public sealed class CountingFormatter : IXmlFormatter
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public string Format(string xml)
    {
        Interlocked.Increment(ref _calls);
        return XmlPretty.Instance.Format(xml);
    }
}

public static class Kit
{
    public static InMemorySessionStore Store(TimeProvider? time = null, CaptureSettings? capture = null) =>
        new(capture ?? new CaptureSettings(), time ?? TimeProvider.System);

    public static SessionFeedViewModel Feed(ISessionStore store, SessionSource? source = SessionSource.Proxy, int max = 2000, bool newestOnTop = true) =>
        new(store, source, max, newestOnTop, new SessionDetailViewModel(new CountingFormatter(), new FakeClipboard()));

    public static Guid StartSession(ISessionSink sink, string serial, SessionSource source = SessionSource.Proxy) =>
        sink.StartSession(new SessionStart(source, "192.168.1.50:5000", "http://acs", new DeviceIdStruct("A", "00D09E", "P", serial),
            [new EventStruct(EventCodes.Boot)], CwmpVersion.V1_0, null));

    public static FeedBatch Batch(ISessionStore store, params (Guid Id, SessionChangeKind Kind)[] changes) =>
        new(changes.Select(c => new FeedChange(c.Id, c.Kind, c.Kind == SessionChangeKind.Removed ? null : store.Get(c.Id))).ToList(), false, null);

    public static MessageSummary Summary(byte[] body) => new MessageSummarizer().Summarize(new CwmpParser().Parse(body));
}
