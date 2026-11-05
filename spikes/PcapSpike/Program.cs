// PT0.1 throw-away Npcap / libpcap spike. Findings only — never merge into main.
// CLI: PcapSpike [--probe-only] [--seconds 30] [--device name]… [--out report.md]
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

return await SpikeApp.RunAsync(args);

internal static class SpikeApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var report = new ReportBuilder();
        var exitCode = 0;
        try
        {
            var opts = Options.Parse(args);
            report.AddHeading("PcapSpike PT0.1 report");
            report.AddParagraph($"Generated (UTC): {DateTime.UtcNow:O}");
            report.AddParagraph($"Command line: {Environment.CommandLine}");

            var probeOk = RunProbe(report);
            if (opts.ProbeOnly)
            {
                Console.WriteLine(probeOk
                    ? "probe-only: native pcap library loaded OK (exit 0)"
                    : "probe-only: native pcap library NOT available (exit 2)");
                WriteReport(opts, report);
                return probeOk ? 0 : 2;
            }

            if (!probeOk)
            {
                report.AddParagraph("Aborting full run: probe failed (native library missing).");
                WriteReport(opts, report);
                Console.WriteLine("Full run aborted: probe failed. See report. Exit 2.");
                return 2;
            }

            var devices = EnumerateDevices(report, opts);
            PreviewD10(report, devices);

            var selected = SelectDevices(devices, opts);
            if (selected.Count == 0)
            {
                report.AddParagraph("No devices selected for capture (pass --device or rely on D10 auto).");
                WriteReport(opts, report);
                Console.WriteLine("No devices to capture. Exit 3.");
                return 3;
            }

            await RunCaptureAsync(report, selected, opts);
            WriteAssumptionsTable(report);
            WriteReport(opts, report);
            Console.WriteLine($"Done. Report: {Path.GetFullPath(opts.OutPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            try
            {
                report.AddHeading("Fatal (caught)");
                report.AddCode(ex.ToString());
                Console.Error.WriteLine("Fatal (caught, not rethrown): " + ex.GetType().FullName + ": " + ex.Message);
            }
            catch
            {
                // ignore secondary failures
            }

            try { WriteReport(new Options(), report); } catch { /* ignore */ }
            return exitCode;
        }
    }

    static bool RunProbe(ReportBuilder report)
    {
        report.AddHeading("1. Probe");
        report.AddBullet($"OS: {RuntimeInformation.OSDescription}");
        report.AddBullet($"OS architecture: {RuntimeInformation.OSArchitecture}");
        report.AddBullet($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        report.AddBullet($"Framework: {RuntimeInformation.FrameworkDescription}");
        report.AddBullet($"Elevated: {IsElevated()}");
        report.AddBullet($"64-bit process: {Environment.Is64BitProcess}");

        string? sharpVer = null, libVer = null, combined = null;
        Exception? versionEx = null;
        try
        {
            sharpVer = Pcap.SharpPcapVersion.ToString();
            libVer = Pcap.LibpcapVersion.ToString();
            combined = Pcap.Version;
        }
        catch (Exception ex)
        {
            versionEx = ex;
        }

        report.AddBullet($"Pcap.SharpPcapVersion: {sharpVer ?? "(threw)"}");
        report.AddBullet($"Pcap.LibpcapVersion: {libVer ?? "(threw)"}");
        report.AddBullet($"Pcap.Version: {combined ?? "(threw)"}");
        if (versionEx is not null)
            report.AddCode($"Version properties threw:\n{Describe(versionEx)}");

        Exception? listEx = null;
        int count = -1;
        try
        {
            // First native touch: device enumeration loads wpcap/libpcap.
            var list = CaptureDeviceList.Instance;
            count = list.Count;
            report.AddBullet($"CaptureDeviceList.Instance.Count: {count}");
            report.Assumption("missing-npcap-dll", "CaptureDeviceList without Npcap/libpcap → DllNotFoundException",
                listEx is null ? "unknown (library present)" : null);
        }
        catch (Exception ex)
        {
            listEx = ex;
            report.AddBullet($"CaptureDeviceList.Instance threw: {ex.GetType().FullName}: {ex.Message}");
            report.AddCode(Describe(ex));
            var isDll = ex is DllNotFoundException || ex.GetType().Name.Contains("DllNotFound", StringComparison.Ordinal)
                        || (ex.InnerException is DllNotFoundException)
                        || ex.Message.Contains("libpcap", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("wpcap", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("pcap", StringComparison.OrdinalIgnoreCase);
            report.Assumption("missing-npcap-dll",
                "CaptureDeviceList without Npcap/libpcap → DllNotFoundException (or equivalent native load failure)",
                isDll
                    ? (ex is DllNotFoundException ? "confirmed (DllNotFoundException)" : $"confirmed-ish ({ex.GetType().Name})")
                    : $"refuted / unexpected type: {ex.GetType().FullName}");
        }

        if (listEx is null)
        {
            report.Assumption("missing-npcap-dll",
                "CaptureDeviceList without Npcap/libpcap → DllNotFoundException",
                "unknown on this host (native library loaded successfully)");
        }

        // Also note Windows Npcap path expectation (cannot verify from Linux).
        if (OperatingSystem.IsWindows())
        {
            var npcapDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
            var wpcap = Path.Combine(npcapDir, "wpcap.dll");
            report.AddBullet($"Expected Npcap dir: {npcapDir} (exists={Directory.Exists(npcapDir)})");
            report.AddBullet($"Expected wpcap.dll: {wpcap} (exists={File.Exists(wpcap)})");
            report.Assumption("wpcap-system32-npcap",
                "wpcap.dll found under System32\\Npcap with no extra setup",
                File.Exists(wpcap) ? "confirmed" : "refuted or not installed");
        }
        else
        {
            report.Assumption("wpcap-system32-npcap",
                "wpcap.dll found under System32\\Npcap with no extra setup",
                "unknown (not Windows)");
        }

        return listEx is null;
    }

    static List<DeviceInfo> EnumerateDevices(ReportBuilder report, Options opts)
    {
        report.AddHeading("2. Devices");
        var result = new List<DeviceInfo>();
        try
        {
            foreach (var raw in CaptureDeviceList.Instance)
            {
                if (raw is not LibPcapLiveDevice live)
                {
                    report.AddParagraph($"Skipping non-LibPcapLiveDevice: {raw.GetType().FullName} name={raw.Name}");
                    continue;
                }

                var info = new DeviceInfo(live);
                result.Add(info);
                report.AddSubheading(info.Name);
                report.AddBullet($"FriendlyName: {info.FriendlyName}");
                report.AddBullet($"Description: {info.Description}");
                report.AddBullet($"Addresses: {string.Join("; ", info.Addresses)}");
                report.AddBullet($"Mac: {info.Mac}");
                report.AddBullet($"Loopback: {info.Loopback}");
                report.AddBullet($"Flags: 0x{info.Flags:X8}");
                report.AddBullet($"TimestampsSupported: {string.Join(", ", info.TimestampsSupported)}");

                // Open briefly for LinkType (closed again after).
                try
                {
                    var cfg = NewConfig(report, info.Name);
                    live.Open(cfg);
                    info.LinkType = live.LinkType.ToString();
                    info.OpenedTimestampResolution = live.TimestampResolution.ToString();
                    report.AddBullet($"LinkType (after Open): {info.LinkType}");
                    report.AddBullet($"TimestampResolution (after Open): {info.OpenedTimestampResolution}");
                    if (info.Loopback || info.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                        || info.Name.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(info.FriendlyName, "Adapter for loopback traffic capture", StringComparison.OrdinalIgnoreCase))
                    {
                        var isNull = string.Equals(info.LinkType, nameof(LinkLayers.Null), StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(info.LinkType, "Null", StringComparison.OrdinalIgnoreCase);
                        report.Assumption("loopback-dlt-null",
                            "NPF_Loopback / loopback LinkType is DLT_NULL (LinkLayers.Null)",
                            isNull ? $"confirmed ({info.LinkType})" : $"refuted ({info.LinkType})");
                    }
                }
                catch (Exception ex)
                {
                    report.AddBullet($"Open for LinkType failed: {Describe(ex)}");
                }
                finally
                {
                    try { if (live.Opened) live.Close(); } catch { /* ignore */ }
                }
            }

            if (!result.Any(d => d.Loopback || d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)))
            {
                report.Assumption("loopback-dlt-null",
                    "NPF_Loopback / loopback LinkType is DLT_NULL (LinkLayers.Null)",
                    "unknown (no loopback device found)");
            }
        }
        catch (Exception ex)
        {
            report.AddCode("EnumerateDevices failed:\n" + Describe(ex));
        }

        return result;
    }

    static void PreviewD10(ReportBuilder report, List<DeviceInfo> devices)
    {
        report.AddHeading("3. D10 auto-selection preview");
        report.AddParagraph("Scenario: proxy bind 0.0.0.0:7547, upstream localhost:7557 (design §D10 / §11.2).");

        var lanUp = devices.Where(d =>
            !d.Loopback
            && !d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            && d.Addresses.Any(a => IsUsableIpv4(a))).ToList();

        var loopback = devices.Where(d =>
            d.Loopback
            || d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.FriendlyName, "Adapter for loopback traffic capture", StringComparison.OrdinalIgnoreCase)
            || d.Name is "lo" or "lo0").ToList();

        report.AddParagraph("Bind 0.0.0.0 → select all up physical NICs with IPv4:");
        foreach (var d in lanUp)
            report.AddBullet($"LAN candidate: {d.Name} ({d.FriendlyName}) addrs=[{string.Join(", ", d.Addresses)}]");
        if (lanUp.Count == 0)
            report.AddBullet("(none)");

        report.AddParagraph("Upstream localhost:7557 → include Loopback:");
        foreach (var d in loopback)
            report.AddBullet($"Loopback candidate: {d.Name} ({d.FriendlyName}) LinkType={d.LinkType}");
        if (loopback.Count == 0)
            report.AddBullet("(none)");

        var auto = lanUp.Concat(loopback).DistinctBy(d => d.Name).ToList();
        report.AddParagraph("D10 auto set:");
        foreach (var d in auto)
            report.AddBullet(d.Name);

        var ok = lanUp.Count >= 1 && loopback.Count >= 1;
        report.Assumption("d10-auto-lan-plus-loopback",
            "D10 auto-selection picks LAN NIC(s) for bind 0.0.0.0 plus Loopback when upstream is localhost",
            ok ? "confirmed (candidates present for both)" : "refuted or incomplete on this host");
    }

    static List<DeviceInfo> SelectDevices(List<DeviceInfo> all, Options opts)
    {
        if (opts.DeviceNames.Count > 0)
        {
            return all.Where(d =>
                opts.DeviceNames.Any(n =>
                    d.Name.Contains(n, StringComparison.OrdinalIgnoreCase)
                    || (d.FriendlyName?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (d.Description?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        }

        // Default: D10 auto for bind 0.0.0.0 + upstream localhost.
        var lanUp = all.Where(d =>
            !d.Loopback
            && !d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            && d.Addresses.Any(IsUsableIpv4)).ToList();
        var loopback = all.Where(d =>
            d.Loopback
            || d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase)
            || d.Name is "lo" or "lo0").ToList();
        return lanUp.Concat(loopback).DistinctBy(d => d.Name).ToList();
    }

    static DeviceConfiguration NewConfig(ReportBuilder report, string deviceLabel)
    {
        var cfg = new DeviceConfiguration
        {
            Mode = DeviceModes.None,
            Snaplen = 65535,
            ReadTimeout = 100,
            KernelBufferSize = 16 * 1024 * 1024,
            TimestampResolution = TimestampResolution.Nanosecond,
        };
        cfg.ConfigurationFailed += (_, e) =>
        {
            report.AddBullet($"ConfigurationFailed on {deviceLabel}: property={e.Property} error={e.Error} message={e.Message}");
            if (string.Equals(e.Property, nameof(DeviceConfiguration.TimestampResolution), StringComparison.OrdinalIgnoreCase)
                || e.Error == PcapError.TimestampPrecisionNotSupported)
            {
                report.Assumption("timestamp-nanosecond",
                    "TimestampResolution.Nanosecond accepted by device",
                    $"refuted on {deviceLabel}: {e.Error} — {e.Message}");
            }

            if (string.Equals(e.Property, nameof(DeviceConfiguration.KernelBufferSize), StringComparison.OrdinalIgnoreCase))
            {
                report.Assumption("kernel-buffer-16mib",
                    "KernelBufferSize=16MiB accepted",
                    $"refuted on {deviceLabel}: {e.Error} — {e.Message}");
            }
        };
        return cfg;
    }

    static async Task RunCaptureAsync(ReportBuilder report, List<DeviceInfo> selected, Options opts)
    {
        report.AddHeading("4. Open / filter / capture");
        report.AddParagraph($"Seconds: {opts.Seconds}; devices: {string.Join(", ", selected.Select(d => d.Name))}");

        var sessions = new List<CaptureSession>();
        var filter = "(tcp port 7547) or (tcp port 7557) or icmp or (udp port 53)";
        report.AddBullet($"BPF filter: `{filter}`");

        foreach (var info in selected)
        {
            var session = new CaptureSession(info);
            sessions.Add(session);
            try
            {
                var cfg = NewConfig(report, info.Name);
                info.Device.Open(cfg);
                report.AddBullet($"Opened {info.Name}: LinkType={info.Device.LinkType}, TimestampResolution={info.Device.TimestampResolution}");
                if (info.Device.TimestampResolution == TimestampResolution.Nanosecond)
                {
                    report.Assumption("timestamp-nanosecond",
                        "TimestampResolution.Nanosecond accepted by device",
                        $"confirmed on {info.Name}");
                }
                else
                {
                    report.Assumption("timestamp-nanosecond",
                        "TimestampResolution.Nanosecond accepted by device",
                        $"partial on {info.Name}: got {info.Device.TimestampResolution}");
                }

                try
                {
                    info.Device.Filter = filter;
                    report.AddBullet($"Filter set OK on {info.Name}");
                }
                catch (Exception ex)
                {
                    report.AddBullet($"Filter set FAILED on {info.Name}: {Describe(ex)}");
                }

                try
                {
                    info.Device.Filter = "this is not a valid bpf (((((";
                    report.AddBullet($"Unexpected: invalid filter accepted on {info.Name}");
                }
                catch (Exception ex)
                {
                    report.AddBullet($"Invalid filter rejected on {info.Name}: {ex.GetType().Name}: {ex.Message}");
                    report.Assumption("invalid-filter-throws",
                        "Setting an invalid BPF filter throws with libpcap's text",
                        "confirmed");
                }

                // Restore good filter.
                try { info.Device.Filter = filter; } catch { /* ignore */ }

                info.Device.OnPacketArrival += session.OnArrival;
                info.Device.StartCapture();
                report.AddBullet($"StartCapture on {info.Name}");
            }
            catch (Exception ex)
            {
                report.AddBullet($"Failed to start {info.Name}: {Describe(ex)}");
                report.Assumption("admin-only-npcap",
                    "Admin-only Npcap / access denied surfaces as a clear open failure",
                    $"observed on {info.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        report.AddHeading("5. Self-traffic (listeners + 3 POSTs)");
        var traffic = await GenerateSelfTrafficAsync(report);

        report.AddParagraph($"Capturing for {opts.Seconds}s (self-traffic already issued; waiting for more / drain)…");
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.Seconds)));

        report.AddHeading("6. Callback / decode notes");
        foreach (var s in sessions)
        {
            s.StopAndSummarize(report);
        }

        report.AddHeading("5b. Which device saw each POST?");
        CorrelateTraffic(report, sessions, traffic);

        report.AddHeading("7. Statistics");
        foreach (var s in sessions)
            s.WriteStats(report);

        report.AddHeading("8. CaptureFileWriterDevice sample .pcap");
        foreach (var s in sessions)
            s.WritePcap(report);

        foreach (var s in sessions)
            s.DisposeQuiet();
    }

    static async Task<SelfTrafficResult> GenerateSelfTrafficAsync(ReportBuilder report)
    {
        var result = new SelfTrafficResult();
        TcpListener? proxyListener = null;
        TcpListener? upstreamListener = null;
        var cts = new CancellationTokenSource();
        try
        {
            proxyListener = new TcpListener(IPAddress.Any, 7547);
            proxyListener.Start();
            report.AddBullet("TcpListener 0.0.0.0:7547 started");
            _ = AcceptLoopAsync(proxyListener, "proxy-7547", report, cts.Token);

            upstreamListener = new TcpListener(IPAddress.Loopback, 7557);
            upstreamListener.Start();
            report.AddBullet("TcpListener 127.0.0.1:7557 started");
            _ = AcceptLoopAsync(upstreamListener, "upstream-7557", report, cts.Token);

            await Task.Delay(200);

            var lanIp = PickLanIp();
            result.LanIp = lanIp?.ToString();
            report.AddBullet($"LAN IP chosen for POST #3: {result.LanIp ?? "(none found)"}");

            result.Post1 = await PostAsync(report, "POST#1 127.0.0.1:7557", "http://127.0.0.1:7557/acs");
            result.Post2 = await PostAsync(report, "POST#2 127.0.0.1:7547", "http://127.0.0.1:7547/acs");
            if (lanIp is not null)
                result.Post3 = await PostAsync(report, $"POST#3 {lanIp}:7547", $"http://{lanIp}:7547/acs");
            else
                report.AddBullet("POST#3 skipped (no LAN IPv4)");

            report.Assumption("lan-ip-on-loopback",
                "Self-connection to the PC's own LAN IP appears on Loopback (not the LAN NIC) on Windows",
                "unknown until Windows matrix correlates POST#3 device");
        }
        catch (Exception ex)
        {
            report.AddCode("Self-traffic setup failed:\n" + Describe(ex));
        }
        finally
        {
            cts.Cancel();
            try { proxyListener?.Stop(); } catch { /* ignore */ }
            try { upstreamListener?.Stop(); } catch { /* ignore */ }
        }

        return result;
    }

    static async Task AcceptLoopAsync(TcpListener listener, string tag, ReportBuilder report, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            var buf = new byte[4096];
                            _ = await stream.ReadAsync(buf, ct);
                            var body = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
                            await stream.WriteAsync(body, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        report.AddBullet($"{tag} accept handler: {ex.GetType().Name}: {ex.Message}");
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            report.AddBullet($"{tag} accept loop ended: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static async Task<PostAttempt> PostAsync(ReportBuilder report, string label, string url)
    {
        var attempt = new PostAttempt { Label = label, Url = url, StartedUtc = DateTime.UtcNow };
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var content = new StringContent("<soap/>", Encoding.UTF8, "text/xml");
            var resp = await http.PostAsync(url, content);
            attempt.StatusCode = (int)resp.StatusCode;
            attempt.Ok = true;
            report.AddBullet($"{label} → HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            attempt.Error = $"{ex.GetType().Name}: {ex.Message}";
            report.AddBullet($"{label} → FAILED {attempt.Error}");
        }

        attempt.EndedUtc = DateTime.UtcNow;
        return attempt;
    }

    static void CorrelateTraffic(ReportBuilder report, List<CaptureSession> sessions, SelfTrafficResult traffic)
    {
        void Match(PostAttempt? post, int destPort, string? destHostHint)
        {
            if (post is null) return;
            report.AddSubheading(post.Label);
            var hits = new List<string>();
            foreach (var s in sessions)
            {
                var n = s.Frames.Count(f => f.DestPort == destPort || f.SourcePort == destPort);
                if (n > 0)
                    hits.Add($"{s.Info.Name}: {n} frame(s) touching port {destPort} (sample {string.Join("; ", s.Frames.Where(f => f.DestPort == destPort || f.SourcePort == destPort).Take(3).Select(f => f.Summary))})");
            }

            if (hits.Count == 0)
                report.AddBullet("No matching frames observed on selected devices (filter/timing/device selection?).");
            else
                foreach (var h in hits) report.AddBullet(h);

            if (post.Label.StartsWith("POST#3", StringComparison.Ordinal))
            {
                var onLoop = sessions.Where(s => s.Info.Loopback || s.Info.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                    .Any(s => s.Frames.Any(f => f.DestPort == 7547 || f.SourcePort == 7547));
                var onLan = sessions.Where(s => !(s.Info.Loopback || s.Info.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)))
                    .Any(s => s.Frames.Any(f => f.DestPort == 7547 || f.SourcePort == 7547 && destHostHint is not null && (f.DestIp == destHostHint || f.SourceIp == destHostHint)));
                report.Assumption("lan-ip-on-loopback",
                    "Self-connection to the PC's own LAN IP appears on Loopback (not the LAN NIC) on Windows",
                    onLoop && !onLan ? "confirmed"
                    : onLoop && onLan ? "partial (seen on both)"
                    : onLan ? "refuted (LAN NIC only)"
                    : "unknown (no frames)");
            }
        }

        Match(traffic.Post1, 7557, "127.0.0.1");
        Match(traffic.Post2, 7547, "127.0.0.1");
        Match(traffic.Post3, 7547, traffic.LanIp);
    }

    static void WriteAssumptionsTable(ReportBuilder report)
    {
        report.AddHeading("9. Assumptions → confirmed / refuted / unknown");
        report.WriteAssumptionsTable();
    }

    static void WriteReport(Options opts, ReportBuilder report)
    {
        var path = string.IsNullOrWhiteSpace(opts.OutPath) ? "report.md" : opts.OutPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir ? dir : ".");
        File.WriteAllText(path, report.ToString());
    }

    static IPAddress? PickLanIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(ua.Address)
                    && !ua.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    return ua.Address;
            }
        }

        return null;
    }

    static bool IsUsableIpv4(string addressText)
    {
        if (!IPAddress.TryParse(addressText.Split('/')[0].Trim(), out var ip)) return false;
        return ip.AddressFamily == AddressFamily.InterNetwork
               && !IPAddress.IsLoopback(ip)
               && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal);
    }

    static bool IsElevated()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                using var id = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
            }

            // Best-effort on Unix: euid 0.
            return geteuid() == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    static string Describe(Exception ex) => ex.ToString();
}

internal sealed class Options
{
    public bool ProbeOnly { get; init; }
    public int Seconds { get; init; } = 30;
    public List<string> DeviceNames { get; init; } = new();
    public string OutPath { get; init; } = "report.md";

    public static Options Parse(string[] args)
    {
        var probeOnly = false;
        var seconds = 30;
        var devices = new List<string>();
        var outPath = "report.md";
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--probe-only":
                    probeOnly = true;
                    break;
                case "--seconds" when i + 1 < args.Length && int.TryParse(args[++i], out var s):
                    seconds = Math.Clamp(s, 1, 600);
                    break;
                case "--device" when i + 1 < args.Length:
                    devices.Add(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "-h" or "--help":
                    Console.WriteLine("PcapSpike [--probe-only] [--seconds 30] [--device name]… [--out report.md]");
                    break;
            }
        }

        return new Options
        {
            ProbeOnly = probeOnly,
            Seconds = seconds,
            DeviceNames = devices,
            OutPath = outPath,
        };
    }
}

