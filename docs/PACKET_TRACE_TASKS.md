# Packet Trace — task breakdown (PT0–PT4)

| Field | Value |
|---|---|
| Document | `docs/PACKET_TRACE_TASKS.md` (design: [`PACKET_TRACE_DESIGN.md`](PACKET_TRACE_DESIGN.md)) |
| Author | PlannerBot (for CoderBot → ReviewerBot / TesterBot) |
| Date | 2026-11-05 |
| Baseline | `main` @ `479639b` |
| Target framework | net10.0 (App net10.0-windows) |
| Status | **Approved 2026-11-05** — Fariborz: "Its approved skip the M5 and build proxy-tab packet plan." All decisions D1–D16 locked (design §14). **D1 overridden: PT starts now on current `main`; M5 (TLS) is deferred** and will rebase onto PT later. |
| Revision | **r3 (2026-11-05):** decisions locked; "after M5" dependencies removed; M5 follow-ups listed in §"M5 rebase notes".<br>**r2 (2026-11-05):** Proxy-tab integration promoted into the MVP (Fariborz: "Inform arrives → see the session and its packets in Proxy"). PT1 re-ordered around that story; the standalone Packet Trace tab is now secondary and off the critical path. |

Sizes: **S** ≤ ½ day · **M** 1–2 days · **L** 3–5 days (one developer). Task IDs use the prefix `PT` so they cannot collide with
PLAN `Tn.x` or `M5-` IDs. "Files" lists the main files; **(new)** marks new files. Event ids for logging: **7000–7099**.

Branch convention: one branch per milestone (`feat/packet-trace-pt1`, …), merged after ReviewerBot + TesterBot like earlier milestones.
CI must stay green on Linux and Windows whether or not libpcap/Npcap is installed. Observed: the Linux runner has libpcap
(Available path, non-root); the Windows runner has no Npcap (NotInstalled path).

---

## Milestone overview

| Milestone | Goal | Exit criteria | Estimate |
|---|---|---|---|
| **PT0** Spike & groundwork | Prove packages + native loading on Windows; project skeleton; Core endpoint plumbing | Skeleton builds on both CIs with `CI=true`; Windows spike notes recorded; `ExchangeEndpoints` populated by proxy and simulator | 2–3 d |
| **PT1** MVP (r2/r3) | **Proxy tab shows each session's/exchange's packets** (Capture packets toggle + session-detail Packets tab, both legs, list/tree/hex, live) + exchange-scope correlation + PCAPNG export with masking + Wireshark launch; Packet Trace tab as capture console + offline open (PT1.13, may slip to PT1b) | Acceptance PT1-A1…A10 below; Windows checklist W-PT-01…16 (incl. 04b–04d) passed | **16–21 d** (r1: 13–17 d). Proxy-story critical path ≈ 14–18 d; PT1.13 + file reader ≈ 2–3 d can become PT1b |
| **PT2** Streams & HTTP/CWMP | TCP reassembly, HTTP/1.1 extraction, CWMP summary from wire, Follow TCP Stream, exact exchange-level match (upgrades the Packets tab basis), wire-vs-app diff, ladder/flow view in the Packets tab | PT2-A1…A8 | 10–13 d |
| **PT3** Offline & polish | Display filter, wire sessions for files, feed context-menu shortcuts, TLS handshake details, capture spooling | PT3-A1…A6 | 7–9 d |
| **PT4** Later / maybe | Linux key log, ETW/pktmon backend (no Npcap), stats graphs | Only on request | — |

**MVP cut = PT0 + PT1** (≈ 18–24 d, r1: 15–20 d). If a smaller first drop is wanted: PT0 + PT1 without PT1.13 and the file reader (≈ 16–21 d) still delivers the full Proxy story; offline files then follow in PT1b.

---

## PT0 — Spike & groundwork

### PT0.1 Windows Npcap spike (throw-away tool; only its findings are merged) — S
- **Description:** A standalone console tool `spikes/PcapSpike` (net10.0, **not** in `AcsTool.sln`) on branch `spike/pt0-npcap`
  that answers the native-layer questions the design depends on, and writes a Markdown report. Built on Linux; run on a Windows
  PC with Npcap (by Fariborz or TesterBot). Checks: `Pcap.Version`/`SharpPcapVersion`/`LibpcapVersion`; device list (name,
  friendly name, description, addresses, MAC, loopback flag, link type after open, supported timestamp types); behaviour with
  Npcap missing (exception type/message) and with "admin only" Npcap (UAC); D10 auto-selection preview; 30 s capture on the
  chosen LAN NIC + `NPF_Loopback` with self-generated traffic (local listeners on 7547/7557 and a connection to the PC's own LAN
  IP → verify it appears on Loopback, design §11.2); per-device `ICaptureStatistics`; `KernelBufferSize` and
  `TimestampResolution.Nanosecond` accepted or not; capture-callback thread identity (not thread pool); write a sample `.pcap`
  via `CaptureFileWriterDevice` for fixtures. Full handoff: see the PT0.1 brief delivered with r3 (DevOrchestrator).
- **Files:** `spikes/PcapSpike/*` on `spike/pt0-npcap` (never merged); findings → `docs/IMPLEMENTATION_NOTES.md` § "Packet trace
  spike (PT0.1)" and sample captures → `tests/fixtures/pcap/` (merged with PT0.2).
- **Deps:** none (current `main`; no M5 dependency).
- **Acceptance:** Report answers each check with observed values; confirms or refutes: `wpcap.dll` found under `System32\Npcap`
  with no extra setup; loopback link type (expected DLT_NULL); missing Npcap → `DllNotFoundException`; self-connection to the LAN
  IP appears on Loopback; D10 auto-selection picks LAN + Loopback for defaults. Any refuted assumption is listed with the design
  section it affects.

### PT0.2 Project skeleton + packages — S
- **Description:** Add `src/AcsTool.PacketCapture` (net10.0, refs Core, `PackageReference SharpPcap` + `PacketDotNet`),
  `tests/AcsTool.PacketCapture.Tests`, solution entries, `InternalsVisibleTo`, central versions `SharpPcap 6.3.1`,
  `PacketDotNet 1.4.8` in `Directory.Packages.props`. `ServiceCollectionExtensions.AddAcsToolPacketCapture()`,
  called from `AddAcsToolPresentation`. **Landed (a5a4cd1):** brought forward from PT1.1 — `ICaptureBackend.Probe()`,
  `CaptureBackendInfo`/`CaptureBackendStatus`, `SharpPcapBackend.Probe()`, registered by `AddAcsToolPacketCapture()`.
  Still pending from PT0.1: spike findings in `IMPLEMENTATION_NOTES.md` and `tests/fixtures/pcap/` (`spike/pt0-npcap`). `THIRD-PARTY-NOTICES.md` (SharpPcap MIT, PacketDotNet MPL-2.0 + source URL).
