# Review: PT1.1 capture backend + DeviceSelector + FakeCaptureBackend

- Branch `feat/packet-trace-pt1` @ `5d747b9` (one commit), diff `e490f2f..5d747b9` (18 files, +1216/-5)
- Reviewer: ReviewerBot, Thu 2026-11-05 ~10:10–10:30 EST
- Method: private clone `/tmp/rv-pt` (detached), `CI=true` Release build, `dotnet test --filter "Category!=Perf"` + XPlat coverage,
  19 source mutations (revert/neuter key lines), 25x repeat of PacketCapture.Tests, native-free scratch harness for the real
  `SharpPcapCaptureSession` (subclass of `LibPcapLiveDevice` with a simulated capture thread, mirroring SharpPcap 6.3.1
  `StopCapture`); SharpPcap v6.3.1 source read for `StopCapture`/capture loop/`Open`. Shared checkout not touched. No servers.

## Verdict: FIXES NEEDED (1 Major)

## CI (verified with gh)
- Run 42052702767: **success**, head `5d747b935cd8…`, 10:13–10:14 EST.
- linux (libpcap present): 664 passed, 6 skipped (PacketCapture 44 + 4 skipped: 2 live, real-NotInstalled probe, ListDevices NotInstalled).
- windows (no Npcap): 665 passed, 5 skipped (PacketCapture 45 + 3 skipped: 2 live, ListDevices_WhenAvailable); publish + `--smoke-exit` green.

## Local run (box: no libpcap, uid 1000)
- Build `CI=true` Release: 0 warnings, 0 errors.
- Tests: Core 348+2s, Proxy 108, Simulator 101, Presentation 63, PacketCapture 45+3s = **665 passed, 5 skipped, 0 failed**.
- PacketCapture.Tests x25: 25/25 green.
- Coverage of changed lines (merged): src 91/279 (33 %): DeviceSelector 58/69, options/exception/RawFrame/LinkKind ~full,
  **SharpPcapBackend 4/96, SharpPcapCaptureSession 0/81**; FakeCaptureBackend 99/106. (Linux CI additionally runs
  ListDevices_WhenAvailable → ToInfo/CollectAddresses; the session and Open stay 0 % in every CI job.)
- Mutations: 8/19 killed. Killed: DeviceSelector IsUp, loopback add, own-NIC self target, localhost literal; DllNotFound→NotInstalled;
  fake Start idempotence; fake interface stamping; eager `Pcap.Version` in ctor (killed by the Presentation smoke-exit tests).
  **Survived:** v4-mapped match, host:port split, bracketed IPv6, loopback exclusion on specific bind, session copy→zero buffer,
  session Start idempotence, timestamp epoch, original length, bad-filter Close/Dispose, `isUp` flag, fake Stop join.

## Findings

### M1 (Major) Capture callback takes the session lock that Stop/Dispose hold across StopCapture → stalled Stop, thread not joined; violates PT-1
- `src/AcsTool.PacketCapture/Backend/SharpPcapCaptureSession.cs:129-130` (`lock (_gate) callback = _onFrame;` on the capture thread),
  `:44-59` (Stop) and `:85-122` (Dispose) call `_device.StopCapture()` while holding `_gate`; `:64-80` ReadCounters holds `_gate` over a native stats call.
- SharpPcap 6.3.1 `StopCapture` cancels, `pcap_breakloop`, then `Task.WaitAny(captureThread, StopCaptureTimeout)` (= 2 × ReadTimeout = 200 ms) and returns
  without throwing. Under traffic the capture thread is parked on `_gate`, so the wait always times out.
- Measured with the real session class (scratch harness, simulated busy capture thread):
  idle Stop 1.7 ms; **busy Stop 200.2 ms, capture thread still alive after Stop returned**; busy Dispose 200.2 ms.
  With the one-line fix below: busy Stop 0.1 ms, thread exited, 0 callbacks after Stop, timestamps/orig length unchanged.
