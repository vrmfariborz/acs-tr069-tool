using System.Globalization;
using System.Text.RegularExpressions;
using AcsTool.Core.Model;
using AcsTool.Core.Settings;
using AcsTool.Simulator.Profiles;
using Microsoft.Extensions.Logging;

namespace AcsTool.Simulator.Engine;

public sealed class ProfileValidationException(IReadOnlyList<ValidationError> errors)
    : Exception("Profile is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;
}

/// <summary>Owns the running simulated CPEs and the global session gate (plan §3.7.1).</summary>
public sealed partial class SimulationEngine : IAsyncDisposable
{
    private readonly CpeServices _services;
    private readonly SettingsHolder _settings;
    private readonly ILogger<SimulationEngine> _log;
    private readonly object _lock = new();
    private readonly List<SimulatedCpe> _cpes = [];
    private SemaphoreSlim _gate;

    public SimulationEngine(CpeServices services, SettingsHolder settings, ILogger<SimulationEngine> log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        _services = services;
        _settings = settings;
        _log = log;
        MaxConcurrentSessions = Math.Max(1, settings.Current.Simulator.MaxConcurrentSessions);
        _gate = new SemaphoreSlim(MaxConcurrentSessions, MaxConcurrentSessions);
    }

    /// <summary>
    /// Size of the global session gate. Follows <c>SimulatorSettings.MaxConcurrentSessions</c>; a changed value takes effect
    /// the next time a profile is started while no simulated CPE is running (running CPEs keep the gate they were given).
    /// </summary>
    public int MaxConcurrentSessions { get; private set; }

    public IReadOnlyList<SimulatedCpe> Cpes
    {
        get
        {
            lock (_lock)
            {
                return [.. _cpes];
            }
        }
    }

    public event EventHandler? CpesChanged;

    public async Task<IReadOnlyList<SimulatedCpe>> StartProfileAsync(CpeProfile profile, bool sendBootNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }
        var ids = Expand(profile);
        var started = new List<SimulatedCpe>();
        lock (_lock)
        {
            var wanted = Math.Max(1, _settings.Current.Simulator.MaxConcurrentSessions);
            if (_cpes.Count == 0 && wanted != MaxConcurrentSessions)
            {
                // The old gate is not disposed: a stopping CPE may still Release it (no wait handle was allocated).
                MaxConcurrentSessions = wanted;
                _gate = new SemaphoreSlim(wanted, wanted);
            }
            foreach (var id in ids)
            {
                if (_cpes.Any(c => c.DeviceKey == id.Key))
                {
                    throw new ProfileValidationException([new ValidationError("DeviceId.SerialNumber", $"A CPE with serial {id.SerialNumber} is already running")]);
                }
            }
            foreach (var id in ids)
            {
                var cpe = new SimulatedCpe(profile, id, _services, _gate, _settings.Current.Simulator);
                _cpes.Add(cpe);
                started.Add(cpe);
            }
        }
        foreach (var cpe in started)
        {
            await cpe.StartAsync(CancellationToken.None).ConfigureAwait(false);
            if (sendBootNow)
            {
                cpe.Post(new CpeTrigger.Manual(profile.DefaultEvents));
            }
        }
        _log.LogInformation(3401, "Started {Count} CPE(s) for profile {Profile}", started.Count, profile.Name);
        CpesChanged?.Invoke(this, EventArgs.Empty);
        return started;
    }

    public async Task StopProfileAsync(Guid profileId)
    {
        List<SimulatedCpe> stop;
        lock (_lock)
        {
            stop = _cpes.Where(c => c.Profile.Id == profileId).ToList();
            _cpes.RemoveAll(stop.Contains);
        }
        await Task.WhenAll(stop.Select(c => c.StopAsync())).ConfigureAwait(false);
        CpesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAllAsync()
    {
        List<SimulatedCpe> stop;
        lock (_lock)
        {
            stop = [.. _cpes];
            _cpes.Clear();
        }
        await Task.WhenAll(stop.Select(c => c.StopAsync())).ConfigureAwait(false);
        if (stop.Count > 0)
        {
            CpesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Expands MultiInstanceSettings into device ids: {serial}, {i}, {i:D6}.</summary>
    public static IReadOnlyList<DeviceIdStruct> Expand(CpeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var n = Math.Max(1, profile.Instances.Count);
        var pattern = string.IsNullOrEmpty(profile.Instances.SerialPattern) ? "{serial}" : profile.Instances.SerialPattern;
        var list = new List<DeviceIdStruct>();
        for (var k = 0; k < n; k++)
        {
            var i = profile.Instances.StartIndex + k;
            var serial = IndexToken().Replace(pattern.Replace("{serial}", profile.DeviceId.SerialNumber, StringComparison.Ordinal),
                m => m.Groups[1].Success ? i.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture) : i.ToString(CultureInfo.InvariantCulture));
            list.Add(profile.DeviceId with { SerialNumber = serial });
        }
        if (list.Select(d => d.SerialNumber).Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            throw new ProfileValidationException([new ValidationError("Instances.SerialPattern", "The serial pattern produces duplicate serials")]);
        }
        return list;
    }

    [GeneratedRegex(@"\{i(?::([A-Za-z]\d*))?\}")]
    private static partial Regex IndexToken();

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