internal sealed class DeviceInfo
{
    public DeviceInfo(LibPcapLiveDevice device)
    {
        Device = device;
        Name = device.Name ?? "";
        Description = device.Description ?? "";
        FriendlyName = device.Interface?.FriendlyName ?? "";
        Mac = device.MacAddress?.ToString() ?? device.Interface?.MacAddress?.ToString() ?? "";
        Loopback = device.Loopback;
        Flags = device.Flags;
        Addresses = new List<string>();
        if (device.Addresses is not null)
        {
            foreach (var addr in device.Addresses)
            {
                var a = addr.Addr?.ipAddress?.ToString() ?? addr.Addr?.ToString();
                if (!string.IsNullOrWhiteSpace(a)) Addresses.Add(a);
            }
        }

        if (device.Interface?.Addresses is not null)
        {
            foreach (var addr in device.Interface.Addresses)
            {
                var a = addr.Addr?.ipAddress?.ToString() ?? addr.Addr?.ToString();
                if (!string.IsNullOrWhiteSpace(a) && !Addresses.Contains(a)) Addresses.Add(a);
            }
        }

        var ts = new List<string>();
        if (device.Interface?.TimestampsSupported is { } supported)
        {
            foreach (var t in supported)
            {
                var s = t.ToString();
                if (!string.IsNullOrEmpty(s))
                    ts.Add(s);
            }
        }
        TimestampsSupported = ts;
    }

