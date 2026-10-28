using System.Xml;
using System.Xml.Linq;

namespace AcsTool.Core.Util;

/// <summary>Randomness seam (NFR-09).</summary>
public interface IRandom
{
    double NextDouble();

    int Next(int minInclusive, int maxExclusive);
}

public sealed class SystemRandom : IRandom
{
#pragma warning disable CA5394 // not security relevant
    public double NextDouble() => Random.Shared.NextDouble();

    public int Next(int minInclusive, int maxExclusive) => Random.Shared.Next(minInclusive, maxExclusive);
#pragma warning restore CA5394
}

public sealed class SeededRandom(int seed) : IRandom
{
    private readonly Random _r = new(seed);
    private readonly object _lock = new();

#pragma warning disable CA5394
    public double NextDouble()
    {
        lock (_lock)
        {
            return _r.NextDouble();
        }
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        lock (_lock)
        {
            return _r.Next(minInclusive, maxExclusive);
        }
    }
#pragma warning restore CA5394
}

public interface IXmlFormatter
{
    /// <summary>Pretty-prints XML; returns the input unchanged if it is not well-formed or too large.</summary>
    string Format(string xml);
}

public sealed class XmlPretty : IXmlFormatter
{
    public const int MaxChars = 2 * 1024 * 1024;

    public static XmlPretty Instance { get; } = new();

    public string Format(string xml) => TryFormat(xml, out var pretty) ? pretty : xml;

    public static bool TryFormat(string xml, out string pretty)
    {
        pretty = xml;
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxChars)
        {
            return false;
        }
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxChars * 2L };
            using var sr = new StringReader(xml);
            using var reader = XmlReader.Create(sr, settings);
            var doc = XDocument.Load(reader);
            var decl = doc.Declaration is null ? string.Empty : doc.Declaration + "\n";
            pretty = decl + doc.ToString(SaveOptions.None);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}

public static class AtomicFile
{
    /// <summary>Writes to "*.tmp", flushes, then moves over the target (NFR-11).</summary>
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = path + ".tmp";
        var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            await fs.WriteAsync(data, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
