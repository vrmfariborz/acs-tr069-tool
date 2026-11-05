# Packet Trace — task breakdown (PT0–PT4)

| Field | Value |
|---|---|
| Document | `docs/PACKET_TRACE_TASKS.md` (design: [`PACKET_TRACE_DESIGN.md`](PACKET_TRACE_DESIGN.md)) |
| Author | PlannerBot (for CoderBot → ReviewerBot / TesterBot) |
| Date | 2026-11-05 |
| Baseline | `main` @ `479639b` |
| Target framework | net10.0 (App net10.0-windows) |
| Precondition | Fariborz approves decisions D1–D14 (design §14). Recommended: PT0 any time; PT1 starts after M5 (TLS) is merged (D1). |

Sizes: **S** ≤ ½ day · **M** 1–2 days · **L** 3–5 days (one developer). Task IDs use the prefix `PT` so they cannot collide with
PLAN `Tn.x` or `M5-` IDs. "Files" lists the main files; **(new)** marks new files. Event ids for logging: **7000–7099**.

Branch convention: one branch per milestone (`feat/packet-trace-pt1`, …), merged after ReviewerBot + TesterBot like earlier milestones.
CI must stay green on Linux and Windows with **no** libpcap/Npcap installed.

---

## Milestone overview

| Milestone | Goal | Exit criteria | Estimate |
|---|---|---|---|
| **PT0** Spike & groundwork | Prove packages + native loading on Windows; project skeleton; Core endpoint plumbing | Skeleton builds on both CIs with `CI=true`; Windows spike notes recorded; `ExchangeEndpoints` populated by proxy and simulator | 2–3 d |
| **PT1** MVP | Live multi-device capture + offline open + three-pane UI + tuple correlation + PCAPNG export with masking + Wireshark launch | Acceptance list PT1-A1…A12 below; Windows checklist W-PT-01…14 passed | 13–17 d |
| **PT2** Streams & HTTP/CWMP | TCP reassembly, HTTP/1.1 extraction, CWMP summary from wire, Follow TCP Stream, exchange-level correlation, wire-vs-app diff | PT2-A1…A7 | 9–12 d |
| **PT3** Offline & linking polish | Display filter, wire sessions for files, "Show packets" cross-links, TLS handshake details, capture spooling | PT3-A1…A6 | 7–9 d |
| **PT4** Later / maybe | Linux key log, ETW/pktmon backend (no Npcap), stats graphs | Only on request | — |

**MVP cut = PT0 + PT1.**

---

## PT0 — Spike & groundwork

### PT0.1 Windows native spike (throw-away, not merged) — S
- **Description:** Console app on a Windows PC with Npcap: list devices (friendly names, addresses, loopback flag), capture 30 s on
  the LAN NIC and `NPF_Loopback` with `tcp port 7547 or tcp port 7557`, print link types, timestamp resolution support, kernel stats.
  Repeat with Npcap installed "admin only" (record the UAC behaviour) and with Npcap uninstalled (record the exception type/message).
- **Files:** none in repo; results appended to `docs/IMPLEMENTATION_NOTES.md` § "Packet trace spike" by CoderBot (or TesterBot).
- **Deps:** —
- **Acceptance:** Notes state: SharpPcap 6.3.1 finds `wpcap.dll` under `System32\Npcap` without extra setup; loopback link type
  (expected DLT_NULL); exception for missing Npcap (expected `DllNotFoundException`); admin-only behaviour.

### PT0.2 Project skeleton + packages — S
- **Description:** Add `src/AcsTool.PacketCapture` (net10.0, refs Core, `PackageReference SharpPcap` + `PacketDotNet`),
  `tests/AcsTool.PacketCapture.Tests`, solution entries, `InternalsVisibleTo`, central versions `SharpPcap 6.3.1`,
  `PacketDotNet 1.4.8` in `Directory.Packages.props`. `ServiceCollectionExtensions.AddAcsToolPacketCapture()` (empty for now),
  called from `AddAcsToolPresentation`. `THIRD-PARTY-NOTICES.md` (SharpPcap MIT, PacketDotNet MPL-2.0 + source URL).
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
- **Deps:** D13 approved. If M5 is in flight, rebase onto it (same files).
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