    public LibPcapLiveDevice Device { get; }
    public string Name { get; }
    public string FriendlyName { get; }
    public string Description { get; }
    public List<string> Addresses { get; }
    public string Mac { get; }
    public bool Loopback { get; }
    public uint Flags { get; }
    public List<string> TimestampsSupported { get; }
    public string? LinkType { get; set; }
    public string? OpenedTimestampResolution { get; set; }
}

internal sealed class CaptureSession
{
    static readonly char[] s_extraInvalidNameChars = { '\\', '/', ':', '{', '}' };
    static readonly char[] s_invalidNameChars =
        Path.GetInvalidFileNameChars().Concat(s_extraInvalidNameChars).Distinct().ToArray();

    readonly object _gate = new();
    string? _callbackThread;
    int _arrivalCount;
    int _decodeErrors;
    int _badTcpChecksum;
    int _oversized;
    readonly List<FrameHit> _frames = new();
    readonly List<byte[]> _rawForPcap = new();
    LinkLayers _link = LinkLayers.Ethernet;
    bool _linkSet;

    public CaptureSession(DeviceInfo info) => Info = info;
    public DeviceInfo Info { get; }
    public IReadOnlyList<FrameHit> Frames
    {
        get { lock (_gate) return _frames.ToList(); }
    }

