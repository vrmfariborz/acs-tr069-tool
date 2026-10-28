using System.Collections.ObjectModel;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Feed;
using AcsTool.Simulator.Engine;
using AcsTool.Simulator.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AcsTool.Presentation.Simulator;

/// <summary>Running CPE row; state arrives on the CPE loop thread and is marshalled via IUiDispatcher.Post.</summary>
public sealed partial class CpeInstanceViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;

    [ObservableProperty] private CpeStateKind _state;
    [ObservableProperty] private uint _retryCount;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string _lastOutcome = "";
    [ObservableProperty] private int _sessions;

    public CpeInstanceViewModel(SimulatedCpe cpe, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(cpe);
        Cpe = cpe;
        _dispatcher = dispatcher;
        Apply(cpe.State);
        cpe.StateChanged += OnStateChanged;
    }

    public SimulatedCpe Cpe { get; }

    public string DeviceKey => Cpe.DeviceKey;

    public Guid ProfileId => Cpe.Profile.Id;

    private void OnStateChanged(object? sender, CpeState s) => _dispatcher.Post(() => Apply(s));

    private void Apply(CpeState s)
    {
        State = s.Kind;
        RetryCount = s.RetryCount;
        LastError = s.LastError;
        Sessions = Cpe.SessionsRun;
        LastOutcome = Cpe.LastResult is { } r ? SessionItemViewModel.Badge(r.Outcome) : "";
    }

    public void Detach() => Cpe.StateChanged -= OnStateChanged;

    [RelayCommand]
    private void SendInform() => Cpe.Post(new CpeTrigger.Manual(Cpe.Profile.DefaultEvents));
}

/// <summary>Simulator tab (plan §4.4, MVP subset).</summary>
public sealed partial class SimulatorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IProfileStore _profiles;
    private readonly SimulationEngine _engine;
    private readonly ISecretProtector _secrets;
    private readonly IBannerService _banners;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SimulatorViewModel> _log;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(StartCommand), nameof(StopCommand), nameof(SendInformNowCommand))]
    private CpeProfileEditorViewModel? _selectedProfile;

    [ObservableProperty]
    private CpeInstanceViewModel? _selectedCpe;

    public SimulatorViewModel(IProfileStore profiles, SimulationEngine engine, ISecretProtector secrets, IBannerService banners,
        IUiDispatcher dispatcher, SessionFeedViewModel feed, ILogger<SimulatorViewModel> log)
    {
        _profiles = profiles;
        _engine = engine;
        _secrets = secrets;
        _banners = banners;
        _dispatcher = dispatcher;
        _log = log;
        Feed = feed;
    }

    public SessionFeedViewModel Feed { get; }

    public ObservableCollection<CpeProfileEditorViewModel> Profiles { get; } = [];

    public ObservableCollection<CpeInstanceViewModel> RunningCpes { get; } = [];

    public string Summary => $"{RunningCpes.Count} running · {RunningCpes.Count(c => c.State == CpeStateKind.InSession)} in session · " +
        $"{RunningCpes.Count(c => c.State == CpeStateKind.Error)} error";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await _profiles.LoadAllAsync(ct);
        Profiles.Clear();
        foreach (var p in all)
        {
            Profiles.Add(new CpeProfileEditorViewModel(p, _secrets));
        }
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void NewFromTr098() => AddNew(DefaultProfiles.Tr098("New TR-098 profile", UniqueSerial("SIM098")));

    [RelayCommand]
    private void NewFromTr181() => AddNew(DefaultProfiles.Tr181("New TR-181 profile", UniqueSerial("SIM181")));

    private string UniqueSerial(string prefix)
    {
        for (var i = 1; ; i++)
        {
            var s = prefix + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            if (!Profiles.Any(p => p.SerialNumber == s))
            {
                return s;
            }
        }
    }

    private void AddNew(CpeProfile p)
    {
        var vm = new CpeProfileEditorViewModel(p, _secrets) { IsDirty = true };
        Profiles.Add(vm);
        SelectedProfile = vm;
    }

    private bool HasSelection() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveAsync()
    {
        if (SelectedProfile is not { } vm)
        {
            return;
        }
        if (vm.Validate() is not { } profile)
        {
            _banners.Show(BannerKind.Error, $"Profile '{vm.Name}' has {vm.Errors.Count} validation error(s)", "sim-validation");
            return;
        }
        _banners.Dismiss("sim-validation");
        await _profiles.SaveAsync(profile, CancellationToken.None);
        vm.AcceptSaved(profile);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedProfile is not { } vm)
        {
            return;
        }
        await StopProfileAsync(vm.Id);
        await _profiles.DeleteAsync(vm.Id, CancellationToken.None);
        Profiles.Remove(vm);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    /// <summary>Starts the selected profile's CPE(s) without sending (use Send Inform now), or with the boot events when <paramref name="sendBootNow"/>.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task StartAsync() => await StartSelectedAsync(sendBootNow: false);

    public async Task<bool> StartSelectedAsync(bool sendBootNow)
    {
        if (SelectedProfile is not { } vm || vm.Validate() is not { } profile)
        {
            if (SelectedProfile is { } bad)
            {
                _banners.Show(BannerKind.Error, $"Cannot start '{bad.Name}': {string.Join("; ", bad.Errors)}", "sim-start");
            }
            return false;
        }
        if (RunningCpes.Any(c => c.ProfileId == profile.Id))
        {
            return true;
        }
        try
        {
            var cpes = await _engine.StartProfileAsync(profile, sendBootNow, CancellationToken.None);
            foreach (var cpe in cpes)
            {
                RunningCpes.Add(new CpeInstanceViewModel(cpe, _dispatcher));
            }
            _banners.Dismiss("sim-start");
            OnPropertyChanged(nameof(Summary));
            return true;
        }
        catch (ProfileValidationException ex)
        {
            _banners.Show(BannerKind.Error, ex.Message, "sim-start");
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task StopAsync() => SelectedProfile is { } vm ? StopProfileAsync(vm.Id) : Task.CompletedTask;

    private async Task StopProfileAsync(Guid id)
    {
        await _engine.StopProfileAsync(id);
        foreach (var row in RunningCpes.Where(c => c.ProfileId == id).ToList())
        {
            row.Detach();
            RunningCpes.Remove(row);
        }
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>"Send Inform now": starts the profile if needed, then posts a Manual trigger with the editor's selected events.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SendInformNowAsync()
    {
        if (SelectedProfile is not { } vm)
        {
            return;
        }
        if (!await StartSelectedAsync(sendBootNow: false))
        {
            return;
        }
        var events = vm.SelectedEvents();
        foreach (var row in RunningCpes.Where(c => c.ProfileId == vm.Id))
        {
            row.Cpe.Post(new CpeTrigger.Manual(events));
        }
    }

    [RelayCommand]
    public async Task StopAllAsync()
    {
        await _engine.StopAllAsync();
        foreach (var row in RunningCpes)
        {
            row.Detach();
        }
        RunningCpes.Clear();
        OnPropertyChanged(nameof(Summary));
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