## PT1 — MVP

### PT1.1 Capture backend abstraction + SharpPcap implementation — M
- **Description:** `ICaptureBackend`, `ICaptureSession`, `RawFrame`, `CaptureDeviceInfo`, `CaptureBackendInfo/Status`,
  `CaptureOpenOptions` (snaplen, promiscuous, kernel buffer, BPF, read timeout 100 ms, prefer ns timestamps), `CaptureCounters`,
  `CaptureOpenException` (user-facing message + `CaptureBackendStatus`). `SharpPcapBackend`: `Probe()` never throws (maps
  `DllNotFoundException` → NotInstalled; access errors → AccessDenied); `ListDevices()` from `CaptureDeviceList`/`LibPcapLiveDeviceList`
  with friendly names, addresses, loopback flag; `Open()` → `LibPcapLiveDevice.Open(DeviceConfiguration)`, sets `Filter`
  (bad filter → `CaptureOpenException` with libpcap's text); `Start(onFrame)` hooks `OnPacketArrival` and copies the span.
  `FakeCaptureBackend` in `AcsTool.Testing` (scripted devices, frames, counters, failure modes).
- **Files:** `src/AcsTool.PacketCapture/Backend/*.cs` (new), `tests/AcsTool.Testing/FakeCaptureBackend.cs` (new), tests.
- **Deps:** PT0.2.
- **Acceptance:** On Linux CI without libpcap: `Probe()` returns `NotInstalled` with a message, no exception (test). Fake backend
  contract tests (start/stop idempotent, stop joins, counters). Live test `[SkippableFact]` runs only if `ACSTOOL_PCAP_LIVE=1`
  (captures a loopback TCP connect to a local listener and sees SYN/SYN-ACK/ACK).

### PT1.2 Managed PCAP/PCAPNG reader and writer — M
- **Description:** `PcapReader` (magic a1b2c3d4 µs / a1b23c4d ns, both endiannesses, snaplen, link type), `PcapngReader`
  (SHB incl. byte-order detection and multiple sections, IDB with `if_tsresol`/`if_name`/`if_description`/`if_filter`, EPB, SPB,
  legacy OPB, skip unknown blocks, `opt_comment`), `PcapngWriter` (SHB with `shb_userappl`, one IDB per interface, EPB with
  optional comment, `if_tsresol`=7), `PcapWriter` (one link type per file). Corrupt/truncated files → `CaptureFileException` with
  block offset; reading stops at the budget.
- **Files:** `src/AcsTool.PacketCapture/Files/*.cs` (new), `tests/AcsTool.PacketCapture.Tests/FileFormatTests.cs` (new),
  tiny hand-made fixtures under `tests/fixtures/pcap/` (generated by a test helper and committed, each < 4 KiB) **plus** at least
  one file produced by Wireshark/dumpcap on Windows (from PT0.1) and one by `tcpdump` on Linux (ns resolution).
- **Deps:** PT0.2.
- **Acceptance:** Round-trip write→read preserves frames, timestamps (100 ns), interface ids, link types and comments; reads µs/ns,
  big/little-endian PCAP and PCAPNG; unknown block skipped; truncated last block reported with frames before it returned;
  optional test validates written files with `capinfos`/`tshark -r` when present on PATH (skipped otherwise).

### PT1.3 Frame decoder + protocol tree — M
- **Description:** `FrameDecoder.Summarize(linkKind, data, origLen)` → `FrameSummary` (PacketDotNet `Packet.ParsePacket` with
  `LinkLayers` Ethernet/Null/Loop/Raw/LinuxSll/LinuxSll2; IPv4/IPv6/TCP/UDP/ICMP; malformed → `Hints.Malformed`, no throw).
  `ProtocolTreeBuilder.Build(frame)` → `ProtocolNode` tree with byte ranges for each layer and main fields (Ethernet addrs/type;
  IP version/len/TTL/proto/addrs/flags/frag; TCP ports/seq/ack/flags/window/checksum/options; UDP; ICMP type/code; DNS summary;
  TLS record header + handshake type + ClientHello SNI; HTTP first line + complete headers in segment). `InfoFormatter` for the
  Info column (Wireshark-like `51712 → 7547 [SYN] Seq=0 Win=64240 Len=0`, `POST /acs`, `HTTP/1.1 200 OK`).
- **Files:** `src/AcsTool.PacketCapture/Decode/*.cs`, `src/AcsTool.PacketCapture/Model/*.cs` (new), `tests/AcsTool.Testing/PacketBuilder.cs` (new), tests.
- **Deps:** PT0.2.
- **Acceptance:** For synthetic frames of each link type × IPv4/IPv6 × TCP/UDP/ICMP the summary fields are correct; tree node
  ranges cover exactly the header bytes (property test: children within parent, no overlap at the same level); 10,000 random
  byte arrays never throw; relative seq numbers per stream match Wireshark's on the Windows fixture.

### PT1.4 TCP stream tracker — S/M
- **Description:** `TcpStreamTracker` assigns `StreamId` on first frame of a new 4-tuple (new id after FIN/RST + new SYN on the
  same tuple), client/server roles (SYN sender; else lower port = server heuristic for 7547/7557/80/443), per-direction byte
  counts, state, analysis hints: retransmission, out-of-order, zero window, RST, "SYN unanswered ≥ 3 s", "data after FIN".
  Exposes `TcpConversation` snapshots and a conversation list.
- **Files:** `src/AcsTool.PacketCapture/Streams/TcpStreamTracker.cs` (new), tests.
- **Deps:** PT1.3.
- **Acceptance:** Scripted scenarios (handshake/data/FIN; RST mid-stream; retransmitted segment; SYN without answer; port reuse
  creating a second stream; 32-bit seq wrap) produce the expected ids, states and hints.

### PT1.5 PacketStore with budget + capture settings — M
- **Description:** `PacketCaptureSettings` record (design §7) as optional `AppSettings.PacketCapture` (null → defaults in
  `Normalize()`, `SchemaVersion` unchanged; JSON source-gen context updated). `PacketStore`: append-only, frame numbers, range
  reads, eviction by `MaxFrames`/`FrameBudgetBytes` (ring) or "stop when full", stats (`Frames`, `Bytes`, `Evicted`, `FirstAvailable`).
  Thread-safety: single writer (ingest thread), many readers.
- **Files:** `src/AcsTool.Core/Settings/AppSettings.cs` (+ JSON context in `Persistence.cs` if needed), `src/AcsTool.PacketCapture/Pipeline/PacketStore.cs` (new), tests in Core.Tests (settings round trip, old settings file without the section loads) and PacketCapture.Tests.
- **Deps:** PT0.2, D8.
- **Acceptance:** Budget never exceeded after any append; evicts oldest; `WhenFull=StopCapture` signals the service; readers
  racing with eviction never see torn frames (stress test with 1 writer / 4 readers, 1e6 frames); existing `settings.json` without
  `packetCapture` loads with defaults; Settings "Restore defaults" keeps behaving as today.

### PT1.6 Ingest pipeline: PacketCaptureService — M
- **Description:** `PacketCaptureService` (singleton): `StartAsync(devices, options)`, `StopAsync()`, `OpenFileAsync(path)`,
  `Clear()`, `State` (Stopped/Starting/Capturing/Stopping/Faulted/Offline), `Stats` (frames, bytes, dropped kernel/if/app),
  `FramesAppended` cursor. Bounded `Channel<RawFrame>` (capacity from settings, **DropWrite**, `itemDropped` → app-drop counter),
  one `LongRunning` ingest loop: number → summarize → stream tracker → store; gap marker after drops; counters polled every 1 s.
  Per-device open failure doesn't kill other devices (banner per device). Logging 7000–7099 (start/stop, device errors, drops
  summarised at most once per 10 s).
