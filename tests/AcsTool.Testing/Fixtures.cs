using System.Text;

namespace AcsTool.Testing;

/// <summary>Loads SOAP sample files from the <c>fixtures</c> folder copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string PathOf(string name) => Path.Combine(Directory, name);

    public static byte[] Load(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture '{name}' not found in {Directory}", path);
        }
        return File.ReadAllBytes(path);
    }

    public static string LoadString(string name) => Encoding.UTF8.GetString(Load(name));

    public static IReadOnlyList<string> All() =>
        System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
