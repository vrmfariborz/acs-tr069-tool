using System.Globalization;
using System.Threading.Channels;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using AcsTool.Simulator.Parameters;
using AcsTool.Simulator.Profiles;
using AcsTool.Simulator.Rpc;
using AcsTool.Simulator.Session;
using AcsTool.Simulator.Transport;
using Microsoft.Extensions.Logging;

namespace AcsTool.Simulator.Engine;

public enum CpeStateKind { Idle, InSession, WaitingRetry, Rebooting, Stopped, Error }

public sealed record CpeState(CpeStateKind Kind, uint RetryCount, DateTimeOffset? NextAttempt, string? LastError, Guid? CurrentSessionId)
{
    public static CpeState Initial { get; } = new(CpeStateKind.Stopped, 0, null, null, null);
}

public abstract record CpeTrigger
{
    public sealed record Manual(IReadOnlyList<EventStruct> Events) : CpeTrigger;

    /// <summary>UI parameter edits, applied in-loop (actor style).</summary>
    public sealed record Edit(IReadOnlyList<ParameterValue> Values) : CpeTrigger;

    public sealed record Stop : CpeTrigger;
}

/// <summary>Bundles the shared services a CPE needs (keeps the constructor readable).</summary>
public sealed record CpeServices(ICwmpTransportFactory Transports, RpcDispatcher Rpc, ICwmpParser Parser, IMessageSummarizer Summarizer,
    ICwmpMessageBuilder Builder, IInformBuilder Inform, ISessionSink Sink, ISecretProtector Secrets, TimeProvider Time, IRandom Random,
    ILoggerFactory LoggerFactory);

/// <summary>
/// One simulated CPE (plan §3.7.4): a single loop owns the ParameterStore and runs sessions for triggers posted to its channel.
/// MVP: Manual and Edit triggers only; no automatic retry/periodic scheduling (M8) and no follow-up execution (M7).
/// </summary>
public sealed class SimulatedCpe : IAsyncDisposable
{
    private readonly CpeServices _svc;
    private readonly SemaphoreSlim _globalGate;
    private readonly SimulatorSettings _settings;
    private readonly ILogger _log;
    private readonly Channel<CpeTrigger> _triggers = Channel.CreateUnbounded<CpeTrigger>(new UnboundedChannelOptions { SingleReader = true });
    private readonly PendingEventQueue _events = new();
    private readonly DateTimeOffset _bootTime;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ICwmpTransport? _transport;
    private CpeState _state = CpeState.Initial;
    private int _nextId;

    public SimulatedCpe(CpeProfile profile, DeviceIdStruct deviceId, CpeServices services, SemaphoreSlim globalGate, SimulatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(services);
        Profile = profile;
        DeviceId = deviceId;
        _svc = services;
        _globalGate = globalGate;
        _settings = settings;
        _log = services.LoggerFactory.CreateLogger($"AcsTool.Simulator.Cpe.{deviceId.SerialNumber}");
        Store = new ParameterStore(profile.Parameters);
        _bootTime = services.Time.GetUtcNow();
    }

    public CpeProfile Profile { get; }

    public DeviceIdStruct DeviceId { get; }

    public string DeviceKey => DeviceId.Key;

    public ParameterStore Store { get; }

    public CpeState State => Volatile.Read(ref _state);

    public IReadOnlyList<EventStruct> PendingEvents => _events.Snapshot();

    /// <summary>Raised on the CPE loop thread; view models marshal via IUiDispatcher.</summary>
    public event EventHandler<CpeState>? StateChanged;

    /// <summary>Result of the most recent session (for tests and the UI).</summary>
    public ClientSessionResult? LastResult { get; private set; }

    public int SessionsRun { get; private set; }

    public bool Post(CpeTrigger trigger) => _triggers.Writer.TryWrite(trigger);