- **Files:** `src/AcsTool.PacketCapture/Pipeline/PacketCaptureService.cs`, `PacketCaptureStats.cs` (new), tests with `FakeCaptureBackend`.
- **Deps:** PT1.1, PT1.3, PT1.4, PT1.5.
- **Acceptance:** Fake backend pushing 100k frames as fast as possible: no exception, `appDropped + stored == pushed`, ordering
  per device preserved; stop completes ≤ 2 s; start/stop/start works; the capture callback path does no allocation beyond the frame
  copy (BenchmarkDotNet not required — assert via a `Category=Perf` test with `GC.GetAllocatedBytesForCurrentThread`).

### PT1.7 BPF capture-filter builder — S
- **Description:** `CaptureFilterBuilder.Build(ProxySettings, PacketCaptureSettings, IEnumerable<Uri> simulatorTargets, Func<string, IPAddress[]> resolve)`
  per design §8.1 (listen port, M5 HTTPS port when present, upstream host(s)/port with scheme defaults, IPv6, optional DNS/ICMP).
  Plus `ForSession(SessionSnapshot)` ("Limit to session"). Validation via backend when available.
- **Files:** `src/AcsTool.PacketCapture/Filters/CaptureFilterBuilder.cs` (new), tests.
- **Deps:** PT0.3 (session endpoints), D9.
- **Acceptance:** Table tests: default settings → exactly the documented string; `https://acs.example:8443/x` resolving to two IPs
  → both hosts; IPv6 upstream; no port in URL → 80/443; DNS/ICMP off; session filter uses the exchange endpoints; strings are
  valid BPF (checked by `BpfProgram` in the opt-in live test).

