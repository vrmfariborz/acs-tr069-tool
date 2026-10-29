using System.Collections.ObjectModel;
using System.Globalization;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Export;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Util;
using AcsTool.Presentation.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcsTool.Presentation.Detail;

/// <summary>Detail pane for the selected session (plan §4.3).</summary>
public sealed partial class SessionDetailViewModel(IXmlFormatter formatter, IClipboardService clipboard) : ObservableObject
{
    [ObservableProperty]
    private SessionSnapshot? _session;

    [ObservableProperty]
    private ExchangeItemViewModel? _selectedExchange;

    [ObservableProperty]
    private bool _revealAuthorization;

    public ObservableCollection<ExchangeItemViewModel> Exchanges { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public bool HasSession => Session is not null;

    public string DeviceKey => Session?.DeviceId?.Key ?? Session?.Label ?? "?";

    public string ClientEndpoint => Session?.ClientEndpoint ?? "";

    public string TargetUrl => Session?.TargetUrl ?? "";

    public string StartedText => Session?.Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";

    public string DurationText => Session?.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : Session is null ? "" : "in progress";

    public string OutcomeText => Session is null ? "" : Feed.SessionItemViewModel.Badge(Session.Outcome);

    public string VersionText => Session is null ? "" : CwmpNamespaces.Display(Session.Version);

    public string EventsText => Session is null ? "" : string.Join("; ",
        Session.Events.Select(e => string.IsNullOrEmpty(e.CommandKey) ? e.EventCode : $"{e.EventCode} ({e.CommandKey})"));

    public string? ErrorText => Session?.LastError?.ToString();

    public string? TlsText => Session?.Tls is { } t ? $"{t.Subject} · issuer {t.Issuer} · {t.PolicyErrors}" : null;

    public void Load(SessionSnapshot? snapshot)
    {
        Session = snapshot;
        Exchanges.Clear();
        if (snapshot is not null)
        {
            foreach (var e in snapshot.Exchanges)
            {
                Exchanges.Add(new ExchangeItemViewModel(e, formatter, () => RevealAuthorization));
            }
        }
        SelectedExchange = Exchanges.FirstOrDefault();
        RefreshHeader();
    }

    /// <summary>Applies a newer snapshot of the same session, keeping the selected exchange.</summary>
    public void Refresh(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Session?.Id != snapshot.Id)
        {
            Load(snapshot);
            return;
        }
        Session = snapshot;
        for (var i = 0; i < snapshot.Exchanges.Count; i++)
        {
            var rec = snapshot.Exchanges[i];
            if (i < Exchanges.Count)
            {
                if (!ReferenceEquals(Exchanges[i].Record, rec))
                {
                    Exchanges[i].Update(rec);
                }
            }
            else
            {
                Exchanges.Add(new ExchangeItemViewModel(rec, formatter, () => RevealAuthorization));
            }
        }
        SelectedExchange ??= Exchanges.FirstOrDefault();
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        Notes.Clear();
        foreach (var n in Session?.Notes ?? [])
        {
            Notes.Add(n);
        }
        foreach (var name in new[] { nameof(HasSession), nameof(DeviceKey), nameof(ClientEndpoint), nameof(TargetUrl), nameof(StartedText),
            nameof(DurationText), nameof(OutcomeText), nameof(VersionText), nameof(EventsText), nameof(ErrorText), nameof(TlsText) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnRevealAuthorizationChanged(bool value)
    {
        foreach (var e in Exchanges)
        {
            e.RefreshHeaders();
        }
    }

    [RelayCommand]
    private void CopyRequest()
    {
        if (SelectedExchange is { } e)
        {
            clipboard.SetText(e.RequestRaw);
        }
    }

    [RelayCommand]
    private void CopyResponse()
    {
        if (SelectedExchange is { } e)
        {
            clipboard.SetText(e.ResponseRaw);
        }
    }
}

public sealed record HeaderRow(string Name, string Value);

public sealed record FieldRow(string Name, string Value);

/// <summary>One exchange row + its Summary/Headers/Raw tabs. Raw text and pretty-printing are lazy and cached.</summary>
public sealed partial class ExchangeItemViewModel : ObservableObject
{
    private readonly IXmlFormatter _formatter;
    private readonly Func<bool> _reveal;
    private string? _requestRaw;
    private string? _responseRaw;
    private Task<string>? _requestPretty;
    private Task<string>? _responsePretty;

    private string? _requestDisplay;
    private string? _responseDisplay;

    [ObservableProperty]
    private bool _pretty;

    public ExchangeItemViewModel(ExchangeRecord record, IXmlFormatter formatter, Func<bool> reveal)
    {
        Record = record;
        _formatter = formatter;
        _reveal = reveal;
    }

    public ExchangeRecord Record { get; private set; }

    public int Seq => Record.Seq;

    public string RequestText => Record.RequestSummary.Kind == Core.Model.CwmpMessageKind.Empty ? "(empty)" : Record.RequestSummary.OneLine;

    public string ResponseText => Record.Response is { } r
        ? $"{r.StatusCode} {(Record.ResponseSummary is { Kind: not Core.Model.CwmpMessageKind.Empty } s ? s.OneLine : "")}".Trim()
        : Record.Error?.ShortText ?? "…";

    public string Milliseconds => Record.Timings.Total is { } t ? t.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "";

    public bool IsPending => Record.IsPending;

    public bool HasError => Record.Error is not null && Record.Error.Kind != Core.Net.UpstreamErrorKind.HttpStatus;

    public string? ErrorText => Record.Error?.ToString();

    public IReadOnlyList<FieldRow> RequestFields => Fields(Record.RequestSummary);

    public IReadOnlyList<FieldRow> ResponseFields => Record.ResponseSummary is { } s ? Fields(s) : [];

    public IReadOnlyList<HeaderRow> RequestHeaders => Headers(Record.Request);

    public IReadOnlyList<HeaderRow> ResponseHeaders => Record.Response is { } r ? Headers(r) : [];

    /// <summary>Per-exchange proxy notes (path rewrite, cookie handling, ...), shown on the Summary tab (doc review g).</summary>
    public IReadOnlyList<string> Notes => Record.Notes;

    public bool HasNotes => Record.Notes.Count > 0;

    public string RequestRaw => _requestRaw ??= Decode(Record.Request);

    public string ResponseRaw => _responseRaw ??= Record.Response is { } r ? Decode(r) : "";

    public string? RequestTruncatedBanner => Banner(Record.Request);

    public string? ResponseTruncatedBanner => Record.Response is { } r ? Banner(r) : null;

    /// <summary>Text for the raw viewer: raw, or pretty-printed once <see cref="Pretty"/> is on (computed off the UI thread).</summary>
    public string RequestDisplay => _requestDisplay ?? RequestRaw;

    public string ResponseDisplay => _responseDisplay ?? ResponseRaw;

    partial void OnPrettyChanged(bool value) => _ = UpdateDisplayAsync(value);

    private async Task UpdateDisplayAsync(bool pretty)
    {
        if (pretty)
        {
            _requestDisplay = await GetRequestPrettyAsync();
            _responseDisplay = await GetResponsePrettyAsync();
        }
        else
        {
            _requestDisplay = _responseDisplay = null;
        }
        OnPropertyChanged(nameof(RequestDisplay));
        OnPropertyChanged(nameof(ResponseDisplay));
    }

    /// <summary>Pretty-printed request XML, formatted once on a worker thread and cached.</summary>
    public Task<string> GetRequestPrettyAsync() => _requestPretty ??= FormatAsync(RequestRaw);

    public Task<string> GetResponsePrettyAsync() => _responsePretty ??= FormatAsync(ResponseRaw);

    public void Update(ExchangeRecord record)
    {
        Record = record;
        _requestRaw = _responseRaw = null;
        _requestPretty = _responsePretty = null;
        _requestDisplay = _responseDisplay = null;
        OnPropertyChanged(string.Empty);
        if (Pretty)
        {
            _ = UpdateDisplayAsync(true);
        }
    }

    public void RefreshHeaders()
    {
        OnPropertyChanged(nameof(RequestHeaders));
        OnPropertyChanged(nameof(ResponseHeaders));
    }

    private Task<string> FormatAsync(string raw) => raw.Length == 0 ? Task.FromResult(raw) : Task.Run(() => _formatter.Format(raw));

    private List<HeaderRow> Headers(HttpMessageCapture c) =>
        c.Headers.Select(h => new HeaderRow(h.Key, AuthRedactor.IsSensitive(h.Key) && !_reveal() ? AuthRedactor.Redact(h.Value) : h.Value)).ToList();

    private static List<FieldRow> Fields(MessageSummary s) => s.Fields.Select(f => new FieldRow(f.Key, f.Value)).ToList();

    private static string Decode(HttpMessageCapture c)
    {
        if (c.Body.IsEmpty)
        {
            return "";
        }
        return CwmpParser.DecodeText(c.Body.Span);
    }

    private static string? Banner(HttpMessageCapture c) => c.Truncated
        ? $"Captured {Size(c.Body.Length)} of {Size(c.OriginalLength)}"
        : null;

    internal static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KiB" : bytes + " B";
}
