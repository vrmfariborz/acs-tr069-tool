using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AcsTool.PacketCapture.Tests;

/// <summary>
/// Native-free <see cref="LibPcapLiveDevice"/> for driving <c>SharpPcapCaptureSession</c> and <c>SharpPcapBackend.Open</c>.
/// Its capture thread mirrors SharpPcap 6.3.1: <see cref="StopCapture"/> cancels, waits up to <see cref="PcapDevice.StopCaptureTimeout"/>
/// and then forgets the thread whether or not it exited. With <see cref="AutoTraffic"/> the thread raises packets back to back
/// (a busy link); otherwise tests raise packets with <see cref="Emit"/>.
/// </summary>
internal sealed class TestLiveDevice : LibPcapLiveDevice
{
    private readonly string _name;
    private Thread? _thread;
    private CancellationTokenSource _cts = new();
    private bool _opened;
    private bool _started;
    private long _sent;

    public TestLiveDevice(string name = "test0", bool opened = true)
        : base(null!)
    {
        _name = name;
        _opened = opened;
    }

    public bool AutoTraffic { get; set; }
    public Exception? StartFailure { get; set; }
    public Exception? OpenFailure { get; set; }
    public Exception? FilterFailure { get; set; }
    public ICaptureStatistics? Stats { get; set; }
    public Exception? StatsFailure { get; set; }
    public uint FlagsValue { get; set; } = 0x2; // PCAP_IF_UP
    public string DescriptionValue { get; set; } = "Test device";
    public PhysicalAddress? Mac { get; set; }

    public DeviceConfiguration? LastConfiguration { get; private set; }
    public int StartCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public bool? LastStopJoined { get; private set; }
    public Thread? LastCaptureThread { get; private set; }
    public long Sent => Interlocked.Read(ref _sent);

    public override string Name => _name;
    public override string Description => DescriptionValue;
    public override uint Flags => FlagsValue;
    public override ReadOnlyCollection<PcapAddress> Addresses => new([]);
    public override PhysicalAddress? MacAddress => Mac;
    public override bool Opened => Volatile.Read(ref _opened);
    public override bool Started => Volatile.Read(ref _started);
    public override LinkLayers LinkType => LinkLayers.Ethernet;

    public override string? Filter
    {
        get => null;
        set
        {
            if (FilterFailure is not null)
                throw FilterFailure;
        }
    }

    public override ICaptureStatistics Statistics =>
        StatsFailure is not null ? throw StatsFailure : Stats!;

    public override void Open(DeviceConfiguration configuration)
    {
        if (OpenFailure is not null)
            throw OpenFailure;
        LastConfiguration = configuration;
        StopCaptureTimeout = TimeSpan.FromMilliseconds(configuration.ReadTimeout * 2); // as LibPcapLiveDevice.Open does
        Volatile.Write(ref _opened, true);
    }

    public override void Close()
    {
        CloseCalls++;
        if (Started)
            StopCapture();
        Volatile.Write(ref _opened, false);
    }

    public override void StartCapture()
    {
        StartCalls++;
        if (StartFailure is not null)
            throw StartFailure;
        if (Started)
            return;
        Volatile.Write(ref _started, true);
        if (!AutoTraffic)
            return;
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token)) { IsBackground = true, Name = "TestLiveDevice capture" };
        LastCaptureThread = _thread;
        _thread.Start();
    }

    public override void StopCapture()
    {
        if (!Started)
            return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        LastStopJoined = _thread?.Join(StopCaptureTimeout) ?? true;
        _thread = null;
        Volatile.Write(ref _started, false);
    }

    /// <summary>Raise one packet on the calling thread, as SharpPcap's capture loop would.</summary>
    public void Emit(PcapHeader header, byte[] data) => SendPacketArrivalEvent(header, data);

    private void Loop(CancellationToken token)
    {
        var data = new byte[60];
        while (!token.IsCancellationRequested)
        {
            SendPacketArrivalEvent(new PcapHeader(1_700_000_000, 1, 60, 60), data);
            Interlocked.Increment(ref _sent);
        }
    }
}

internal sealed class StubStats : ICaptureStatistics
{
    public uint ReceivedPackets { get; set; }
    public uint DroppedPackets { get; set; }
    public uint InterfaceDroppedPackets { get; set; }
}