### PT1.8 SessionCorrelator (conversation level) — M
- **Description:** `EndpointIndex` + `SessionCorrelator` per design §6.3 items 1, 2, 4, 5: polls `ISessionStore.GetAll()` every 1 s
  only when `Stats` changed; indexes exchanges with `Endpoints`; links conversations (`Exact` by 4-tuple + time overlap ±2 s;
  `Probable` by IP + time); handles two links (sim + proxy on loopback); unlinks when sessions are evicted/cleared. **Never**
  reads `ISessionStore.Changes`.
- **Files:** `src/AcsTool.PacketCapture/Correlation/*.cs` (new), tests.
- **Deps:** PT0.4, PT1.4.
- **Acceptance:** End-to-end test on Linux without native libs: run CPE client → `ProxyServer` → `FakeAcs`, synthesise the
  corresponding TCP frames from the recorded `ExchangeEndpoints` via `PacketBuilder` + `FakeCaptureBackend`, and assert both legs
  link `Exact` to the right session/seqs; port reuse after the session ended links to the new session only; unrelated traffic stays
  unlinked; store eviction removes links; a test proves the feed pump still receives every change (no second reader).

### PT1.9 Presentation: Packet Trace view models + PacketFeedPump — L
- **Description:** `PacketTraceViewModel` (source live/file, device selection, filter text + regenerate + validation state,
  start/stop/clear/open commands, quick toggles CWMP/Linked/Problems, session picker, footer stats, backend status → banners
  `npcap-missing`/`npcap-access`/`npcap-error`), `PacketListViewModel` (row VMs, max live rows, auto-scroll, selection),
  `PacketRowViewModel`, `ProtocolTreeViewModel` (built off-UI for the selected frame, cached), `HexDumpViewModel` (lines, highlight
  range ↔ node selection), `DeviceSelectionViewModel`, `PacketFeedPump` (100/250 ms batches, one dispatch per batch).
  `MainViewModel`: new `PacketTrace` property, status-bar segment, `ShutdownAsync` stops capture first. `IExternalToolLauncher`
  abstraction (new). No WPF types in Presentation.
- **Files:** `src/AcsTool.Presentation/PacketTrace/*.cs` (new), `src/AcsTool.Presentation/Shell/MainViewModel.cs`,
  `src/AcsTool.Presentation/Abstractions/Abstractions.cs`, `src/AcsTool.Presentation/ServiceCollectionExtensions.cs`,
  `tests/AcsTool.Presentation.Tests/PacketTraceTests.cs` (new).
- **Deps:** PT1.6, PT1.7, PT1.8.
- **Acceptance:** With `SynchronousUiDispatcher` + fake backend: 50k frames in 2 s → ≤ 25 dispatches; selection builds tree/hex
  off the dispatcher; node selection sets the hex highlight and vice versa; backend `NotInstalled` disables Start and shows the
  banner while Open file stays enabled; `ShutdownAsync` calls `StopAsync` on the capture service before `Proxy.StopAsync` (order test).

