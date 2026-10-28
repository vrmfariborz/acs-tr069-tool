using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AcsTool.Core.Settings;

public interface IAppPaths
{
    string ConfigDir { get; }
    string ProfilesDir { get; }
    string LogsDir { get; }
    string DefaultExportDir { get; }
}

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string stored);
}

/// <summary>Linux/tests protector: "plain:&lt;text&gt;". Windows uses DPAPI in the App.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    private const string Prefix = "plain:";

    public string Protect(string plain) => Prefix + (plain ?? string.Empty);

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }
        return stored.StartsWith(Prefix, StringComparison.Ordinal) ? stored[Prefix.Length..] : stored;
    }
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings s, CancellationToken ct);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AcsToolJsonContext : JsonSerializerContext;

/// <summary>settings.json in <see cref="IAppPaths.ConfigDir"/>; atomic writes; corrupt files are backed up (plan §3.5.6).</summary>
public sealed class JsonSettingsStore(IAppPaths paths, TimeProvider time, ILogger<JsonSettingsStore> log) : ISettingsStore
{
    public string FilePath => Path.Combine(paths.ConfigDir, "settings.json");

    /// <summary>Set when the last load found a corrupt file (UI shows a banner).</summary>
    public string? LastLoadProblem { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        LastLoadProblem = null;
        if (!File.Exists(FilePath))
        {
            return AppSettings.Default;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(FilePath, ct).ConfigureAwait(false);
            var s = JsonSerializer.Deserialize(bytes, AcsToolJsonContext.Default.AppSettings);
            return (s ?? AppSettings.Default).Normalize();
        }
        catch (JsonException ex)
        {
            var backup = $"{FilePath}.bad-{time.GetUtcNow():yyyyMMddHHmmss}";
            File.Copy(FilePath, backup, overwrite: true);
            LastLoadProblem = $"settings.json was corrupt and was backed up to {Path.GetFileName(backup)}; defaults loaded.";
            log.LogWarning(3001, ex, "Corrupt settings file backed up to {Backup}", backup);
            return AppSettings.Default;
        }
    }

    public Task SaveAsync(AppSettings s, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(s.Normalize(), AcsToolJsonContext.Default.AppSettings);
        return Util.AtomicFile.WriteAllBytesAsync(FilePath, bytes, ct);
    }
}
