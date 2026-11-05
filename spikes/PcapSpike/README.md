# PcapSpike (PT0.1) — throw-away Npcap / libpcap probe

Standalone console tool that checks the native-capture assumptions in
`docs/PACKET_TRACE_DESIGN.md` before production `AcsTool.PacketCapture` code.
**Not** part of `AcsTool.sln`. Only the findings (and later sample `.pcap`
fixtures) are meant to be merged; this branch itself is never merged to `main`.

## Packages

- SharpPcap **6.3.1**
- PacketDotNet **1.4.8**
- `ManagePackageVersionsCentrally=false` (pins above; ignores repo central props)

## Build (Linux or Windows)

```bash
dotnet build -c Release
# warnings-as-errors
```

Publish a Windows self-contained single-file binary from Linux:

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./publish-win-x64
```

## CLI

```text
PcapSpike [--probe-only] [--seconds 30] [--device name]… [--out report.md]
```

| Flag | Meaning |
|---|---|
| `--probe-only` | Load native pcap, print versions / elevated / OS; **exit 0** if OK, **exit 2** if missing (expected `DllNotFoundException` without Npcap/libpcap). |
| `--seconds N` | Capture duration after self-traffic (default 30). |
| `--device name` | Restrict capture to devices whose name/friendly name/description contains `name` (repeatable). Default = D10 auto (LAN IPv4 NICs for bind `0.0.0.0` + Loopback for upstream `localhost:7557`). |
| `--out path` | Markdown report path (default `report.md`). |

Never throws to the console: failures are caught and written into the report.

## What it checks

1. **Probe** — `Pcap.Version` / `SharpPcapVersion` / `LibpcapVersion`, OS, bitness, elevated; `CaptureDeviceList` without Npcap → expect native load failure.
2. **Devices** — Name, FriendlyName, Description, Addresses, Mac, Loopback, Flags, TimestampsSupported; LinkType after Open (expect `NPF_Loopback` → `Null`).
3. **D10 preview** — auto device set for bind `0.0.0.0` + upstream `localhost:7557`.
4. **Open** — `DeviceConfiguration` Mode=None, Snaplen=65535, ReadTimeout=100, KernelBufferSize=16MiB, TimestampResolution=Nanosecond; `ConfigurationFailed`; BPF `tcp 7547/7557 + icmp + udp 53`; invalid filter.
5. **Self-traffic** — listeners on `0.0.0.0:7547` and `127.0.0.1:7557`; three POSTs (`127.0.0.1:7557`, `:7547`, LAN-IP:7547); which device each appears on (design assumes LAN-IP → Loopback on Windows).
6. **OnPacketArrival** — copy `e.Data` only; record callback thread once; decode off-thread; counts, checksum/LSO signs, timestamp resolution.
7. **Statistics** — Received / Dropped / InterfaceDropped.
8. **CaptureFileWriterDevice** → `spike-<device>.pcap`.
9. **report.md** — assumption → confirmed / refuted / unknown table.

## Windows matrix (Fariborz / TesterBot)

Npcap from <https://npcap.com> (not bundled). Recommended: defaults with **"Restrict Npcap driver's access to Administrators only" unchecked**.

| Run | Expect |
|---|---|
| (a) No Npcap, `--probe-only` | exit **2** |
| (b) Npcap, normal user, full run | exit 0; report fills assumptions; `.pcap` samples written |
| (c) Optional: admin-only Npcap | open failure / UAC behaviour recorded |

```powershell
.\PcapSpike.exe --probe-only --out probe.md
.\PcapSpike.exe --seconds 30 --out report.md
```

## Linux smoke (this box)

```bash
dotnet run -c Release -- --probe-only --out /tmp/pcapspike-probe.md
# without libpcap installed → exit 2
```

## SharpPcap API notes observed on Linux (no libpcap)

- `Pcap.SharpPcapVersion` / `LibpcapVersion` / `Version` do **not** throw when the native library is missing; `LibpcapVersion` is `0.0` and `Version` is a "can't be identified" string. The failure appears on first `CaptureDeviceList.Instance` access as `DllNotFoundException`.
- On Linux, SharpPcap 6.3.1's `NativeLibrary` probe asks for **`wpcap`** first (Windows name) before platform aliases; the exception text therefore mentions `wpcap.so` / `libwpcap.so` even on Debian. Production `Probe()` should treat any `DllNotFoundException` (and likely `PcapException` from findalldevs) as NotInstalled regardless of the soname in the message.
- `PacketCapture.Data` is a `ReadOnlySpan<byte>` (copy immediately). `DeviceConfiguration.ConfigurationFailed` is an event on the configuration object, not on the device.
- `CaptureFileWriterDevice` ctor is `(string path, FileMode mode)`; open with `LinkLayerType` matching the live device.