- Also breaks design §5.3 PT-1 ("No decoding, logging, locking … on that thread"); ReadCounters polling (1/s from the chip later) blocks the capture thread.
- The ICaptureSession contract ("Stops capture and joins the capture thread") is therefore not met; PT1.10 shutdown order relies on it.
- Fix: read the delegate lock-free (`var callback = Volatile.Read(ref _onFrame);`, write it with `Volatile.Write`), keep `_gate` only for
  state transitions; optionally call `StopCapture()` outside the lock (flip `_started` under the lock first). Add a native-free regression
  test: `LibPcapLiveDevice` is public/non-sealed with virtual `Started/StartCapture/StopCapture/Opened/Close`, and `SendPacketArrivalEvent`
  is protected, so a test subclass (`base(null!)`) can drive the internal session (the harness used here did exactly that).

### m1 (Minor) Start failure leaves the handler subscribed → duplicated frames on retry; raw exception escapes
- `SharpPcapCaptureSession.cs:35-38`: `OnPacketArrival +=` and `_onFrame` are set before `StartCapture()`; if it throws, `_started` stays false.
  Verified: after a throwing first Start, a second Start delivered **2 callbacks per packet**. The exception is not a `CaptureOpenException`.
- Fix: try/catch around `StartCapture()`: unhook, null `_onFrame`, rethrow as `CaptureOpenException(…, Error, ex)`.

### m2 (Minor) D10: specific loopback bind selects nothing for the bind address
- `DeviceSelector.cs:37` excludes `IsLoopback` devices in the specific-bind branch, so `BindAddress=127.0.0.1`/`::1` never selects the NIC that owns it
  (D10: "the NIC owning the proxy bind address"); with a remote upstream the result is empty. Mutation "remove the loopback exclusion" survived (no test).
- Fix: drop `!d.IsLoopback &&` there (dedupe already via `Contains`), add table rows for bind 127.0.0.1 / ::1. Consider documenting that with a
  specific bind the upstream leg leaves via the routed NIC, which may differ from the bind NIC.

### m3 (Minor) Real session / Open paths untested; several selector branches unpinned
- Session 0/81 and `Open` 0 % in all CI jobs; mutants that zero the copied bytes, drop the epoch, use capture length as original length,
  break Start idempotence, or skip Close/Dispose on a bad filter all survive. `ToUtcTicks` is `internal static` and trivially testable.
- Selector: add rows for `127.0.0.1:7547`, `[::1]:7547`, `::ffff:127.0.0.1`/mapped NIC address, `isUp` flag (ToInfo), empty device list.
- Fake test `Start_Stop_Idempotent_AndStopJoins` (`FakeCaptureBackendTests.cs:33-70`) doesn't assert the join (mutant removing `Join` survived):
  assert no callbacks after Stop / feeder thread finished.
- Fix: seam tests as in M1, plus the rows above.

### m4 (Minor) Stop is not a join on Linux even after M1
- SharpPcap's Linux loop sits in a managed `poll(…, 500 ms)` (`PcapDeviceCaptureLoop.PollFileDescriptor`) while `StopCaptureTimeout` is 200 ms, so
  Stop can return with the thread alive for ≤ ~300 ms; a quick Stop→Start could briefly run two dispatch loops on one pcap handle.
- Fix: after `live.Open(config)` set `live.StopCaptureTimeout = TimeSpan.FromMilliseconds(Math.Max(2 * ReadTimeout, 750))`, or document Stop as
  best-effort. (Reasoned from source; not reproducible here without libpcap.)

### m5 (Minor) Docs
- No PT1.1 "Landed" note in `docs/PACKET_TRACE_TASKS.md` §PT1.1 nor an `IMPLEMENTATION_NOTES.md` entry (previous milestones had both).
- `SharpPcapCaptureSession.cs:127` comment ("No … locks beyond reading the delegate") contradicts design PT-1; `CaptureOpenOptions.InterfaceId` is not in design §5.2.

