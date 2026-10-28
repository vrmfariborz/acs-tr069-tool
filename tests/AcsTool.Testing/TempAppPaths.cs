using AcsTool.Core.Settings;

namespace AcsTool.Testing;

/// <summary>IAppPaths over a unique temp directory, deleted on dispose.</summary>
public sealed class TempAppPaths : IAppPaths, IDisposable
{
    public TempAppPaths()
    {
        Root = Path.Combine(Path.GetTempPath(), "acstool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string ConfigDir => Path.Combine(Root, "config");
    public string ProfilesDir => Path.Combine(Root, "config", "profiles");
    public string LogsDir => Path.Combine(Root, "logs");
    public string DefaultExportDir => Path.Combine(Root, "exports");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