    public void OnArrival(object sender, PacketCapture e)
    {
        // PT-1 rule: copy bytes only on the callback thread.
        byte[] copy;
        PosixTimeval timeval;
        try
        {
            copy = e.Data.ToArray();
            timeval = e.Header.Timeval;
            if (_callbackThread is null)
                _callbackThread = $"{Environment.CurrentManagedThreadId}:{Thread.CurrentThread.Name}:{Thread.CurrentThread.IsThreadPoolThread}";
            Interlocked.Increment(ref _arrivalCount);
        }
        catch
        {
            return;
        }

        // Decode off-thread (inline Task for spike simplicity; production uses LongRunning ingest).
        ThreadPool.QueueUserWorkItem(_ => Decode(copy, timeval));
    }

    void Decode(byte[] data, PosixTimeval timeval)
    {
        try
        {
            LinkLayers link;
            try { link = Info.Device.LinkType; }
            catch { link = LinkLayers.Ethernet; }

            if (!_linkSet)
            {
                _link = link;
                _linkSet = true;
            }

            lock (_gate)
            {
                if (_rawForPcap.Count < 200)
                    _rawForPcap.Add(data);
            }

            Packet? packet = null;
            try
            {
                packet = Packet.ParsePacket(link, data);
            }
            catch
            {
                Interlocked.Increment(ref _decodeErrors);
                return;
            }

            var ip = packet.Extract<IPPacket>();
            var tcp = packet.Extract<TcpPacket>();
            var udp = packet.Extract<UdpPacket>();
            var icmp = packet.Extract<IcmpV4Packet>() ?? (Packet?)packet.Extract<IcmpV6Packet>();

            if (tcp is not null)
            {
                try
                {
                    if (!tcp.ValidTcpChecksum)
                        Interlocked.Increment(ref _badTcpChecksum);
                }
                catch { /* ValidTcpChecksum can throw on truncated */ }

                if (tcp.PayloadData?.Length > 1460)
                    Interlocked.Increment(ref _oversized);
            }

            var hit = new FrameHit
            {
                Length = data.Length,
                Timeval = timeval.ToString(),
                SourceIp = ip?.SourceAddress?.ToString(),
                DestIp = ip?.DestinationAddress?.ToString(),
                SourcePort = tcp?.SourcePort ?? udp?.SourcePort ?? 0,
                DestPort = tcp?.DestinationPort ?? udp?.DestinationPort ?? 0,
                Summary = ip is null
                    ? $"len={data.Length} link={link} {(icmp is not null ? "icmp" : "non-ip")}"
                    : $"{ip.SourceAddress}:{tcp?.SourcePort ?? udp?.SourcePort} → {ip.DestinationAddress}:{tcp?.DestinationPort ?? udp?.DestinationPort} len={data.Length}",
            };
            lock (_gate)
            {
                if (_frames.Count < 500)
                    _frames.Add(hit);
            }
        }
        catch
        {
            Interlocked.Increment(ref _decodeErrors);
        }
    }