### Nits
- n1 `SharpPcapBackend.cs:148`: AccessDenied relies on message text; Linux activation failure reads "Unable to activate the adapter (x). (Error Code: PermissionDenied)"
  and only matches because of "Permission…". Prefer `PcapException.Error is PcapError.PermissionDenied or PcapError.PromiscuousPermissionDenied`.
- n2 `SharpPcapCaptureSession.cs:144`: callback exceptions are swallowed silently; count them (diagnostic counter) so a broken ingest isn't invisible.
- n3 Fake fidelity: `ReadCounters` after Dispose doesn't throw (`FakeCaptureBackend.cs:160`; real throws ODE); `Open` accepts devices not in `Devices` (`:53`,
  real throws "not found"); counters are a fixed snapshot (can't script growth/drops); Stop releases the lock before joining (`:140-158`) so a concurrent Start is a silent no-op.
- n4 `DeviceSelector`: a hostname that resolves to a local IP (machine name) isn't treated as local (no DNS) — document; IPv6 link-local scope ids make `Equals` fail.
- n5 `ToUtcTicks` (`:150-164`): decimal math per frame is fine; the catch/fallback is effectively unreachable.
- n6 `AcsTool.Testing` now references PacketCapture, so every test project pulls SharpPcap transitively (harmless; smoke tests still green).

## Checks
1. API vs design §5.2/§6.1/D-decisions: matches (LinkKind exactly §6.1; options defaults = §7/D8 snap 65,535, 16 MiB kernel buffer, 100 ms, ns preferred; tested).
   Extra `InterfaceId` option (m5). D11/D15/D16 not in scope of this slice; nothing contradicts them.
2. Callback safety: copies exact-size (`ToArray`), RawFrame owns its buffer, original length from `PcapHeader.PacketLength`, UTC ticks from
   `PosixTimeval.Value` (ns-capable) — verified correct in the harness. Exceptions contained. **Lock on the capture thread → M1.**
3. Lifecycle: Start/Stop idempotent; use-after-dispose: Start/ReadCounters throw ODE, Stop no-op; Open closes/disposes on bad filter and on other failures;
   NotInstalled/AccessDenied classified (n1). Stop-vs-callback: no callbacks after Stop (null delegate), but Stop stalls (M1) and isn't a join (m4); Start failure m1.
4. Counters: received / kernel drops / interface drops from pcap stats, read under the lock (thread-safe), best-effort on error. Bounded queue/backpressure
   (PT-2, 20k DropWrite) belongs to PT1.7 — not present here, as expected.
5. DeviceSelector: deterministic (input order, no sets/hash ordering), handles none (empty), multiple NICs, down NICs, loopback via upstream/simulator/own-NIC self-connection;
   m2 for loopback bind.
6. Fake lives in `tests/AcsTool.Testing` only; no `src` project references it; reasonable fidelity (n3).
7. `--smoke-exit`: `SmokeTests.ShellBuild_ResolveAll_DoesNotLoadSharpPcap` / `SmokeExitPath_…` green; positive control (eager `Pcap.Version` in ctor) makes both fail.
   `SharpPcapBackend` still has no statics; new types touch SharpPcap/PacketDotNet only inside methods. Windows CI smoke-exit green.
8. Tests: meaningful for selector/fake; 0 analyzer warnings; gaps m3.
9. Docs: m5.

## Not verified on Linux
- `ACSTOOL_PCAP_LIVE=1`: not run — this box has no libpcap and no root/CAP_NET_RAW (would need installing packages and raising privileges). Live open/start/stop,
  bad-filter text, AccessDenied on open, real Stop timing (M1/m4 shown via harness + SharpPcap source), kernel buffer / ns timestamp acceptance.
- Npcap behaviour (Windows device names, NPF_Loopback DLT_NULL, admin-only Npcap) — covered only by CI "not installed" path.