- **Files:** `AcsTool.sln`, `Directory.Packages.props`, `src/AcsTool.PacketCapture/AcsTool.PacketCapture.csproj` (new),
  `src/AcsTool.PacketCapture/ServiceCollectionExtensions.cs` (new), `src/AcsTool.Presentation/AcsTool.Presentation.csproj`,
  `src/AcsTool.Presentation/ServiceCollectionExtensions.cs`, `tests/AcsTool.PacketCapture.Tests/*` (new), `THIRD-PARTY-NOTICES.md` (new), `README.md` (one line).
- **Deps:** D2 approved.
- **Acceptance:** `./build.sh` and `./build.ps1 -NoCoverage` pass with `CI=true` (warnings as errors, no NU15xx); Windows publish
  output contains `SharpPcap.dll` and `PacketDotNet.dll`; `--smoke-exit` still passes on the Windows runner (no native load at startup).

### PT0.3 Core: transport endpoints on exchanges — M
- **Description:** Close design gaps G1–G3 (design §6.2). Add `ExchangeEndpoints` record and `ExchangeRecord.Endpoints` (init
  property); `ISessionSink.SetExchangeEndpoints(Guid, int, ExchangeEndpoints)` as a **default interface method (no-op)**;
  implement in `InMemorySessionStore` (replace record under `_gate`, raise `Updated`). `PhaseTracker`: `LocalEndPoint`,
  `ConnectionSerial` (incremented in `BeginConnect`). `ConnectCallbackFactory`: pass `socket.LocalEndPoint`.
  Normalise IPv4-mapped IPv6 like `ProxyRequestHandler.Snapshot`.
- **Files:** `src/AcsTool.Core/Sessions/SessionModel.cs`, `src/AcsTool.Core/Sessions/InMemorySessionStore.cs`,
  `src/AcsTool.Core/Net/PhaseTracker.cs`, `src/AcsTool.Core/Net/ConnectCallbackFactory.cs`, tests in `tests/AcsTool.Core.Tests/SessionStoreTests.cs`, `NetTests.cs`.
- **Deps:** D13 (approved). Starts on current `main` — **no M5 dependency** (M5 deferred, D1 r3). M5 will later rebase onto these `PhaseTracker`/`ConnectCallbackFactory` changes.
- **Acceptance:** Store test: `SetExchangeEndpoints` on an existing exchange sets it and raises one `Updated`; unknown session/seq
  is a no-op; `CapturedBytes` unchanged. Net test: a real connect to a local `TcpListener` records `LocalEndPoint` with a non-zero
  port and `ConnectionSerial` 1, a second connection → 2; pooled reuse keeps the serial. Existing tests unchanged and green
  (test fakes implementing `ISessionSink` compile without edits).

### PT0.4 Proxy + simulator populate endpoints — S
- **Description:** `IncomingRequest` gains `LocalIp`, `LocalPort` (from `ctx.Connection.LocalIpAddress/LocalPort`).
  `ProxyRequestHandler.HandleAsync` calls `_sink.SetExchangeEndpoints(...)` after `CompleteExchange` with downstream (connection id,
  remote, local) and upstream (`session.Tracker.LocalEndPoint`, `RemoteEndPoint`, `ConnectionSerial`) — upstream null when the
  failure happened before connect. Simulator: `TransportResponse.Endpoints` (new optional init property) filled by
  `HttpCwmpTransport` from its `PhaseTracker`; `CwmpClientSession` calls `SetExchangeEndpoints`. Also the 413 orphan path (downstream only).
- **Files:** `src/AcsTool.Proxy/Model.cs`, `src/AcsTool.Proxy/ProxyRequestHandler.cs`, `src/AcsTool.Simulator/Transport/HttpCwmpTransport.cs`,
  `src/AcsTool.Simulator/Session/CwmpClientSession.cs`, tests in `tests/AcsTool.Proxy.Tests/ForwardingTests.cs`, `tests/AcsTool.Simulator.Tests/TransportAndEngineTests.cs`.
- **Deps:** PT0.3.
- **Acceptance:** Integration test (CPE client → `ProxyServer` → `FakeAcs` on 127.0.0.1): every completed exchange has
  `Endpoints` with downstream remote = client socket's local endpoint, downstream local port = `BoundPort`, upstream remote port =
  FakeAcs port, upstream local port = a port FakeAcs saw as remote (FakeAcs records `RemotePort` per request — add if missing).
  A CPE that reconnects mid-session gets a different `DownstreamRemote` on the later exchange. Simulator → FakeAcs: upstream filled,
  downstream null. Connect failure: upstream local null.

---

## PT1 — MVP (r2: Proxy-integrated)

**Primary story:** *Inform arrives → the Proxy tab shows the session **and** its packets.* Task order follows the critical path
to that story:

```
PT0.2 ─┬─ PT1.1 backend ─────────────┐
       ├─ PT1.3 decoder ─ PT1.4 streams ─┤
       └─ PT1.5 store/settings ──────┴─ PT1.6 pipeline ─ PT1.7 BPF ─┐
PT0.3/0.4 endpoints ────────────────────────────────── PT1.8 correlation + IPacketLookup ─┤
                                                         PT1.9 shared packet pane ───────┤
                                                         PT1.10 Proxy toolbar toggle ────┼─ PT1.11 Packets tab in session detail ─ PT1.12 export
PT1.2 file formats (writer on the critical path for PT1.12; reader only for PT1.13) ────────┘
                                          off the critical path:  PT1.13 Packet Trace tab (console) · PT1.14 docs
```

If time is short, **PT1.13 and the PCAP/PCAPNG reader half of PT1.2 may move to a "PT1b"** without affecting the Proxy story
(offline-file analysis, R7, then ships in PT1b).

