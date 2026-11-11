namespace AcsTool.PacketCapture.Files;

/// <summary>
/// Sequential reader over a (possibly non-seekable) stream that tracks the absolute byte offset for error reports and can
/// replay a prefix already consumed by format detection.
/// </summary>
internal sealed class ByteSource : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _prefix;
    private int _prefixPos;
    private bool _disposed;

    public ByteSource(Stream stream, bool leaveOpen, byte[]? prefix = null, long startOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        _prefix = prefix ?? [];
        Position = startOffset;
    }

    /// <summary>Absolute offset of the next byte (counted from the start of the file).</summary>
    public long Position { get; private set; }

    /// <summary>Fills as much of <paramref name="buffer"/> as the stream has; returns the count (less than requested only at end of stream).</summary>
    public int ReadFully(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        if (_prefixPos < _prefix.Length)
        {
            var n = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
            _prefix.AsSpan(_prefixPos, n).CopyTo(buffer);
            _prefixPos += n;
            total = n;
        }
        while (total < buffer.Length)
        {
            var n = _stream.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        Position += total;
        return total;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