    public void StopAndSummarize(ReportBuilder report)
    {
        try
        {
            if (Info.Device.Started)
                Info.Device.StopCapture();
        }
        catch (Exception ex)
        {
            report.AddBullet($"StopCapture {Info.Name}: {ex.GetType().Name}: {ex.Message}");
        }

        // Allow queued decodes to finish.
        Thread.Sleep(200);

        report.AddSubheading(Info.Name);
        report.AddBullet($"OnPacketArrival count: {_arrivalCount}");
        report.AddBullet($"First callback thread: {_callbackThread ?? "(none)"}");
        report.AddBullet($"IsThreadPoolThread on callback: {(_callbackThread?.Split(':').LastOrDefault() ?? "?")}");
        report.Assumption("callback-not-threadpool",
            "OnPacketArrival runs on SharpPcap's capture thread (not the thread pool)",
            _callbackThread is null ? "unknown (no packets)"
            : _callbackThread.EndsWith(":False", StringComparison.Ordinal) ? "confirmed"
            : "refuted (thread-pool thread)");
        report.AddBullet($"Decoded frames kept: {Frames.Count}");
        report.AddBullet($"Decode errors: {_decodeErrors}");
        report.AddBullet($"TCP ValidTcpChecksum=false: {_badTcpChecksum} (offload/LSO sign if on sent packets)");
        report.AddBullet($"TCP payload > 1460 (LSO/RSC sign): {_oversized}");
        report.AddBullet($"Device TimestampResolution: {Safe(() => Info.Device.TimestampResolution.ToString())}");
    }

