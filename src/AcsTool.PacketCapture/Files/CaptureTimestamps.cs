namespace AcsTool.PacketCapture.Files;

/// <summary>Conversions between file timestamp units (<c>if_tsresol</c>) and UTC <see cref="DateTime.Ticks"/> (100 ns).</summary>
public static class CaptureTimestamps
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>The PCAPNG resolution AcsTool writes: 10^-7 s, i.e. exactly one tick (design §6.1).</summary>
    public const byte TicksResolution = 7;

    /// <summary>Default PCAPNG resolution when an IDB has no <c>if_tsresol</c>: µs.</summary>
    public const byte DefaultResolution = 6;

    /// <summary>True when <paramref name="resolution"/> can be converted without overflow (10^-0..10^-38, 2^-0..2^-126).</summary>
    public static bool IsSupported(byte resolution) =>
        (resolution & 0x80) == 0 ? resolution <= 38 : (resolution & 0x7F) <= 126;

    /// <summary>
    /// Converts <paramref name="units"/> since the Unix epoch at <paramref name="resolution"/>, plus <paramref name="offsetSeconds"/>
    /// (<c>if_tsoffset</c>), to UTC ticks. Finer-than-tick values round half away from zero (as the live path does);
    /// results outside the <see cref="DateTime"/> range are clamped.
    /// </summary>
    public static long ToTicksUtc(ulong units, byte resolution, long offsetSeconds)
    {
        if (!IsSupported(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unsupported timestamp resolution.");
        }

        Int128 relative;
        if ((resolution & 0x80) == 0)
        {
            relative = resolution <= 7
                ? (Int128)units * Pow10(7 - resolution)
                : RoundDiv(units, Pow10(resolution - 7));
        }
        else
        {
            relative = RoundDiv((Int128)units * TicksPerSecond, Int128.One << (resolution & 0x7F));
        }

        var absolute = DateTime.UnixEpoch.Ticks + relative + (Int128)offsetSeconds * TicksPerSecond;
        if (absolute < DateTime.MinValue.Ticks)
        {
            return DateTime.MinValue.Ticks;
        }
        if (absolute > DateTime.MaxValue.Ticks)
        {
            return DateTime.MaxValue.Ticks;
        }
        return (long)absolute;
    }

    /// <summary>UTC ticks to 100 ns units since the Unix epoch. Throws for times before 1970.</summary>
    internal static ulong ToUnitsSinceEpoch(long timestampTicksUtc, string paramName)
    {
        var rel = timestampTicksUtc - DateTime.UnixEpoch.Ticks;
        if (rel < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, timestampTicksUtc, "Timestamps before 1970-01-01 cannot be stored in a capture file.");
        }
        return (ulong)rel;
    }

    private static Int128 Pow10(int n)
    {
        Int128 r = 1;
        for (var i = 0; i < n; i++)
        {
            r *= 10;
        }
        return r;
    }

    private static Int128 RoundDiv(Int128 value, Int128 divisor) => (value + (divisor / 2)) / divisor;
}