### PT1.10 App: Packet Trace tab, three panes, hex control — L
- **Description:** `Views/PacketTraceView.xaml` (+ code-behind only for focus/scroll), `Views/HexDumpView.xaml` (light custom
  control, renders visible lines only), third `TabItem` in `MainWindow.xaml` wrapped in the standard `ScrollViewer` pattern,
  GridSplitters, virtualized list with fixed row height and colour rules, context menu (Filter on conversation/session, Export
  selected, Copy), `Ctrl+E` start/stop when the tab is active, device picker pop-up, "Install Npcap…" link (opens npcap.com),
  `Services/WindowsExternalToolLauncher.cs`.
- **Files:** `src/AcsTool.App/Views/PacketTraceView.xaml(.cs)`, `HexDumpView.xaml(.cs)` (new), `src/AcsTool.App/Views/MainWindow.xaml`,
  `src/AcsTool.App/Services/PlatformServices.cs` or new `Services/ExternalToolLauncher.cs`, `src/AcsTool.App/App.xaml.cs` (DI),
  `src/AcsTool.App/Themes/Light.xaml` (row colours), `tests/AcsTool.Presentation.Tests/XamlLayoutTests.cs` (new tab included in the
  existing theories; new test that the three panes have splitters and the list is virtualized).
- **Deps:** PT1.9.
- **Acceptance:** App builds on Linux (`EnableWindowsTargeting`); `XamlLayoutTests` pass; Windows checklist W-PT-04…W-PT-08.

### PT1.11 Export (PCAPNG/PCAP) + credential masking + Open in Wireshark — M
- **Description:** `PacketExportService.ExportAsync(scope, format, path, maskCredentials, progress, ct)` with scopes
  selected/displayed/stream/session/all (session scope = all conversations linked to the session + DNS/ICMP frames in its time
  window); PCAPNG with per-frame comments; PCAP split per link type; atomic write (temp + rename like `AtomicFile`). `CredentialMasker`
  per design §12.2 (same-length, TCP checksum recomputed, MVP segment-span handling). `PacketExportViewModel` (dialog: scope,
  format, include credentials checkbox with warning, path defaulting to `DefaultExportDir\packets\acstool-<ts>-<scope>.pcapng`),
  "Export & open in Wireshark", "Open folder".
- **Files:** `src/AcsTool.PacketCapture/Export/*.cs` (new), `src/AcsTool.Presentation/PacketTrace/PacketExportViewModel.cs` (new),
  `src/AcsTool.App/Views/PacketExportDialog.xaml` (new), tests.
- **Deps:** PT1.2, PT1.8, PT1.9, D6, D7.
- **Acceptance:** Exported file re-reads with `PcapngReader` to identical bytes except masked ranges; `Authorization: Basic …` and
  Digest response/nonce/cnonce masked, Digest `username` kept, lengths identical, TCP checksum valid after masking; header split
  across two segments masked in both; "include credentials" leaves bytes untouched; comments `session=…` present on linked frames;
  mixed link types in PCAP format produce two files; cancellation leaves no partial file. Windows: W-PT-09/10.

### PT1.12 Docs — S
- **Description:** User guide page `docs/user-guide/packet-trace.md` (Npcap install with recommended options, devices incl.
  loopback, filters, three panes, correlation columns, export/masking, Wireshark), entries in `settings-reference.md`
  (`packetCapture.*`), `troubleshooting.md` (Npcap missing/admin-only/BFE/offload checksums/no devices after Windows update/drops),
  `ui-tour.md`, `roadmap.md`, `architecture.md` (new project), `IMPLEMENTATION_NOTES.md` (deviations).
- **Files:** as listed.
- **Deps:** PT1.1–PT1.11.
- **Acceptance:** ReviewerBot doc review; every setting in `PacketCaptureSettings` documented with default.