    public void WriteStats(ReportBuilder report)
    {
        try
        {
            var stats = Info.Device.Statistics;
            report.AddBullet($"{Info.Name}: Received={stats.ReceivedPackets} Dropped={stats.DroppedPackets} InterfaceDropped={stats.InterfaceDroppedPackets}");
        }
        catch (Exception ex)
        {
            report.AddBullet($"{Info.Name}: Statistics failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void WritePcap(ReportBuilder report)
    {
        var safeName = string.Join("_", Info.Name.Split(s_invalidNameChars));
        if (safeName.Length > 40) safeName = safeName[^40..];
        var path = Path.GetFullPath($"spike-{safeName}.pcap");
        try
        {
            List<byte[]> frames;
            lock (_gate) frames = _rawForPcap.ToList();
            if (frames.Count == 0)
            {
                report.AddBullet($"{Info.Name}: no frames to write → skipped {path}");
                return;
            }

            using var writer = new CaptureFileWriterDevice(path, FileMode.Create);
            writer.Open(new DeviceConfiguration { LinkLayerType = _link });
            foreach (var f in frames)
                writer.Write(f);
            writer.Close();
            report.AddBullet($"{Info.Name}: wrote {frames.Count} frames → {path}");
        }
        catch (Exception ex)
        {
            report.AddBullet($"{Info.Name}: CaptureFileWriterDevice failed: {Describe(ex)}");
        }
    }

    public void DisposeQuiet()
    {
        try
        {
            Info.Device.OnPacketArrival -= OnArrival;
            if (Info.Device.Started) Info.Device.StopCapture();
            if (Info.Device.Opened) Info.Device.Close();
            Info.Device.Dispose();
        }
        catch { /* ignore */ }
    }

    static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return ex.GetType().Name; }
    }

    static string Describe(Exception ex) => ex.ToString();
}

internal sealed class FrameHit
{
    public int Length { get; init; }
    public string? Timeval { get; init; }
    public string? SourceIp { get; init; }
    public string? DestIp { get; init; }
    public int SourcePort { get; init; }
    public int DestPort { get; init; }
    public string Summary { get; init; } = "";
}

internal sealed class SelfTrafficResult
{
    public string? LanIp { get; set; }
    public PostAttempt? Post1 { get; set; }
    public PostAttempt? Post2 { get; set; }
    public PostAttempt? Post3 { get; set; }
}

internal sealed class PostAttempt
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public bool Ok { get; set; }
    public int StatusCode { get; set; }
    public string? Error { get; set; }
}

