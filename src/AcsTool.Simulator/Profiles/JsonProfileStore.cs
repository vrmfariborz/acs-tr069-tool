using System.Text.Json;
using System.Text.Json.Serialization;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using Microsoft.Extensions.Logging;

namespace AcsTool.Simulator.Profiles;

public interface IProfileStore
{
    Task<IReadOnlyList<CpeProfile>> LoadAllAsync(CancellationToken ct);
    Task SaveAsync(CpeProfile profile, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CpeProfile))]
public sealed partial class SimulatorJsonContext : JsonSerializerContext;

/// <summary>One file per profile ({id}.json) in <see cref="IAppPaths.ProfilesDir"/>, written atomically.</summary>
public sealed class JsonProfileStore(IAppPaths paths, ILogger<JsonProfileStore> log) : IProfileStore
{
    public string PathFor(Guid id) => Path.Combine(paths.ProfilesDir, id.ToString("D") + ".json");

    public async Task<IReadOnlyList<CpeProfile>> LoadAllAsync(CancellationToken ct)
    {
        var result = new List<CpeProfile>();
        if (!Directory.Exists(paths.ProfilesDir))
        {
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(paths.ProfilesDir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var stream = File.OpenRead(file);
                await using (stream.ConfigureAwait(false))
                {
                    if (await JsonSerializer.DeserializeAsync(stream, SimulatorJsonContext.Default.CpeProfile, ct).ConfigureAwait(false) is { } p)
                    {
                        result.Add(p);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                log.LogWarning(3001, ex, "Skipping unreadable profile {File}", file);
            }
        }
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task SaveAsync(CpeProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Directory.CreateDirectory(paths.ProfilesDir);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, SimulatorJsonContext.Default.CpeProfile);
        return AtomicFile.WriteAllBytesAsync(PathFor(profile.Id), bytes, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}