### PT1 acceptance (milestone)
- **PT1-A1** No libpcap/Npcap on CI → all automated tests green on Linux and Windows runners with `CI=true`; `--smoke-exit` passes.
- **PT1-A2** Without Npcap the tab shows the "not installed" state; offline file open works.
- **PT1-A3** Live capture on ≥ 2 devices (LAN + loopback) concurrently; frames merged in one list with an Interface column.
- **PT1-A4** Default BPF generated from settings and editable; invalid filter shows libpcap's error without starting.
- **PT1-A5** Three panes linked (list → tree → hex highlight, hex click → tree node).
- **PT1-A6** CPE-leg and ACS-leg conversations of a proxied session link `Exact` to the session (Session column + AcsTool tree node).
- **PT1-A7** Session picker filters to that session's packets; "Limit to session" restarts with a session-specific BPF.
- **PT1-A8** Export selected/stream/session/all as PCAPNG (default) or PCAP into `Documents\AcsTool\exports\packets`; opens in Wireshark with comments visible.
- **PT1-A9** Credentials masked by default in exports; checkbox to include.
- **PT1-A10** Budgets respected (frames/bytes), drops counted and visible (kernel/if/app), UI responsive at 5,000 pps synthetic.
- **PT1-A11** Existing Proxy/Simulator tabs, session feed and Summary/Headers/Raw are unchanged: no diffs in `ProxyView.xaml`, `SimulatorView.xaml`, `SessionFeedView.xaml`, `SessionDetailView.xaml`; `MainWindow.xaml` only gains the third tab; `MainViewModel` only gains the `PacketTrace` property, the status-bar segment and the capture stop in `ShutdownAsync`.
- **PT1-A12** Stopping capture and closing the app leaves no running capture threads; Serilog shows 70xx start/stop lines; no secrets in logs.

---

## PT2 — Streams and HTTP/CWMP rebuild

| ID | Title | Description | Files | Deps | Size | Acceptance |
|---|---|---|---|---|---|---|
| PT2.1 | `TcpReassembler` | Per stream/direction byte stream: seq ordering with wrap, retransmission de-dup, overlap first-wins, gap markers (loss/snaplen/eviction), `ReassemblyBudgetBytes` (release closed streams first; over-budget stream keeps head + tail 64 KiB). Offset → frame map. | `PacketCapture/Streams/TcpReassembler.cs` (new) | PT1.4 | L | Out-of-order, duplicate, overlapping, missing segment, wrap, and SYN-less mid-stream start scenarios produce exact byte streams + gap markers; budget never exceeded. |
| PT2.2 | `HttpWireExtractor` | HTTP/1.1 framing on reassembled streams: request/status line, headers, Content-Length, chunked (de-chunk), keep-alive, pipelined, `100 Continue`, read-to-close, 204/304/1xx, HEAD. Bounded bodies (`Capture.MaxCapturedBodyBytes`). Problems list (truncated, gap inside body, bad chunk). | `PacketCapture/Streams/HttpWireExtractor.cs`, `Model/WireHttpMessage.cs` (new) | PT2.1 | M | Fixture streams incl. CWMP session (Inform → 200 InformResponse → empty POST → 204), chunked ACS response, 401 + retry on same connection, `Connection: close` → correct messages with frame ranges. |
| PT2.3 | CWMP from wire | Bodies of `WireHttpMessage` go through existing `ICwmpParser` + `IMessageSummarizer`; tree "HTTP" + "CWMP" nodes with the same fields as the Summary tab; Info column shows `Inform (BOOT)` etc. | `PacketCapture/Decode/ProtocolTreeBuilder.cs`, DI | PT2.2 | S | All `tests/fixtures/*.xml` wrapped into synthetic HTTP/TCP give the same `MessageSummary` as the app-layer parser (equality test). |
| PT2.4 | Follow TCP Stream | `StreamFollowViewModel` + `StreamFollowWindow.xaml`: interleaved directions (colours), ASCII/hex/HTTP-messages modes, gap markers, save raw, export stream PCAPNG. | Presentation `PacketTrace/StreamFollowViewModel.cs`, App `Views/StreamFollowWindow.xaml` (new) | PT2.2 | M | VM tests for each mode; Windows W-PT-15. |
| PT2.5 | Exchange-level correlation + wire-vs-app diff | Design §6.3 item 3 and §10.2 "Wire vs app diff": n-th message ↔ exchange by connection id + method/path/length + body prefix; diff findings (never left the PC, RST before response, response on wire but app timeout, rewritten headers labelled). Shown in the AcsTool tree node and as a "Problems" filter. | `PacketCapture/Correlation/*`, Presentation tree VM | PT2.2, PT1.8 | M | Integration test (proxy + FakeAcs + synthesized frames): each exchange matched to its wire messages; injected faults (drop ACS-leg SYN-ACK; RST after request; extra header) produce the expected findings. |
| PT2.6 | Stream-aware masking | `CredentialMasker` uses reassembler offsets (header anywhere in the stream, incl. across 3+ segments). | `PacketCapture/Export/CredentialMasker.cs` | PT2.1, PT1.11 | S | Header split at every possible byte boundary across 2–3 segments is masked; lengths/checksums valid. |