    public Task StartAsync(CancellationToken ct)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }
        _transport = _svc.Transports.Create(Profile, _svc.Secrets);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SetState(new CpeState(CpeStateKind.Idle, 0, null, null, null));
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the loop; an in-flight session is canceled (recorded as Canceled).</summary>
    public async Task StopAsync()
    {
        if (_loop is null)
        {
            return;
        }
        _triggers.Writer.TryComplete();
        await (_cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        _loop = null;
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }
        _cts?.Dispose();
        _cts = null;
        SetState(State with { Kind = CpeStateKind.Stopped, CurrentSessionId = null });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _triggers.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var runSession = false;
                while (_triggers.Reader.TryRead(out var trigger))
                {
                    switch (trigger)
                    {
                        case CpeTrigger.Stop:
                            return;
                        case CpeTrigger.Manual m:
                            _events.Add(m.Events.Count == 0 ? [new EventStruct(EventCodes.ConnectionRequest)] : m.Events);
                            runSession = true;
                            break;
                        case CpeTrigger.Edit e:
                            foreach (var v in e.Values)
                            {
                                if (Store.Get(v.Name) is not null && Store.ApplyLocalEdit(v.Name, v.Value))
                                {
                                    _events.Add([new EventStruct(EventCodes.ValueChange)]);
                                    runSession = true;
                                }
                            }
                            break;
                    }
                }
                if (runSession && _events.Count > 0)
                {
                    try
                    {
                        await RunSessionAsync(ct).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // one failed session must not kill the CPE loop (review m5)
                    catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        _log.LogError(3302, ex, "Session for {Device} failed", DeviceKey);
                        SetState(new CpeState(CpeStateKind.Error, State.RetryCount + 1, null, ex.Message, null));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
#pragma warning disable CA1031 // the loop must never die silently (NFR-07)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError(3301, ex, "CPE loop for {Device} crashed", DeviceKey);
            SetState(State with { Kind = CpeStateKind.Error, LastError = ex.Message, CurrentSessionId = null });
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        await _globalGate.WaitAsync(ct).ConfigureAwait(false);
        ClientSessionResult result;
        var events = _events.Snapshot();
        try
        {
            Store.SetInternal(Profile.RootPrefix + "DeviceInfo.UpTime",
                ((uint)Math.Max(0, (_svc.Time.GetUtcNow() - _bootTime).TotalSeconds)).ToString(CultureInfo.InvariantCulture));
            var ctx = new ClientSessionContext(Profile, DeviceId, _transport!, Store, events, BuildInformParameters(events), State.RetryCount,
                () => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture), [], _svc.Sink, _svc.Rpc, _svc.Parser,
                _svc.Summarizer, _svc.Builder, _svc.Inform, _settings, _svc.Time, _log)
            {
                ClientEndpoint = $"sim:{DeviceKey}",
            };
            var session = new CwmpClientSession();
            SetState(State with { Kind = CpeStateKind.InSession });
            result = await session.RunAsync(ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            _globalGate.Release();
        }
        LastResult = result;
        SessionsRun++;
        var ok = result.Outcome == SessionOutcome.Completed;
        if (result.InformAccepted || ok)
        {
            _events.Remove(events, ok);
        }
        if (ct.IsCancellationRequested)
        {
            return;
        }
        if (ok)
        {
            SetState(new CpeState(CpeStateKind.Idle, 0, null, null, null));
        }
        else
        {
            // MVP: no automatic retry (M8). Keep undelivered events; the next manual trigger retries with RetryCount+1.
            var err = result.Error?.ShortText ?? result.Note ?? result.Outcome.ToString();
            SetState(new CpeState(CpeStateKind.Error, State.RetryCount + 1, null, $"{result.Outcome}: {err}", null));
        }
    }

    private List<ParameterValue> BuildInformParameters(IReadOnlyList<EventStruct> events)
    {
        var forced = new HashSet<string>(DefaultProfiles.Forced(Profile.Root), StringComparer.Ordinal);
        var list = new List<ParameterValue>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Store.Snapshot())
        {
            if ((forced.Contains(p.Name) || p.IncludeInInform) && names.Add(p.Name))
            {
                list.Add(new ParameterValue(p.Name, p.Value, p.XsdType));
            }
        }
        if (events.Any(e => e.EventCode == EventCodes.ValueChange))
        {
            foreach (var v in Store.TakeChangedForValueChange())
            {
                if (names.Add(v.Name))
                {
                    list.Add(v);
                }
            }
        }
        return list;
    }

    private void SetState(CpeState s)
    {
        Volatile.Write(ref _state, s);
        StateChanged?.Invoke(this, s);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