internal sealed class ReportBuilder
{
    readonly StringBuilder _sb = new();
    readonly Dictionary<string, AssumptionRow> _assumptions = new(StringComparer.Ordinal);

    public void AddHeading(string text) => _sb.AppendLine().Append("## ").AppendLine(text).AppendLine();
    public void AddSubheading(string text) => _sb.AppendLine().Append("### ").AppendLine(text).AppendLine();
    public void AddParagraph(string text) => _sb.AppendLine(text).AppendLine();
    public void AddBullet(string text) => _sb.Append("- ").AppendLine(text);
    public void AddCode(string text)
    {
        _sb.AppendLine();
        _sb.AppendLine("```");
        _sb.AppendLine(text.TrimEnd());
        _sb.AppendLine("```");
        _sb.AppendLine();
    }

    public void Assumption(string id, string statement, string? result)
    {
        if (!_assumptions.TryGetValue(id, out var row))
        {
            row = new AssumptionRow { Id = id, Statement = statement };
            _assumptions[id] = row;
        }

        if (!string.IsNullOrWhiteSpace(result))
            row.Result = result;
    }

    public void WriteAssumptionsTable()
    {
        _sb.AppendLine("| Id | Assumption | Result |");
        _sb.AppendLine("|---|---|---|");
        foreach (var row in _assumptions.Values.OrderBy(r => r.Id))
            _sb.Append("| ").Append(Escape(row.Id)).Append(" | ").Append(Escape(row.Statement))
                .Append(" | ").Append(Escape(row.Result ?? "unknown")).AppendLine(" |");
    }

    static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public override string ToString() => _sb.ToString();

    sealed class AssumptionRow
    {
        public string Id { get; init; } = "";
        public string Statement { get; init; } = "";
        public string? Result { get; set; }
    }
}