**PT2 acceptance:** PT2-A1 reassembly correct on fixtures · A2 HTTP messages extracted with frame ranges · A3 CWMP summary from
wire equals app summary · A4 Follow Stream window works on live and offline data · A5 per-exchange links shown · A6 diff findings
for the three injected-fault scenarios · A7 reassembly budget respected under a 1 GiB synthetic stream.

## PT3 — Offline analysis and linking polish

| ID | Title | Description | Files | Deps | Size | Acceptance |
|---|---|---|---|---|---|---|
| PT3.1 | Display filter | Managed subset (design §8.2) with parser, error position, history; replaces/extends quick toggles. | `PacketCapture/Filters/DisplayFilter*.cs`, VM | PT1.9 | M | Grammar tests; filter of 200k frames applied off-UI in < 1 s. |
| PT3.2 | Wire sessions for files | Group rebuilt CWMP messages into display-only sessions (CPE IP + cookie + Inform boundaries); list in a side panel; export per wire session (D12). | `PacketCapture/Correlation/WireSessionBuilder.cs` (new), VM, view | PT2.3 | M | Fixture capture with two CPEs and three sessions → three wire sessions with right exchanges; nothing written to `InMemorySessionStore`. |
| PT3.3 | "Show packets" cross-links | One context-menu item in the session feed + a link in the detail header → switches to Packet Trace filtered to the session (D11). Summary/Headers/Raw tabs untouched. | `SessionFeedView.xaml`, `SessionDetailView.xaml` (header only), `SessionFeedViewModel`, `MainViewModel` | PT1.9 | S | VM test: command selects tab 2 and sets the session filter; XAML tests still green. |
| PT3.4 | TLS handshake details + M5 link | TLS ClientHello/ServerHello/Alert/Certificate (subject/issuer/thumbprint SHA-256 when unencrypted ≤ TLS 1.2) in the tree; match alerts to `SessionSnapshot.Tls`/`UpstreamError(Kind=Tls)`. | `PacketCapture/Decode/TlsDissector.cs` (new) | PT1.3, M5 merged | M | Fixtures (TLS 1.2 and 1.3 handshakes, alert 40/42/48) dissected correctly; thumbprint equals `TlsInfo` thumbprint in an HTTPS integration test (Linux, synthetic capture from test certs). |
| PT3.5 | Capture spooling | Optional ring of PCAPNG files on disk (`%LOCALAPPDATA%\AcsTool\pcap-spool`, N × M MiB) so long captures survive in-memory eviction; "export" can read from spool. | `PacketCapture/Pipeline/SpoolWriter.cs` (new), settings | PT1.6, PT1.2 | M | 1 h synthetic capture keeps memory within budget and spool within N×M; export of an evicted range reads from spool. |
| PT3.6 | Pooled frame buffers (perf) | Replace exact-size arrays with slab/pool if PT1 perf numbers show GC pressure. | `PacketStore`, ingest | PT1 perf data | S–M | Gen0 GCs/s reduced ≥ 50 % at 5,000 pps with no correctness regressions. |

**PT3 acceptance:** A1 display filter · A2 wire sessions for offline files · A3 cross-links · A4 TLS details · A5 spooling ·
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

