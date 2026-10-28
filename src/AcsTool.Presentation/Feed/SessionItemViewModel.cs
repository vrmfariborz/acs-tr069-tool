using System.Globalization;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcsTool.Presentation.Feed;

/// <summary>One feed row; updated in place so WPF keeps selection and scroll position.</summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _device = "?";

    [ObservableProperty]
    private string _client = "";

    [ObservableProperty]
    private string _events = "";

    [ObservableProperty]
    private int _exchangeCount;

    [ObservableProperty]
    private SessionOutcome _outcome;

    [ObservableProperty]
    private string _outcomeBadge = "";

    [ObservableProperty]
    private string _duration = "";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _methods = "";

    public SessionItemViewModel(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Id = snapshot.Id;
        Source = snapshot.Source;
        Snapshot = snapshot;
        Time = snapshot.Started.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Update(snapshot);
    }

    public Guid Id { get; }

    public SessionSource Source { get; }

    public string Time { get; }

    public SessionSnapshot Snapshot { get; private set; }

    public bool HasError => ErrorText is not null;

    public bool IsError => Outcome is not (SessionOutcome.Completed or SessionOutcome.InProgress);

    public void Update(SessionSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Snapshot = s;
        Device = s.DeviceId?.Key ?? s.Label ?? "?";
        Client = s.ClientEndpoint;
        Events = string.Join(",", s.Events.Select(e => EventCodes.Short(e.EventCode)));
        ExchangeCount = s.Exchanges.Count;
        Outcome = s.Outcome;
        OutcomeBadge = Badge(s.Outcome);
        Duration = s.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "";
        var newError = s.LastError?.ToString() ?? (IsError && s.Notes.Count > 0 ? s.Notes[^1] : null);
        if (newError != ErrorText)
        {
            ErrorText = newError;
            OnPropertyChanged(nameof(HasError));
        }
        Methods = string.Join(" ", s.Exchanges.Select(e => e.RequestSummary.Kind.ToString()).Distinct());
        OnPropertyChanged(nameof(IsError));
    }

    public static string Badge(SessionOutcome o) => o switch
    {
        SessionOutcome.Completed => "✔ Completed",
        SessionOutcome.InProgress => "⧗ In progress",
        SessionOutcome.UpstreamError => "✖ Upstream error",
        SessionOutcome.AuthFailed => "🔒 Auth failed",
        SessionOutcome.AcsFault => "⚠ ACS fault",
        SessionOutcome.CpeFault => "⚠ CPE fault",
        SessionOutcome.HttpError => "⚠ HTTP error",
        SessionOutcome.ProtocolError => "⚠ Protocol error",
        SessionOutcome.Abandoned => "⌛ Abandoned",
        SessionOutcome.Superseded => "↷ Superseded",
        SessionOutcome.Canceled => "⊘ Canceled",
        _ => o.ToString(),
    };

    /// <summary>Case-insensitive match on device key, client, methods and error text.</summary>
    public bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        var t = text.Trim();
        return Device.Contains(t, StringComparison.OrdinalIgnoreCase)
            || Client.Contains(t, StringComparison.OrdinalIgnoreCase)
            || Methods.Contains(t, StringComparison.OrdinalIgnoreCase)
            || (ErrorText?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