### PT1.1 Capture backend abstraction + SharpPcap implementation — M
- **Already landed in PT0.2:** `ICaptureBackend.Probe()`, `CaptureBackendInfo/Status`, `SharpPcapBackend.Probe()` and its tests.
- **Description:** `ICaptureBackend`, `ICaptureSession`, `RawFrame`, `CaptureDeviceInfo`, `CaptureBackendInfo/Status`,
  `CaptureOpenOptions` (snaplen, promiscuous, kernel buffer, BPF, read timeout 100 ms, prefer ns timestamps), `CaptureCounters`,
  `CaptureOpenException` (user-facing message + `CaptureBackendStatus`). `SharpPcapBackend`: `Probe()` never throws (maps
  `DllNotFoundException` → NotInstalled; access errors → AccessDenied); `ListDevices()` from `CaptureDeviceList`/`LibPcapLiveDeviceList`
  with friendly names, addresses, loopback flag; `Open()` → `LibPcapLiveDevice.Open(DeviceConfiguration)`, sets `Filter`
  (bad filter → `CaptureOpenException` with libpcap's text); `Start(onFrame)` hooks `OnPacketArrival` and copies the span.
  `FakeCaptureBackend` in `AcsTool.Testing` (scripted devices, frames, counters, failure modes). **Auto device selection** (D10):
  `DeviceSelector.Auto(devices, ProxySettings, simulatorTargets)` — no UI needed for the Proxy path.
- **Files:** `src/AcsTool.PacketCapture/Backend/*.cs` (new), `tests/AcsTool.Testing/FakeCaptureBackend.cs` (new), tests.
- **Deps:** PT0.2.
- **Acceptance:** Without the native library (Windows CI runner, no Npcap; or a dev box without libpcap): `Probe()` returns
  `NotInstalled` with a message, no exception. With it (Linux CI runner has libpcap): `Probe()` returns without throwing. Fake backend contract
  tests (start/stop idempotent, stop joins, counters). `DeviceSelector` table tests (bind 0.0.0.0 / specific IP / loopback upstream /
  remote upstream / simulator targeting local proxy). Live test `[SkippableFact]` only with `ACSTOOL_PCAP_LIVE=1`.
- **Landed (5d747b9, review fixes on `feat/packet-trace-pt1`):** `ICaptureBackend.ListDevices/Open`, `ICaptureSession`,
  `SharpPcapBackend`/`SharpPcapCaptureSession`, `DeviceSelector.Auto`, `FakeCaptureBackend`. Review
  [REVIEW_feat-packet-trace-PT1.1.md](reviews/REVIEW_feat-packet-trace-PT1.1.md) fixes: M1 lock-free capture callback, Stop joins;
  m1 failed Start detaches; m2 loopback bind (D10); m3 native-free `LibPcapLiveDevice` test seam; m4 `StopCaptureTimeout` ≥ 750 ms;
  m5 docs; n1–n3. Details in `IMPLEMENTATION_NOTES.md`. Live open/start/stop still only verified with `ACSTOOL_PCAP_LIVE=1`.

### PT1.2 Managed PCAPNG writer (critical) + PCAP/PCAPNG reader (PT1b-able) — M
- **Description:** **Writer** (needed by export, PT1.12): `PcapngWriter` (SHB with `shb_userappl`, one IDB per interface,
  EPB with optional comment, `if_tsresol`=7) and `PcapWriter` (one link type per file). **Reader** (needed only by the Packet Trace
  tab's *Open file*, PT1.13): `PcapReader` (µs/ns magic, both endiannesses), `PcapngReader` (multi-section, IDB options, EPB/SPB/legacy
  OPB, unknown blocks skipped, `opt_comment`). Corrupt/truncated → `CaptureFileException` with block offset; reading stops at the budget.
- **Files:** `src/AcsTool.PacketCapture/Files/*.cs` (new), `tests/AcsTool.PacketCapture.Tests/FileFormatTests.cs` (new),
  small fixtures under `tests/fixtures/pcap/` (generated + one Wireshark-made file from PT0.1 + one `tcpdump` ns file).
- **Deps:** PT0.2.
- **Acceptance:** Round-trip write→read preserves frames, 100 ns timestamps, interface ids, link types, comments; µs/ns and
  both endiannesses read; unknown block skipped; truncated last block reported with earlier frames returned; optional
  `capinfos`/`tshark` validation when on PATH.

### PT1.3 Frame decoder + protocol tree — M
- **Description:** `FrameDecoder.Summarize` → `FrameSummary` (PacketDotNet; Ethernet/Null/Loop/Raw/LinuxSll/LinuxSll2; IPv4/IPv6/
  TCP/UDP/ICMP; malformed → `Hints.Malformed`, no throw). `ProtocolTreeBuilder.Build` → `ProtocolNode` tree with byte ranges
  (Ethernet, IP, TCP incl. options, UDP, ICMP, DNS summary, TLS record header + handshake type + ClientHello SNI, HTTP first line +
  complete headers in segment). `InfoFormatter` (Wireshark-like Info, plus `Inform (1 BOOT)` when the segment holds a complete
  small CWMP body — otherwise just `POST /acs`).
- **Files:** `src/AcsTool.PacketCapture/Decode/*.cs`, `Model/*.cs` (new), `tests/AcsTool.Testing/PacketBuilder.cs` (new), tests.
- **Deps:** PT0.2.
- **Acceptance:** Each link type × IPv4/IPv6 × TCP/UDP/ICMP summarised correctly; tree ranges nest without overlap; 10,000 random
  byte arrays never throw; relative seq numbers match Wireshark on the Windows fixture.

### PT1.4 TCP stream tracker — S/M
- **Description:** Stream ids (new id on SYN after FIN/RST on the same tuple), client/server roles, per-direction byte counts,
  state, hints: retransmission, out-of-order, zero window, RST, "SYN unanswered ≥ 3 s", "data after FIN". **(r2)** also records
  **request-start segments** (client→server payload starting with an HTTP method token) per conversation, used by PT1.8's
  exchange segmentation.
- **Files:** `src/AcsTool.PacketCapture/Streams/TcpStreamTracker.cs` (new), tests.
- **Deps:** PT1.3.
- **Acceptance:** Scripted scenarios (handshake/data/FIN; RST mid-stream; retransmission — not counted as a new request start;
  SYN unanswered; port reuse; seq wrap; keep-alive with 3 requests) give expected ids, states, hints and request starts.

### PT1.5 PacketStore (linked-first retention) + capture settings — M (was M; +½ d for retention)
- **Description:** `PacketCaptureSettings` (design §7, incl. r2 `CaptureWithProxy`, `LinkedShare`) as optional
  `AppSettings.PacketCapture` (null → defaults in `Normalize()`, `SchemaVersion` unchanged; `AcsToolJsonContext` updated).
  `PacketStore`: append-only, frame numbers, range reads, **two FIFO segments (linked / other)** with the D8 eviction order and
  `LinkedShare` cap, "stop when full" option, stats. Frames move to the linked segment when PT1.8 links their conversation
  (by id list, not copying data).
- **Files:** `src/AcsTool.Core/Settings/AppSettings.cs`, `src/AcsTool.Core/Settings/Persistence.cs`,
  `src/AcsTool.PacketCapture/Pipeline/PacketStore.cs` (new), tests in Core.Tests and PacketCapture.Tests.
- **Deps:** PT0.2, D8.
- **Acceptance:** Budget never exceeded; unlinked frames evicted before linked ones; linked frames of a session evicted from the
  store go next; linked share never above `LinkedShare`; concurrent readers never see torn frames (1 writer / 4 readers, 1e6 frames);
  old `settings.json` without `packetCapture` loads with defaults.

### PT1.6 Ingest pipeline: PacketCaptureService — M
- **Description:** As r1: `StartAsync/StopAsync/OpenFileAsync/Clear`, `State`, `Stats`, bounded DropWrite channel, one
  `LongRunning` ingest loop, gap markers, counters every 1 s, per-device failure isolation, logs 7000–7099. **(r2)** single
  shared instance driven by both the Proxy toggle (PT1.10) and the Packet Trace tab (PT1.13); `StartForProxyAsync(ProxySettings)`
  convenience that runs `DeviceSelector.Auto` + `CaptureFilterBuilder.Build`.
- **Files:** `src/AcsTool.PacketCapture/Pipeline/PacketCaptureService.cs`, `PacketCaptureStats.cs` (new), tests with `FakeCaptureBackend`.
- **Deps:** PT1.1, PT1.3, PT1.4, PT1.5.
- **Acceptance:** 100k frames flood: `appDropped + stored == pushed`, per-device order kept; stop ≤ 2 s; start/stop/start;
  callback path allocates only the frame copy (`Category=Perf`).

### PT1.7 BPF capture-filter builder — S
- **Description:** `CaptureFilterBuilder.Build(...)` per design §8.1; `ForSession(SessionSnapshot)`; **(r2)** regenerated on
  every proxy start unless `CaptureFilterOverride` is set. **(r3)** Takes a list of listen ports (today: `ProxySettings.Port` only);
  M5's HTTPS port is added there when M5 lands (see "M5 rebase notes").
- **Files:** `src/AcsTool.PacketCapture/Filters/CaptureFilterBuilder.cs` (new), tests.
- **Deps:** PT0.3, D9.
- **Acceptance:** Table tests as r1 (default string exact; multi-IP host; IPv6; default ports; DNS/ICMP off; session filter); a
  proxy port change produces a new filter on the next `StartForProxyAsync`.

### PT1.8 Correlation + `IPacketLookup` (conversation and exchange scope) — L (was M)
- **Description:** `EndpointIndex` + `SessionCorrelator` (design §6.3 items 1, 2, 4, 5; polls `GetAll()` ≤ 1/s when `Stats`
  changed; never reads `Changes`). **(r2)** plus design §6.4: per-exchange frame sets by **request-order segmentation** using
  PT1.4's request starts and `ExchangeEndpoints.DownstreamConnectionId`/`UpstreamConnectionSerial`; connection-setup/teardown
  frames attached and marked shared; time-window fallback; a `Basis` on every result. Public `IPacketLookup`
  (`ForExchange`, `ForSession`, `UnlinkedCwmpConversations`, `State`, coalesced `LinksChanged` ≤ 4/s). Notifies PT1.5 of links.
- **Files:** `src/AcsTool.PacketCapture/Correlation/*.cs` (new, incl. `IPacketLookup.cs`, `ExchangeSegmenter.cs`), tests.
- **Deps:** PT0.4, PT1.4, PT1.5.
- **Acceptance:** Proxy + FakeAcs integration with frames synthesised from the recorded `ExchangeEndpoints` (Linux, no native lib):
  - `ForExchange(s, 1)` returns the Inform's frames on both legs (`Basis = Exact`); a 4-exchange keep-alive session splits into the
    right 4 slots per leg; the SYN/FIN are attached to #1/#4 and flagged shared.
  - 401 + retry on a new connection: each exchange gets frames from its own connection.
  - Upstream connect refused (RST to SYN): the exchange's upstream leg shows SYN + RST; downstream shows request + 502.
  - TLS leg (synthetic TLS records): `Basis = ByTime`.
  - Unlinked connection to 7547 (SYN only) appears in `UnlinkedCwmpConversations`.
  - Port reuse, store eviction and two-session (sim + proxy) links as r1; `SessionFeedPump` still receives every change.

### PT1.9 Shared packet pane: view models + WPF controls — L
- **Description:** UI-free `PacketPaneViewModel` (any frame set → rows; selection; tree/hex built off-UI and cached),
  `PacketRowViewModel` (No., Δt, Leg, Dir, Len, Info, problem/greyed flags), `ProtocolTreeViewModel`, `HexDumpViewModel`
  (highlight ↔ node). WPF: `Views/PacketPaneView.xaml` (list + tree + hex with splitters; compact and full layouts via a
  `Compact` property) and `Views/HexDumpView.xaml` (visible-lines-only rendering). Used by PT1.11 and PT1.13.
- **Files:** `src/AcsTool.Presentation/PacketTrace/Shared/*.cs` (new), `src/AcsTool.App/Views/PacketPaneView.xaml(.cs)`,
  `HexDumpView.xaml(.cs)` (new), `src/AcsTool.App/Themes/Light.xaml` (row colours), `tests/AcsTool.Presentation.Tests/PacketPaneTests.cs` (new).
- **Deps:** PT1.3.
- **Acceptance:** Node selection sets the hex highlight and vice versa; tree/hex never built on the dispatcher thread
  (`SynchronousUiDispatcher` + a threading probe); 10k-row set binds without per-row dispatches; App builds on Linux.

### PT1.10 Proxy toolbar: Capture packets toggle + status chip — M (new)
- **Description:** `ProxyCaptureViewModel` (exposed as `ProxyViewModel.Capture`): persisted toggle `CaptureWithProxy` (D15), starts
  capture in `ProxyViewModel.StartAsync` after the proxy is running and stops it in `StopAsync`; toggling while running starts/stops
  immediately; Npcap probe on first need with banners (`npcap-missing` / `npcap-access` / `npcap-error`, once); status chip text and
  states (design §13.1a); `⚠ N connections without a session` from `IPacketLookup.UnlinkedCwmpConversations()` (refresh 1/s);
  chip/counter click → Packet Trace tab (until PT1.13 lands, opens the Packets tab of the newest session / no-op with tooltip).
  `MainViewModel`: status-bar segment; **`ShutdownAsync` stops capture before `Proxy.StopAsync`**. `Ctrl+E` toggle.
- **Files:** `src/AcsTool.Presentation/PacketTrace/Proxy/ProxyCaptureViewModel.cs` (new), `src/AcsTool.Presentation/Proxy/ProxyViewModel.cs`,
  `src/AcsTool.Presentation/Shell/MainViewModel.cs`, `src/AcsTool.Presentation/ServiceCollectionExtensions.cs`,
  `src/AcsTool.App/Views/ProxyView.xaml`, `src/AcsTool.App/Views/MainWindow.xaml` (key binding), tests in `ViewModelTests.cs`/new file.
- **Deps:** PT1.6, PT1.7, PT1.8.
- **Acceptance:** With fake backend: proxy start → capture starts with the auto devices and generated filter; stop → stops;
  toggle off → proxy unaffected; `NotInstalled` → proxy starts normally, chip amber, one banner; capture open failure never fails the
  proxy start; shutdown order test; existing `ProxyViewModel` tests unchanged and green.

### PT1.11 Session detail "Packets" tab — M/L (new; the MVP's centrepiece)
- **Description:** `ExchangePacketsViewModel` (property `SessionDetailViewModel.Packets`, created via `IPacketLookup`; inactive unless
  the tab is selected): scope *This exchange* (follows `SelectedExchange`) / *Whole session*; leg checkboxes (CPE↔Proxy, Proxy↔ACS,
  DNS/ICMP); `Basis` line; Δt timeline; hosts a compact `PacketPaneViewModel`; live refresh on `LinksChanged` for its session
  (≤ 4/s, selection + scroll preserved); empty states (capture off / Npcap missing / not captured / evicted / filtered); tab header
  badge (`Packets ●7`, `–`, `⚠`); actions Export this exchange/session (PT1.12), Open in Wireshark, Show in Packet Trace.
  XAML: 4th `TabItem` "Packets" in `SessionDetailView.xaml`'s inner `TabControl`, content bound via
  `RelativeSource AncestorType=UserControl` to `DataContext.Packets` (TabControl's own DataContext and the other tabs unchanged).
  `Ctrl+4` selects it. Works for Simulator sessions too (same view).
- **Files:** `src/AcsTool.Presentation/PacketTrace/Proxy/ExchangePacketsViewModel.cs` (new),
  `src/AcsTool.Presentation/Detail/SessionDetailViewModel.cs` (ctor gains optional `IPacketLookup?`; null in existing tests),
  `src/AcsTool.Presentation/ServiceCollectionExtensions.cs` (`NewFeed`), `src/AcsTool.App/Views/SessionDetailView.xaml`,
  `tests/AcsTool.Presentation.Tests/DetailTests.cs`, `XamlLayoutTests.cs` (new test: tab headers are exactly Summary, Headers, Raw, Packets).
- **Deps:** PT1.8, PT1.9, PT1.10.
- **Acceptance:**
  - Selecting exchange #1 (Inform) of a proxied session shows its frames on both legs in time order with correct `Leg`/`Dir`;
    switching to #2 changes the set; *Whole session* shows all with a `#` column.
  - In-progress session: frames appear live as the fake backend emits them (≤ 4 UI updates/s); no update when the tab is not selected.
  - Each empty state renders its text; `Basis` shown on every non-empty view.
  - `SessionDetailViewModel` without `IPacketLookup` behaves exactly as today (all existing Detail tests unchanged).
  - Windows: W-PT-04, W-PT-04b, W-PT-08.

### PT1.12 Export (PCAPNG/PCAP) + credential masking + Open in Wireshark — M
- **Description:** As r1, with **scope `Exchange`** added and the Packets tab as the first caller: `PacketExportService.ExportAsync(scope, …)`
  (exchange / session / selected / displayed / stream / all), PCAPNG comments, PCAP split per link type, atomic write,
  `CredentialMasker` (same-length, checksum recomputed, MVP segment-span handling), `PacketExportViewModel` dialog,
  `IExternalToolLauncher` + `WindowsExternalToolLauncher`, default path `DefaultExportDir\packets\acstool-<ts>-<scope>[-<device-key>].pcapng`.
- **Files:** `src/AcsTool.PacketCapture/Export/*.cs` (new), `src/AcsTool.Presentation/PacketTrace/Shared/PacketExportViewModel.cs` (new),
  `src/AcsTool.Presentation/Abstractions/Abstractions.cs`, `src/AcsTool.App/Views/PacketExportDialog.xaml` (new),
  `src/AcsTool.App/Services/ExternalToolLauncher.cs` (new), tests.
- **Deps:** PT1.2 (writer), PT1.8, PT1.11, D6, D7.
- **Acceptance:** As r1 (masking Basic/Digest incl. split header, lengths and checksums valid, comments present, PCAP split,
  cancellation leaves no file) + exchange-scope export contains exactly `ForExchange` frames.

### PT1.13 Packet Trace tab (capture console) — M (was L; reduced by reusing PT1.9) · off the critical path
- **Description:** `PacketTraceViewModel` (live/file source, device picker, filter editor with regenerate/validate/pin override,
  start/stop/clear/open, quick toggles CWMP/Linked/Problems/**No session**, session picker, footer stats), `DeviceSelectionViewModel`,
  `PacketFeedPump` (100/250 ms batches, one dispatch per batch), `Views/PacketTraceView.xaml` (third main `TabItem` in the
  standard `ScrollViewer` pattern, full-layout `PacketPaneView`), context menu incl. *Open session in Proxy tab*. Wires the
  Proxy chip/counter click (PT1.10) and the Packets tab's *Show in Packet Trace* (PT1.11).
- **Files:** `src/AcsTool.Presentation/PacketTrace/Console/*.cs` (new), `src/AcsTool.Presentation/Shell/MainViewModel.cs`,
  `src/AcsTool.App/Views/PacketTraceView.xaml(.cs)` (new), `src/AcsTool.App/Views/MainWindow.xaml`, `XamlLayoutTests.cs` (theories pick up the new tab), tests.
- **Deps:** PT1.2 (reader), PT1.6, PT1.9.
- **Acceptance:** 50k frames in 2 s → ≤ 25 dispatches; file open shows frames without native lib; pinned filter shows as `custom`
  on the Proxy chip; "No session" toggle lists exactly `UnlinkedCwmpConversations`; Windows W-PT-05/06/07/11.

### PT1.14 Docs — S
- **Description:** User guide: **`proxy-mode.md` gains "Packets for a session"** (primary story, toggle, chip, Packets tab, basis
  wording, empty states); new `packet-trace.md` (console tab, Npcap install, devices incl. loopback, filters, export/masking,
  Wireshark, offline files); `settings-reference.md` (`packetCapture.*`), `troubleshooting.md` (Npcap states, BFE, offload,
  drops, "no packets for this session"), `ui-tour.md`, `roadmap.md`, `architecture.md`, `IMPLEMENTATION_NOTES.md`.
- **Deps:** PT1.1–PT1.13.
- **Acceptance:** ReviewerBot doc review; every `PacketCaptureSettings` member documented with its default.

### PT1 acceptance (milestone)
- **PT1-A1** Whether or not libpcap/Npcap is present on CI (Linux runner has libpcap, Windows runner has no Npcap) → all automated tests green on Linux and Windows runners with `CI=true`; `--smoke-exit` passes.
- **PT1-A2 (primary story)** With **Capture packets** on, a real CPE's Inform through the proxy shows, in the Proxy tab's session
  detail **Packets** tab, the frames of that exchange on both legs (CPE↔Proxy and Proxy↔ACS) with protocol tree and hex, and
  the match basis — without leaving the Proxy tab.
- **PT1-A3** *Whole session* scope shows all exchanges' frames; live sessions fill in while in progress.
- **PT1-A4** Proxy toggle starts/stops capture with the proxy using auto devices (LAN + Loopback) and the generated filter; capture
  problems never prevent the proxy from starting; Npcap missing → clear chip + one banner.
- **PT1-A5** `⚠ N connections without a session` shows connections to the listen port that never became a session.
- **PT1-A6** Summary / Headers / Raw tabs, the session feed and the Simulator tab behave exactly as before (existing tests unchanged; no
  diffs in `SessionFeedView.xaml` or `SimulatorView.xaml`; `SessionDetailView.xaml` only gains the 4th tab; `ProxyView.xaml` only the toggle/chip/counter).
- **PT1-A7** Export exchange/session/selection as PCAPNG (default) or PCAP into `Documents\AcsTool\exports\packets`; opens in Wireshark
  with `session=…` comments; credentials masked by default.
- **PT1-A8** Budgets respected with linked-first retention (packets of sessions still in the feed outlive unrelated traffic); drops
  visible; UI responsive at 5,000 pps synthetic.
- **PT1-A9** Packet Trace tab: device/filter control, unconstrained live list, "No session" view, offline `.pcap`/`.pcapng` open
  *(may ship as PT1b — see above; A2–A8 do not depend on it)*.
- **PT1-A10** Closing the app while capturing stops capture first, no orphaned threads; 70xx log lines; no secrets in logs.

---

## PT2 — Streams and HTTP/CWMP rebuild

| ID | Title | Description | Files | Deps | Size | Acceptance |
|---|---|---|---|---|---|---|
| PT2.1 | `TcpReassembler` | Per stream/direction byte stream: seq ordering with wrap, retransmission de-dup, overlap first-wins, gap markers (loss/snaplen/eviction), `ReassemblyBudgetBytes` (release closed streams first; over-budget stream keeps head + tail 64 KiB). Offset → frame map. | `PacketCapture/Streams/TcpReassembler.cs` (new) | PT1.4 | L | Out-of-order, duplicate, overlapping, missing segment, wrap, and SYN-less mid-stream start scenarios produce exact byte streams + gap markers; budget never exceeded. |
| PT2.2 | `HttpWireExtractor` | HTTP/1.1 framing on reassembled streams: request/status line, headers, Content-Length, chunked (de-chunk), keep-alive, pipelined, `100 Continue`, read-to-close, 204/304/1xx, HEAD. Bounded bodies (`Capture.MaxCapturedBodyBytes`). Problems list (truncated, gap inside body, bad chunk). | `PacketCapture/Streams/HttpWireExtractor.cs`, `Model/WireHttpMessage.cs` (new) | PT2.1 | M | Fixture streams incl. CWMP session (Inform → 200 InformResponse → empty POST → 204), chunked ACS response, 401 + retry on same connection, `Connection: close` → correct messages with frame ranges. |
| PT2.3 | CWMP from wire | Bodies of `WireHttpMessage` go through existing `ICwmpParser` + `IMessageSummarizer`; tree "HTTP" + "CWMP" nodes with the same fields as the Summary tab; Info column shows `Inform (BOOT)` etc. | `PacketCapture/Decode/ProtocolTreeBuilder.cs`, DI | PT2.2 | S | All `tests/fixtures/*.xml` wrapped into synthetic HTTP/TCP give the same `MessageSummary` as the app-layer parser (equality test). |
| PT2.4 | Follow TCP Stream | `StreamFollowViewModel` + `StreamFollowWindow.xaml`: interleaved directions (colours), ASCII/hex/HTTP-messages modes, gap markers, save raw, export stream PCAPNG. | Presentation `PacketTrace/StreamFollowViewModel.cs`, App `Views/StreamFollowWindow.xaml` (new) | PT2.2 | M | VM tests for each mode; Windows W-PT-15. |
| PT2.5 | Exact exchange-level correlation + wire-vs-app diff | Replaces PT1.8's request-order segmentation with exact HTTP-message matching (Packets tab basis becomes `exact (HTTP match)`). Design §6.3 item 3 and §10.2 "Wire vs app diff": n-th message ↔ exchange by connection id + method/path/length + body prefix; diff findings (never left the PC, RST before response, response on wire but app timeout, rewritten headers labelled). Shown in the AcsTool tree node and as a "Problems" filter. | `PacketCapture/Correlation/*`, Presentation tree VM | PT2.2, PT1.8 | M | Integration test (proxy + FakeAcs + synthesized frames): each exchange matched to its wire messages; injected faults (drop ACS-leg SYN-ACK; RST after request; extra header) produce the expected findings. |
| PT2.7 | Ladder / flow view | Packets tab mode "Flow": CPE │ Proxy │ ACS columns with arrows per frame (SYN, POST Inform, RST…), Δt on the left — the classic sequence diagram for one exchange or session. | Presentation `PacketTrace/Proxy/FlowViewModel.cs`, App `Views/PacketFlowView.xaml` (new) | PT1.11 | M | VM tests produce the expected arrow rows for the fixture session; Windows W-PT-17. |
| PT2.6 | Stream-aware masking | `CredentialMasker` uses reassembler offsets (header anywhere in the stream, incl. across 3+ segments). | `PacketCapture/Export/CredentialMasker.cs` | PT2.1, PT1.11 | S | Header split at every possible byte boundary across 2–3 segments is masked; lengths/checksums valid. |

**PT2 acceptance:** PT2-A1 reassembly correct on fixtures · A2 HTTP messages extracted with frame ranges · A3 CWMP summary from
wire equals app summary · A4 Follow Stream window works on live and offline data · A5 per-exchange links shown · A6 diff findings
for the three injected-fault scenarios · A7 reassembly budget respected under a 1 GiB synthetic stream · A8 flow view in the Packets tab.

## PT3 — Offline analysis and linking polish

| ID | Title | Description | Files | Deps | Size | Acceptance |
|---|---|---|---|---|---|---|
| PT3.1 | Display filter | Managed subset (design §8.2) with parser, error position, history; replaces/extends quick toggles in the Packet Trace tab. | `PacketCapture/Filters/DisplayFilter*.cs`, VM | PT1.13 | M | Grammar tests; filter of 200k frames applied off-UI in < 1 s. |
| PT3.2 | Wire sessions for files | Group rebuilt CWMP messages into display-only sessions (CPE IP + cookie + Inform boundaries); list in a side panel; export per wire session (D12). | `PacketCapture/Correlation/WireSessionBuilder.cs` (new), VM, view | PT2.3 | M | Fixture capture with two CPEs and three sessions → three wire sessions with right exchanges; nothing written to `InMemorySessionStore`. |
| PT3.3 | Feed context-menu shortcuts | (r2: the main cross-link — the Packets tab — is now MVP.) Session feed right-click: *Show packets* (selects the session + Packets tab), *Export packets (.pcapng)*, *Show in Packet Trace*. | `SessionFeedView.xaml`, `SessionFeedViewModel` | PT1.11, PT1.13 | S | VM tests for the three commands; XAML tests still green. |
| PT3.4 | TLS handshake details (+ M5 link when M5 lands) | TLS ClientHello/ServerHello/Alert/Certificate (subject/issuer/thumbprint SHA-256 when unencrypted ≤ TLS 1.2) in the tree; match alerts to `SessionSnapshot.Tls`/`UpstreamError(Kind=Tls)`. | `PacketCapture/Decode/TlsDissector.cs` (new) | PT1.3 (upstream HTTPS parts work on today's `main`; inbound-listener parts need M5, deferred) | M | Fixtures (TLS 1.2 and 1.3 handshakes, alert 40/42/48) dissected correctly; thumbprint equals `TlsInfo` thumbprint in an HTTPS integration test (Linux, synthetic capture from test certs). |
| PT3.5 | Capture spooling | Optional ring of PCAPNG files on disk (`%LOCALAPPDATA%\AcsTool\pcap-spool`, N × M MiB) so long captures survive in-memory eviction; "export" can read from spool. | `PacketCapture/Pipeline/SpoolWriter.cs` (new), settings | PT1.6, PT1.2 | M | 1 h synthetic capture keeps memory within budget and spool within N×M; export of an evicted range reads from spool. |
| PT3.6 | Pooled frame buffers (perf) | Replace exact-size arrays with slab/pool if PT1 perf numbers show GC pressure. | `PacketStore`, ingest | PT1 perf data | S–M | Gen0 GCs/s reduced ≥ 50 % at 5,000 pps with no correctness regressions. |

**PT3 acceptance:** A1 display filter · A2 wire sessions for offline files · A3 feed shortcuts · A4 TLS details · A5 spooling ·
A6 (if done) perf improvement measured.

## PT4 — Later / maybe (not scheduled)

- Linux-only TLS key logging (`System.Net.EnableSslKeyLogging` + `SSLKEYLOGFILE`) with a "decrypt" option (not possible on Windows/SChannel).
- Windows `pktmon`/ETW backend as a no-Npcap fallback (`ICaptureBackend` makes this pluggable).
- Statistics: conversation graphs, RTT/throughput, I/O graph.
- IP fragment reassembly.

---

## ReviewerBot checklist hints

1. **Capture callback** (`SharpPcapBackend` `OnPacketArrival` handler): copy + `TryWrite` only; no logging, decoding, locks, LINQ, `GetPacket()` object graphs, or exceptions escaping.
2. **Threads:** ingest loop is `LongRunning`; nothing blocks the thread pool; no `.Result`/`.Wait()`; `ConfigureAwait(false)` in library code; stop paths join with a timeout and log 70xx.
3. **No second reader** of `ISessionStore.Changes`; correlator uses `GetAll()` at most once per second and only when `Stats` changed.
4. **No `ISessionSink` writes** from PacketCapture; no new `HttpMessageCapture` construction from wire data (wire uses `WireHttpMessage`).
5. **Core changes are additive:** `ISessionSink.SetExchangeEndpoints` has a default body; existing fakes untouched; `ExchangeRecord` positional members unchanged; settings schema version unchanged; old settings load.
6. **Budgets:** every in-memory collection in PacketCapture is bounded (frames, bytes, reassembly, queue, row VMs, tree cache); counters exposed.
7. **Native failures:** `DllNotFoundException`, `PcapException`, `UnauthorizedAccessException` mapped to user-facing banners; app start and `--smoke-exit` never load the native lib.
8. **Security/privacy:** export masks credentials by default; the include-credentials choice is not persisted; no packet payloads or Authorization values in logs; default BPF is narrow; promiscuous off by default.
9. **Presentation stays UI-free** (no `System.Windows` in `AcsTool.Presentation`); App code-behind minimal; new `TabItem` follows the `ScrollViewer` pattern.
10. **Analyzer clean** with `CI=true` on both runners; no new suppressions without a comment.
11. **Licensing:** PacketDotNet unmodified, `THIRD-PARTY-NOTICES.md` present; Npcap not redistributed.
12. **Docs:** every new setting documented; troubleshooting covers Npcap states.
13. **(r2) Proxy integration is additive and fail-safe:** a capture failure (Npcap missing, access denied, bad filter, device gone) never fails or delays `ProxyViewModel.StartAsync`; the proxy start/stop path awaits capture start with a timeout (2 s) and logs, never throws to the user.
14. **(r2) Detail view untouched:** `SessionDetailView.xaml`'s Summary/Headers/Raw `TabItem`s and the TabControl's `DataContext` unchanged; the Packets tab binds through `RelativeSource`; `SessionDetailViewModel` without `IPacketLookup` behaves exactly as before.
15. **(r2) Packets tab cost:** no work while the tab is not selected; ≤ 4 UI updates/s; frame sets computed off the UI thread; selection/scroll preserved on refresh.
16. **(r2) Honest empty states:** every Packets view shows its match basis or the reason it is empty (capture off, not installed, not captured, evicted, filtered) — no blank pane.
17. **(r2) Retention:** linked-first eviction is O(1) per frame (segment FIFOs, no scans) and respects `LinkedShare`.

## TesterBot test plan

### Automated — runs on Linux CI (has libpcap) and Windows CI (no Npcap); none needs the native library

| Area | Tests |
|---|---|
| File formats | PCAP µs/ns, both endiannesses; PCAPNG multi-section/multi-IDB, `if_tsresol` variants, comments, unknown blocks, truncation; writer round-trip; optional `capinfos` validation when present. |
| Decoding | Each link type (Ethernet, Null, Loop, Raw, SLL, SLL2) × IPv4/IPv6 × TCP/UDP/ICMP; malformed/fuzzed input (10k random frames, no throw); tree byte ranges. |
| Streams | Stream ids, roles, states, hints; reassembly scenarios (PT2); HTTP extraction (PT2); CWMP summary equality with app parser (PT2). |
| Pipeline | Fake backend floods (100k frames), DropWrite accounting, stop/start, per-device failure isolation, budget/eviction stress with concurrent readers. |
| Correlation | Proxy + FakeAcs integration with synthesized frames from real `ExchangeEndpoints` (both legs, reconnect mid-session, port reuse, eviction); simulator → proxy on loopback double link. |
| Filters | BPF builder tables; display filter grammar (PT3). |
| Export | Scopes, PCAPNG comments, PCAP split, masking (Basic, Digest, split headers), checksum validity, atomicity/cancellation. |
| Presentation | Pump batching (≤ 10 dispatches/s), selection → tree/hex off-UI, banners per backend status, shutdown order, XAML layout rules for the new tab. **(r2)** Proxy toggle starts/stops capture with the proxy (fake backend); capture failure never fails proxy start; Packets tab: exchange/session scope sets, live refresh ≤ 4/s only while selected, empty-state texts, `Basis` text; detail tab headers exactly Summary/Headers/Raw/Packets; existing Detail/ViewModel tests unchanged. |
| Exchange segmentation (r2) | Keep-alive multi-exchange split, 401 + new connection, refused upstream, TLS → by-time, shared SYN/FIN, unlinked connections — all from the proxy+FakeAcs integration with synthesised frames. |
| Regression | All existing suites unchanged and green (store, feed pump FLAKY-01/02 tests, proxy, simulator). |

Opt-in live tests (`[SkippableFact]`, `ACSTOOL_PCAP_LIVE=1`, Linux with libpcap and `CAP_NET_RAW`, or Windows with Npcap): loopback
capture of a local connect; BPF validation against a real device; counters readable. **Not** enabled in CI by default (D2);
TesterBot runs them locally and records results in `docs/TEST_REPORT_*.md`.

Perf (`Category=Perf`, excluded by `build.sh` unless `--include-perf`): 5,000 pps synthetic for 60 s through fake backend →
app drops < 0.1 %, dispatches ≤ 10/s, allocation per frame ≈ frame size; proxy added latency with capture on vs off (FakeAcs loop) p95 < 5 ms.

### Manual — Windows only (needs Npcap, real NICs, Wireshark)

| ID | Check |
|---|---|
| W-PT-01 | Npcap **not installed**: tab shows "not installed" state + link; Start disabled; Open file works; no error at app start; `--smoke-exit` OK. |
| W-PT-02 | Npcap installed with defaults, standard user: devices listed with friendly names/IPs; Loopback present. |
| W-PT-03 | Npcap "admin only": Start → UAC prompt; accept → capture works; decline → AccessDenied banner text. |
| W-PT-04 | **(r2 primary story)** Proxy tab, *Capture packets* on, real modem → proxy (LAN NIC) → ACS on `localhost:7557`: the Inform appears in the feed; selecting it and the **Packets** tab shows the exchange's frames on both legs (LAN + Loopback) with basis `exact`, tree and hex — without leaving the Proxy tab. |
| W-PT-04b | Same, *Whole session* scope on a session with GPV/SPV: all exchanges' frames with `#`; an in-progress session fills in live; switching exchanges is instant. |
| W-PT-04c | Proxy toggle: start/stop proxy starts/stops capture; Npcap uninstalled → proxy still starts, chip amber, one banner; toggle off → no capture, Packets tab shows "capture is off". |
| W-PT-04d | Unplug/firewall the CPE path so a SYN reaches the PC but Kestrel never gets a request (or send a RST): `⚠ connections without a session` counter increments and opens the list. |
| W-PT-05 | Three panes: tree ↔ hex highlight both directions; splitters; narrow window (tab scrolls, no clipped controls). |
| W-PT-06 | Default BPF matches settings; edit to invalid → libpcap error shown; regenerate restores. |
| W-PT-07 | "Limit to session" restarts capture with the session filter; only that CPE's frames appear. |
| W-PT-08 | Kill the ACS mid-session (or block 7557 with the firewall): RST/SYN-unanswered frames visible and linked; matches the app-layer `UpstreamError`. |
| W-PT-09 | Export session as PCAPNG → opens in Wireshark via "Export & open"; packet comments `session=…` visible; frames identical to AcsTool's list. |
| W-PT-10 | Export with Basic and Digest auth sessions: credentials masked in Wireshark's HTTP view; "include credentials" export shows them; Wireshark reports no TCP length errors. |
| W-PT-11 | Open a Wireshark-saved `.pcapng` (multi-interface) and a `.pcap` offline; correct decoding and times. |
| W-PT-12 | Busy NIC with **empty** filter for 5 min: warning shown; drop counters move but UI stays responsive; memory ≤ budget + ~40 MiB; proxy sessions still complete. |
| W-PT-13 | Simulator → proxy on the same PC: loopback conversation linked to both the simulator and the proxy session. |
| W-PT-14 | HTTPS upstream (M5 HTTPS listener only after M5 lands — deferred): TLS records shown, no HTTP; certificate error case shows the alert frame and links to the session with `UpstreamError Tls`. |
| W-PT-15 | (PT2) Follow TCP Stream on a CWMP session: interleaved Inform/InformResponse/empty/204; HTTP-messages mode; save raw. |
| W-PT-17 | (PT2) Flow view of an Inform exchange reads like a sequence diagram (CPE │ Proxy │ ACS). |
| W-PT-16 | App close while capturing: closes within the normal shutdown time; no orphaned Npcap handles (re-open capture in Wireshark works). |

---

## M5 rebase notes (r3: M5 deferred, PT first)

When M5 (TLS, `docs/M5_TASKS.md`) resumes it rebases onto PT. Expected touch points:
- `PhaseTracker` / `ConnectCallbackFactory` (PT0.3 adds `LocalEndPoint`, `ConnectionSerial`; M5 resets TLS error state per connection).
- `ProxyView.xaml` / `ProxyViewModel` (PT1.10 adds the capture toggle/chip; M5 adds HTTPS listener and TLS settings).
- `SessionDetailView.xaml` (PT1.11 adds the Packets tab; M5 adds the TLS chip/pop-up in the header — different regions).
- `CaptureFilterBuilder` listen ports: add the HTTPS listener port (planned 7443) → one-line change + a table test.
- `docs/user-guide/roadmap.md`: M5 is no longer "planned next" — update when PT1 merges.

## Decisions (copy of design §14, r3 — locked)

**All approved and locked 2026-11-05** ("Its approved skip the M5 and build proxy-tab packet plan.").
**D1 overridden:** packet trace now, M5 deferred (no PT task waits for M5).
Changed in r2 and approved as recommended: **D8** (linked-first retention),
**D9** (filter auto-regenerated on proxy start), **D11** (Packets tab + Proxy toggle in the MVP instead of PT3 links).
New: **D15** (capture starts with the proxy via a persisted toggle, default on when Npcap is present), **D16** (MVP exchange
matching by request order + time fallback, exact in PT2).
Unchanged: D2 no conditional compilation · D3 Npcap not bundled · D4 `asInvoker` · D5 managed file I/O · D6 PCAPNG default ·
D7 masking on · D10 auto devices incl. Loopback (now the normal path) · D12 wire sessions display-only · D13 additive Core endpoints ·
D14 no TLS decryption on Windows.