## TesterBot test plan

### Automated — runs on Linux CI (and Windows CI) without libpcap/Npcap

| Area | Tests |
|---|---|
| File formats | PCAP µs/ns, both endiannesses; PCAPNG multi-section/multi-IDB, `if_tsresol` variants, comments, unknown blocks, truncation; writer round-trip; optional `capinfos` validation when present. |
| Decoding | Each link type (Ethernet, Null, Loop, Raw, SLL, SLL2) × IPv4/IPv6 × TCP/UDP/ICMP; malformed/fuzzed input (10k random frames, no throw); tree byte ranges. |
| Streams | Stream ids, roles, states, hints; reassembly scenarios (PT2); HTTP extraction (PT2); CWMP summary equality with app parser (PT2). |
| Pipeline | Fake backend floods (100k frames), DropWrite accounting, stop/start, per-device failure isolation, budget/eviction stress with concurrent readers. |
| Correlation | Proxy + FakeAcs integration with synthesized frames from real `ExchangeEndpoints` (both legs, reconnect mid-session, port reuse, eviction); simulator → proxy on loopback double link. |
| Filters | BPF builder tables; display filter grammar (PT3). |
| Export | Scopes, PCAPNG comments, PCAP split, masking (Basic, Digest, split headers), checksum validity, atomicity/cancellation. |
| Presentation | Pump batching (≤ 10 dispatches/s), selection → tree/hex off-UI, banners per backend status, shutdown order, XAML layout rules for the new tab. |
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
| W-PT-04 | Real modem (CPE) → proxy on LAN NIC + upstream `localhost:7557` ACS: both legs captured (LAN + Loopback), both linked `Exact` to the session. |
| W-PT-05 | Three panes: tree ↔ hex highlight both directions; splitters; narrow window (tab scrolls, no clipped controls). |
| W-PT-06 | Default BPF matches settings; edit to invalid → libpcap error shown; regenerate restores. |
| W-PT-07 | "Limit to session" restarts capture with the session filter; only that CPE's frames appear. |
| W-PT-08 | Kill the ACS mid-session (or block 7557 with the firewall): RST/SYN-unanswered frames visible and linked; matches the app-layer `UpstreamError`. |
| W-PT-09 | Export session as PCAPNG → opens in Wireshark via "Export & open"; packet comments `session=…` visible; frames identical to AcsTool's list. |
| W-PT-10 | Export with Basic and Digest auth sessions: credentials masked in Wireshark's HTTP view; "include credentials" export shows them; Wireshark reports no TCP length errors. |
| W-PT-11 | Open a Wireshark-saved `.pcapng` (multi-interface) and a `.pcap` offline; correct decoding and times. |
| W-PT-12 | Busy NIC with **empty** filter for 5 min: warning shown; drop counters move but UI stays responsive; memory ≤ budget + ~40 MiB; proxy sessions still complete. |
| W-PT-13 | Simulator → proxy on the same PC: loopback conversation linked to both the simulator and the proxy session. |
| W-PT-14 | HTTPS upstream (or M5 HTTPS listener): TLS records shown, no HTTP; certificate error case shows the alert frame and links to the session with `UpstreamError Tls`. |
| W-PT-15 | (PT2) Follow TCP Stream on a CWMP session: interleaved Inform/InformResponse/empty/204; HTTP-messages mode; save raw. |
| W-PT-16 | App close while capturing: closes within the normal shutdown time; no orphaned Npcap handles (re-open capture in Wireshark works). |

---

## Decisions to confirm before coding (copy of design §14)

D1 PT prefix, PT1 after M5 · D2 no conditional compilation, stable packages · D3 Npcap not bundled · D4 stay `asInvoker` ·
D5 managed PCAP/PCAPNG I/O · D6 PCAPNG default with comments · D7 masking on by default · D8 memory defaults (128 MiB / 200k frames, ring) ·
D9 generated BPF incl. DNS/ICMP · D10 auto devices incl. Loopback · D11 cross-links only in PT3 · D12 wire sessions display-only ·
D13 additive Core endpoints + polling correlator · D14 no TLS decryption on Windows.
