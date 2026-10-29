# Test report: M0–M4 (MVP) — ACS TR-069 Proxy & Inform Simulator

Author: TesterBot · Date: 2026-10-29 (Asia/Tehran, UTC+3:30) · Base commit: `b67eb19` · Branch: `tester/m0-m4`
Scope: automated build/test verification on Linux, coverage of the 15 mandatory scenarios (PLAN §5.1), exploratory
verification of the proxy against FakeAcs, and a manual Windows UI checklist. `src/` was not modified.

## 1. Summary verdict

**Recommended gate: CONDITIONAL PASS.**
- **M0–M3: PASS.** Everything builds with 0 warnings and 0 errors (also with `CI=true`, where warnings are errors). All tests pass in Debug, in Release and through `./build.sh`, and 3 extra Debug runs showed no flakiness. Line coverage is above 80% for Core, Proxy and Simulator.
- **M4: PASS (Linux part) / PENDING (Windows part).** The view-model tests pass. The WPF app has only been compiled, never rendered. The M4 exit criterion ("on Windows the proxy and simulator are usable end-to-end") still needs the manual checklist in §7.
- **No Critical or High bugs.** I found 2 product bugs (1 Medium, 1 Low) and 1 test-quality issue (Low). They are listed in §6. None of them blocks the MVP, but BUG-01 should be fixed before M5 (auth/fault work).

## 2. Environment

| Item | Value |
|---|---|
| OS | Debian 13 (Linux x64), shared box (ReviewerBot working in parallel on the main checkout) |
| .NET | SDK 8.0.427, host 8.0.33, MSBuild 17.11.69 |
| Test stack | xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4 |
| Isolation | Separate git worktree `/workspace/projects/acs-tr069-tool-tester` (branch `tester/m0-m4`), so the shared checkout was not touched. `MSBUILDDISABLENODEREUSE=1`. All listeners bind `127.0.0.1:0` (ephemeral). No processes left running. |
| Network quirk | The box resolves every name (including `nonexistent.invalid` → `198.18.0.1`), and an intercepting egress proxy (`Server: Pingora`) answers HTTP 500. **The real DNS-failure path cannot be exercised on this box** (see §5, OBS-03). |
| Headless proxy | There is no headless proxy CLI (the App is WPF-only). Exploratory runs used the in-process test harness: a real Kestrel `ProxyServer` + a real `FakeAcs` + `HttpClient`/raw TCP as the CPE. |

## 3. Build and test results (actual command output)

### 3.1 Baseline at `b67eb19` (before any TesterBot tests)

| Command | Result | Warnings / errors | Duration |
|---|---|---|---|
| `dotnet build AcsTool.sln -c Debug` | success | 0 / 0 | 7.72 s (MSBuild), 8 s wall |
| `dotnet test AcsTool.sln -c Debug --no-build` | **448 passed, 0 failed, 0 skipped** (Core 303, Proxy 55, Simulator 59, Presentation 31) | — | 7 s wall |
| `dotnet build AcsTool.sln -c Release` | success | 0 / 0 | 6.10 s |
| `dotnet test AcsTool.sln -c Release --no-build` | **448 passed, 0 failed, 0 skipped** | — | 7 s wall |
| `CI=true ./build.sh` (clean tree, coverage on) | exit 0; **447 passed, 0 failed, 0 skipped** (Core 302: the `Category=Perf` test is excluded by design) | 0 / 0 (TreatWarningsAsErrors) | 13 s wall (build 3.10 s) |

### 3.2 Final, on branch `tester/m0-m4` (with the 24 new TesterBot tests)

All runs used a clean `bin/obj` tree.

| Command | Presentation | Core | Simulator | Proxy | Total | Warn/Err | Wall |
|---|---|---|---|---|---|---|---|
| `dotnet build -c Debug` | | | | | success | 0 / 0 | 4 s |
| `dotnet test -c Debug` | 31/0/0 | 303/0/0 | 61/0/0 | 75/0/**2** | **470 passed, 0 failed, 2 skipped (472)** | — | 9 s |
| `dotnet test -c Debug` ×3 repeats | same | same | same | same | 470/0/2 each run (no flakes) | — | 9–10 s |
| `dotnet build -c Release` | | | | | success | 0 / 0 | 4 s |
| `dotnet test -c Release` | 31/0/0 | 303/0/0 | 61/0/0 | 75/0/2 | **470 passed, 0 failed, 2 skipped (472)** | — | 9 s |
| `CI=true ./build.sh` | 31/0/0 | 302/0/0 | 61/0/0 | 75/0/2 | **469 passed, 0 failed, 2 skipped (471)**, exit 0 | 0 / 0 | 15 s |

(Cells read passed/failed/skipped.) The 2 skips are the intentional bug-reproduction tests for BUG-01 and BUG-02. I un-skipped
them and ran them on purpose: **both fail** (see §6). The `CI=true` gate also caught a CA1806 warning in one of my new tests on the
first attempt. I fixed it in the test, so the gate works as intended.

### 3.3 Coverage (coverlet, `CI=true ./build.sh`, each assembly measured by its own test project)

| Assembly | Line | Branch | NFR-09 (≥80% line) |
|---|---|---|---|
| AcsTool.Core | 87.2% | 67.4% | ✔ |
| AcsTool.Proxy | 90.2% (89.8% at baseline) | 79.3% | ✔ |
| AcsTool.Simulator | 89.8% | 71.9% | ✔ |
| AcsTool.Presentation | 83.5% | 64.5% | (no target) |

## 4. Scenario coverage matrix (PLAN §5.1, 15 mandatory scenarios)

Legend: **C** = covered by an integration test, **P** = partially covered, **M** = missing, **OOS** = out of MVP scope (a later milestone per PLAN §5 / IMPLEMENTATION_NOTES).
New TesterBot tests live in `tests/AcsTool.Proxy.Tests/TesterExploratoryTests.cs` (TE) and `tests/AcsTool.Simulator.Tests/TesterScenarioTests.cs` (TS).

| # | Scenario | Before | After | Tests |
|---|---|---|---|---|
| 1 | Happy CWMP session via the proxy | C | C | `ForwardingTests.FullSession_ThroughProxy_OneCompletedSessionWithOrderedExchanges`; TE `Bootstrap_Cwmp12Tr181Fixture_…`, `Boot_Cwmp10Fixture_…`, `Periodic_DefaultNamespaceFixture_…` |
| 2 | Digest challenge via proxy (401 → 200), one session | C | C | `ForwardingTests.DigestAuth_RelayedTransparently_SingleSession`; `TrackerTests.Resolve_InformAfter401_ReusesSession`; TE `Digest_ChallengeRecorded_WwwAuthenticateRelayedVerbatim`, `Digest_WrongPassword_TwoConsecutive401_AuthFailedSingleSession`, `Basic_RelayedAndAccepted`, `Forbidden403_AuthFailed` |
| 3 | Upstream DNS failure → 502 + `Dns` | P | **P** | `NetTests.Classify_NameResolutionError_Dns` (constructed exception); `NetTests.Dns_NonexistentInvalidTld_Dns` (UpstreamSender level, **no-ops on this box**, see BUG-03); TE `UpstreamDns_InvalidTld_502DnsOrSinkholeFallback` (proxy level, but here it took the sinkhole branch: HTTP 500 → `HttpError`). The real `Dns` path through the proxy was never run. Verify on Windows (§7, W-11). |
| 4 | Upstream connection refused → 502 + `Connect/ConnectionRefused` | C | C | `ForwardingTests.UpstreamDown_502AndConnectError`; TE `UpstreamDown_ConnectionRefusedSocketError_RecordedOnExchange` (adds the SocketError, `Connection: close` and empty-body checks); `NetTests.Connect_ClosedLoopbackPort_ConnectionRefused` |
| 5 | Upstream TLS untrusted → 502 + `Tls` | P (unit only) | **OOS** (M6) | `NetTests.Classify_SecureConnectionError_Tls` (constructed). HTTPS tests are deferred to M6 (IMPLEMENTATION_NOTES). |
| 6 | Upstream slow → 504 + `Timeout/AwaitingResponse` | C | C | `ForwardingTests.SlowUpstream_504AndTimeoutAwaitingResponse`; TE `UpstreamAcceptsButNeverAnswers_504Timeout`, `UpstreamTimeoutMidSession_SessionEndsUpstreamError_CpeGets504`; TE `UpstreamAbortsConnection_502Protocol` (related) |
| 7 | ACS HTTP 500 → relayed + `HttpStatus` | C | C | `ForwardingTests.Upstream500_RelayedAndClassifiedHttpStatus` |
| 8 | CPE abandons mid-session → `Abandoned` | C | C | `ForwardingTests.IdleSession_AbandonedBySweeper`; `TrackerTests.Sweeper_IdleTimeout_AbandonsAndDisposesHandler` (FakeTimeProvider) |
| 9 | Two CPEs concurrently, no cross-talk (cookies, handlers) | P (no cookies/RPCs) | **C** | `ForwardingTests.TwoCpesInParallel_TwoSessions`; TE `TwoCpesConcurrently_WithAcsCookies_NoCrossTalk` (ACS cookies + GPV per CPE, checks payload and cookie isolation) |
| 10 | Simulator: SPV then GPV reflects the new value | C | C | `EngineTests.SendInformNow_SpvGpvSession_StoreUpdated_StatesIdleInSessionIdle` |
| 11 | Simulator: unknown RPC → Fault 9000, session continues to 204 | P (fake transport only) | **C** | `ClientSessionTests.UnknownMethod_Upload_Fault9000WithEchoedId` (unit); TS `Scenario11_UnknownRpc_Fault9000EchoedId_SessionContinuesTo204` (real HTTP vs FakeAcs), `Scenario11_ThroughProxy_ProxyRecordsCpeFaultOutcome` |
| 12 | Simulator: Reboot → follow-up `1 BOOT`, `M Reboot` | OOS | OOS (M7) | Follow-ups are not implemented in the MVP (IMPLEMENTATION_NOTES). |
| 13 | Simulator: retry backoff with RetryCount increments | OOS | OOS (M8) | Partly: `EngineTests.FailedSession_ErrorStateKeepsEvents_ManualRetryIncrementsRetryCount` (manual retry only, no backoff schedule) |
| 14 | Simulator → Proxy → FakeAcs: two matching records | C | C | `EndToEndTests.Simulator_Through_Proxy_To_FakeAcs_ProducesTwoMatchingSessionRecords`; TS `Scenario11_ThroughProxy_…` |
| 15 | Export round trip with redaction | OOS | OOS (M9) | Redaction parts only: `SettingsAndUtilTests.AuthRedactor_*`, `DetailTests.Authorization_MaskedUntilRevealed` |

**Tally (after): Covered 10 · Partial 1 · Missing 0 · Out of MVP scope 4.**
(Before TesterBot tests: Covered 8 · Partial 3 (#3, #9, #11) · Missing 0 · OOS 4, counting #5 as OOS.)

## 5. Exploratory findings (crafted CPE traffic → real ProxyServer → FakeAcs)

All of these are automated in TE/TS and run in every `dotnet test`.

| Probe | Expected (PLAN / TR-069) | Actual | Status |
|---|---|---|---|
| BOOTSTRAP Inform (`inform-bootstrap-cwmp12-tr181.xml`, cwmp-1-2, `0 BOOTSTRAP`+`1 BOOT`) + empty POST | Body byte-identical upstream; one session `Completed`; DeviceId, version and events recorded | As expected (SHA-256 equal, `A1B2C3-CT0001`, V1_2, both events) | ✔ |
| BOOT Inform (`inform-boot-cwmp10.xml`) | Same | As expected | ✔ |
| PERIODIC Inform, default-namespace style (`inform-periodic-unqualified-noprefix.xml`) | Parsed; `2 PERIODIC`; `Completed` | As expected | ✔ |
| Malformed XML as the first POST (`not-xml.txt`) | Forwarded verbatim (fidelity); orphan "Unknown session from …"; summary NotXml; ACS status relayed | As expected (FakeAcs 400 relayed → `HttpError`, `HttpStatus`) | ✔ |
| Malformed XML mid-session (same connection) | Stays in the same session | As expected | ✔ |
| XXE body (`xxe-attempt.xml`) | Forwarded verbatim; entity not resolved; proxy keeps serving | As expected. The orphan stays open until the idle sweep (empty 2xx to a non-empty, non-Inform request means "keep open" per §3.6.4) | ✔ (OBS-02) |
| Empty POST with no session | Orphan, forwarded with `Content-Length: 0`; 204 relayed; ends | As expected (`Completed`) | ✔ |
| Empty 200 answer to an Inform | `ProtocolError` | As expected | ✔ |
| Upstream down (closed port) | 502, empty body, `Connection: close`, `UpstreamError/Connect`, SocketError `ConnectionRefused` | As expected | ✔ |
| Upstream DNS (`nonexistent.invalid`) | 502 + `Dns` | Box sinkhole: HTTP 500 from the egress proxy → relayed, `HttpError`. Not verifiable here | ⚠ OBS-03 |
| Upstream accepts TCP but never answers (1 s timeout) | 504, `Timeout/AwaitingResponse` | As expected | ✔ |
| Upstream times out mid-session (on the empty POST) | 504 to the CPE; session `UpstreamError`; first exchange clean | As expected | ✔ |
| Upstream resets the connection | 502, `Protocol` | As expected | ✔ |
| 401 Digest challenge only | `WWW-Authenticate` relayed verbatim; note `AuthChallenge`; no error; session stays open | As expected | ✔ |
| Digest with the wrong password | Two consecutive 401s → `AuthFailed`, one session | As expected | ✔ |
| Basic auth | Relayed; `Completed` | As expected | ✔ |
| 403 | `AuthFailed` | As expected | ✔ |
| Two concurrent CPEs with ACS cookies + GPV | Two sessions, no payload or cookie cross-talk | As expected | ✔ |
| **ACS SOAP Fault (8005) in answer to an Inform** | Session ends `AcsFault` (CPE ends the session; matches the simulator's own rule §3.7.2) | Stays `InProgress` past the 2 s grace; ends **`Abandoned`** after `SessionIdleTimeout` (30 s by default); the fault is lost from the outcome | ✘ **BUG-01** |
| **New Inform from the same CPE within the ~2 s completion grace** | Previous (finished) session `Completed` | Previous session **`Superseded`** | ✘ **BUG-02** |
| Unknown RPC (Upload) from the ACS, simulator direct and via the proxy | 9000 with echoed ID; the session continues to 204 | As expected. The simulator record says `Completed`; the proxy record says `CpeFault` | ✔ (OBS-01) |

Observations (not bugs; each one follows the plan as written):
- **OBS-01**: for the same session the simulator and the proxy report different outcomes when the CPE sent a Fault (`Completed` from the simulator per §3.7.2, `CpeFault` from the proxy per §3.6.4). T3.7 compares only exchange counts and kinds, so this passes. Users may find it confusing in the shared feed. Align them or document it.
- **OBS-02**: an orphan session answered with an empty 2xx stays "in progress" for the full idle timeout (30 s), then shows `Abandoned`.
- **OBS-03**: on this box, scenario #3 (DNS) cannot be verified for real, because every name resolves and an egress proxy answers HTTP 500. `Connect_UnroutableAddress_ConnectTimedOut` accepts `NetworkUnreachable/HostUnreachable` for the same reason.
- **OBS-04**: a CPE that gets a 401 and never retries leaves the session open until the idle timeout → `Abandoned`. This is consistent with "outcome unchanged" (§3.6.5).

## 6. Bugs

### BUG-01 — ACS Fault in answer to an Inform is not treated as the end of the proxy session (**Medium**)
- **Affected**: `src/AcsTool.Proxy/CpeSessionTracker.cs`, `EvaluateEnd` (≈L240–298: `SawAcsFault` is set at L258 but no end rule covers a Fault answer to an Inform) and `Sweep` (L378: the idle path always uses `Abandoned` and ignores `SawAcsFault`).
- **Repro**: `TesterExploratoryTests.AcsFaultToInform_Bug01_SessionEndsAcsFaultPromptly` (skipped; remove the Skip to run). Manually: configure an ACS that answers an Inform with a SOAP Fault (for example 8005 Retry request). The CPE receives the 200 + Fault and stops (TR-069: a fault to Inform terminates the session).
- **Expected**: the proxy session ends promptly as `AcsFault`, as the simulator's own state machine does (PLAN §3.7.2 `Fault → Fail(AcsFault)`).
- **Actual**: `InProgress` for `SessionIdleTimeout` (30 s by default), then `Abandoned`. The fault is not visible in the outcome badge or filter, so the "Inform rejected by the ACS" diagnostic case is reported as a CPE problem. The characterization test `AcsFaultToInform_ActualBehavior_OpenPastGrace_ThenAbandonedNotAcsFault` passes today and documents this.
- Note: PLAN §3.6.4 has no row for this case, so it is partly a plan gap. Suggested rule (for CoderBot to decide): when the ACS answers an Inform with a Fault, end the session as `AcsFault` (or enter the completion grace and end it as `FinalOutcome`).

### BUG-02 — A quick repeat Inform marks the previous, already-finished session `Superseded` (**Low**)
- **Affected**: `src/AcsTool.Proxy/CpeSessionTracker.cs`, `Resolve` L82–88 (supersede loop). A session in `AwaitingFinalEmpty` (finished, waiting out the 2 s grace) is ended as `Superseded` instead of `FinalOutcome(session)`.
- **Repro**: `TesterExploratoryTests.NewInformDuringCompletionGrace_Bug02_PreviousSessionCompleted` (skipped). Manually: in the app, point the simulator through the proxy and click "Send Inform now" twice within about 2 s. The built-in simulator never sends a trailing empty POST, so every proxied simulator session goes through the grace period.
- **Expected**: the first session is `Completed`. **Actual**: `Superseded`. The characterization test `NewInformDuringCompletionGrace_ActualBehavior_PreviousSessionSuperseded` passes today.
- Impact: the wrong outcome badge; the captured data is intact. It can make step W-5 of the Windows checklist look like a failure.

### BUG-03 — The live DNS test reports "Passed" when it actually did nothing (**Low**, test quality)
- **Affected**: `tests/AcsTool.Core.Tests/NetTests.cs` L161 (`Dns_NonexistentInvalidTld_Dns` does a bare `return`). `Connect_UnroutableAddress_ConnectTimedOut` (≈L184) does the same when `ACSTOOL_SKIP_NET_TIMEOUT=1`.
- **Repro**: on this box `getent hosts nonexistent.invalid` → `198.18.0.1`. The test returns early and xUnit reports it as passed.
- **Expected**: the test shows up as **Skipped** with a reason (for example a `SkippableFact` / `Xunit.SkippableFact` package, or a dynamic skip), so the gate does not overstate coverage of scenario #3. **Actual**: counted in the 303/302 passes.

## 7. Manual Windows UI checklist (for you to run; T4.6 and the M4 exit criterion)

Prerequisites: a Windows 10/11 x64 machine, the self-contained publish (`./publish.ps1` → `AcsTool.exe`), and a local HTTP ACS. Any ACS works, or a second machine running FakeAcs. Record pass/fail and screenshots for each step.

| ID | Step | Expected |
|---|---|---|
| W-1 | Start `AcsTool.exe` by double-click | The window opens with **Proxy** and **Simulator** tabs, no error dialog, Light theme, readable at 100% and 150% DPI |
| W-2 | Proxy tab: Listen 0.0.0.0, Port 7547, Upstream = local ACS URL **without a path** → **Start** (or F5) | Status bar "Proxy running"; the bound-URL list shows the LAN IPv4s; ⧉ copies a URL. If Windows Firewall prompts, allowing it works |
| W-3 | Enter an upstream URL **with a path** | The Digest path warning appears. An invalid URL disables Start |
| W-4 | Start a second instance on the same port 7547 | A PortInUse banner with actionable text; no crash |
| W-5 | Simulator tab: new TR-098 profile, ACS URL `http://127.0.0.1:7547/acs`, save, start, **Send Inform now** (click **once**) | The feed shows a Proxy session and a Simulator session for the same device. Both end `Completed`; the proxy one within about 3 s. (Clicking twice within 2 s shows `Superseded`: BUG-02) |
| W-6 | Select a session: Summary, Headers, Raw tabs | Raw shows the XML; the **Pretty** toggle formats it; Authorization is masked until **Reveal Authorization** is ticked; Copy request/response put the text on the clipboard |
| W-7 | Repeat W-5 with a TR-181 profile and with ACS Basic or Digest credentials set on the profile | The session succeeds through the proxy; one proxy session holds the 401 challenge + 200 |
| W-8 | Stop the ACS, then Send Inform now | The proxy session shows `UpstreamError` (Connect) and the simulator CPE row shows the Error state; a banner/error sub-line is shown; no modal dialog |
| W-9 | Feed: Filter text, Outcome filter, ⏸ Pause/resume, ⟲ Clear, Auto-scroll | Each control works; Clear removes only that tab's source |
| W-10 | ⚙ Settings: change a proxy/capture value, OK, restart the app | The value persists (`%APPDATA%\AcsTool`). Profile credentials are stored DPAPI-protected, not in plain text, in the profile JSON |
| W-11 | Upstream `http://nonexistent.invalid:7547` (real DNS, no sinkhole) → Send Inform now | CPE gets 502; session `UpstreamError` with kind **Dns** (scenario #3, not verifiable on Linux box) |
| W-12 | Optional: ACS that answers the Inform with a SOAP Fault | Today the session stays in progress for about 30 s, then shows `Abandoned` (BUG-01). After a fix: `AcsFault` |
| W-13 | Close the window while the proxy and a CPE are running | Both stop; the process exits within a few seconds (check Task Manager); the log exists in `%LOCALAPPDATA%\AcsTool\logs\acstool-YYYYMMDD.log` and contains no passwords or Authorization values |
| W-14 | PowerShell: `$p = Start-Process .\AcsTool.exe -ArgumentList '--smoke-exit' -Wait -PassThru; $p.ExitCode` | Prints `0` |

## 8. Quality gate decision (M0–M4)

| Gate | Result |
|---|---|
| `./build.sh` green on Linux with `CI=true` (0 warnings) | ✔ |
| All automated tests pass in Debug and Release, no flakes in 4 Debug runs | ✔ (470 pass, 2 intentional bug skips) |
| Coverage ≥80% line for Core/Proxy/Simulator | ✔ (87.2 / 90.2 / 89.8) |
| Mandatory MVP scenarios covered (excluding OOS #5, #12, #13, #15) | 10/11 covered; #3 partial (environment) |
| No open Critical/High bugs | ✔ (1 Medium, 2 Low) |
| Windows manual smoke (T4.6) | ✘ not run (needs you) |

**Decision: CONDITIONAL PASS.** Accept M0–M3. Accept M4 once checklist W-1…W-14 passes on Windows, with W-11 closing scenario #3.
Fix BUG-01 before or at the start of M5, and BUG-02/BUG-03 when convenient. When a bug is fixed, un-skip its
`*_Bug0x_*` test and delete or flip its `*_ActualBehavior_*` characterization test.

## 9. Files added by TesterBot (branch `tester/m0-m4`)
- `tests/AcsTool.Proxy.Tests/TesterExploratoryTests.cs`: 22 tests (20 active, 2 skipped bug repros).
- `tests/AcsTool.Simulator.Tests/TesterScenarioTests.cs`: 2 tests (scenario #11 integration, direct and via the proxy).
- `docs/TEST_REPORT_M0-M4.md`: this report.

---

## 10. Fix branch dfae3b3 (`fix/review-m0-m4`)

Re-verification date: 2026-10-29 · Tested commit: **`dfae3b3`** + TesterBot tests/docs (cherry-picked from `6a8c873`, no `src/` change),
on branch `tester/m0-m4-fix`, in a separate worktree (`/workspace/projects/acs-tr069-tool-fixtest`), ephemeral ports, no processes left running.
The fix branch contains 15 commits on top of `b67eb19`: review fixes M1–M6, m1, m5, m6, m8, m10–m14 (m12 partly), the user-reported
clipped-tab layout fix (`c8c0861`), and docs (`dfae3b3`).

### 10.1 Results (actual output, clean `bin/obj`)

| Command | Presentation | Core | Simulator | Proxy | Total (passed/failed/skipped) | Warn/Err | Wall |
|---|---|---|---|---|---|---|---|
| `dotnet build AcsTool.sln -c Release` | | | | | success | 0 / 0 | 5 s (4.75 s MSBuild) |
| `dotnet test AcsTool.sln -c Release --no-build` | 45/0/0 | 309/0/0 | 65/0/0 | 92/0/1 | **511 / 0 / 1 (512)** | — | 9 s |
| `CI=true ./build.sh` (clean tree, coverage) | 45/0/0 | 308/0/0 | 65/0/0 | 92/0/1 | **510 / 0 / 1 (511)**, exit 0 | 0 / 0 | 15 s |

- The only skip is `TesterExploratoryTests.AcsFaultToInform_Bug01_SessionEndsAcsFaultPromptly` (BUG-01 is still open).
- TesterBot tests included: 21 in `TesterExploratoryTests.cs`, 2 in `TesterScenarioTests.cs`, and 5 new cases in `TesterFixBranchTests.cs` (4 methods, one is a 2-case theory).
- Changes to the TesterBot tests for this branch:
  - The BUG-02 repro `NewInformDuringCompletionGrace_Bug02_PreviousSessionCompleted` is un-skipped and **passes**.
  - Its characterization test `…_ActualBehavior_PreviousSessionSuperseded` failed as expected (`Expected: Superseded, Actual: Completed`) and was removed.
  - When un-skipped, the BUG-01 repro **still fails** (`still InProgress after 5 s`), so it stays skipped with an updated reason.
- Line coverage: Core 87.0%, Proxy 89.6%, Simulator 89.7%, Presentation 85.3% (NFR-09 is met).

### 10.2 Bug status

| ID | Title | Severity | Status on dfae3b3 | Evidence |
|---|---|---|---|---|
| BUG-01 | ACS SOAP Fault in answer to an Inform → proxy session `Abandoned` after the idle timeout instead of `AcsFault` | Medium | **OPEN** | No change in `EvaluateEnd`/`Sweep` for this case. The repro test fails; `AcsFaultToInform_ActualBehavior_OpenPastGrace_ThenAbandonedNotAcsFault` still passes. It is not in the review or the fix commits. |
| BUG-02 | A new Inform during the 2 s completion grace marks the finished session `Superseded` | Low | **FIXED** (`d51bb45`, review M3) | `CpeSessionTracker.Resolve` now ends an `AwaitingFinalEmpty` session with `FinalOutcome(old)`. The repro passes. Guards added: a session that is genuinely mid-session is still `Superseded` (✔), and grace after a CPE Fault → `CpeFault` (✔). |
| BUG-03 | `NetTests.Dns_NonexistentInvalidTld_Dns` reports Passed while doing nothing when DNS is hijacked | Low (test quality) | **OPEN** | `NetTests.cs` still does a bare `return` (now around L161). Scenario #3 is still only partly verified on Linux (DNS sinkhole). |

### 10.3 Exploratory checks on the fixed findings

| Fix | Probe (independent of CoderBot's tests) | Result |
|---|---|---|
| M3 / BUG-02 | Two quick sessions without a trailing empty POST; mid-session supersede guard; grace after a CPE Fault | ✔ `Completed`; `Superseded`; `CpeFault` |
| m1 | Proxy stopped while the 2nd exchange (empty POST) is waiting on a 10 s upstream delay | ✔ Session `Canceled`; the 1st exchange keeps no error |
| m13 | Upstream Set-Cookie `Path=/` and `Path=/acs` with the CPE posting to `/acs` (negative case for the rewrite) | ✔ Relayed unchanged, no Path note (CoderBot's `SetCookiePath_ForUpstreamBasePath_RewrittenSoCpeReplaysIt` covers the positive case) |
| m10, m11, m12, m5, m6, m8, M1, M2, M4–M6 | Reviewed the commit diffs and the matching new tests (`UpstreamUrlWithCredentials_NotForwarded_RedactedInSessionTarget`, `ProxyStoppedDuringInFlightExchange_SessionCanceled`, `Start_Localhost_BindsLoopback`, `Start_UnexpectedFailure_FaultedAndCleanedUp_ThenRestartable`, `Start_CustomCaFileMissing_ProxyStartExceptionWithClearMessage`, feed-pump and view-model tests) | All pass in the runs above. No extra probes (out of scope for this lean pass) |

### 10.4 Tab layout: static review of `c8c0861` (rendering can't be done on Linux)

What changed:
- `MainWindow`: MinWidth 1100→640, MinHeight 700→480, `ResizeMode=CanResize`. Each `TabItem` hosts a `ScrollViewer` (both bars on Auto). The view's `Width`/`Height` are bound to the ScrollViewer's `ViewportWidth`/`ViewportHeight`, with MinWidth/MinHeight (Proxy 760×420, Simulator 900×760).
- `SettingsWindow`: resizable (min 420×320); both tabs scroll vertically (horizontal scrolling disabled).
- `SimulatorView`: the profile editor has its own vertical ScrollViewer (DockPanel MinHeight 520). Fixed pixel widths became `*` + MinWidth. Toolbars changed from `StackPanel` to `WrapPanel`.
- `ProxyView`: fixed widths → MinWidth; the button row is a `WrapPanel`.
- `XamlLayoutTests` (3 tests, all pass) lint these properties.

Assessment:
- The viewport-binding pattern is a sound way to get "fill when large, scroll when small" while keeping star rows and DataGrid virtualization. The lint tests confirm the structure but not the rendered result.
- **NEW-01 (Low, needs manual confirmation): the Simulator tab probably needs vertical scrolling at the default window size.**
  - Cause: the Simulator view has `MinHeight="760"`. With a default window `Height="820"`, the tab viewport is roughly 690 px at 100% DPI (820 minus title bar/border ≈ 39, header row ≈ 34, status bar ≈ 23, tab strip and margins ≈ 34; my estimate, not measured).
  - Effect: a vertical scrollbar at startup, and the bottom of the session feed/detail is cut off until the user scrolls. On a 1366×768 laptop or at 150% DPI it gets clearly worse.
  - Repro: on Windows, start the app at its default size and open the Simulator tab.
  - Expected: no scrollbar at the default size. Actual (predicted): a vertical scrollbar.
  - Location: `src/AcsTool.App/Views/MainWindow.xaml` (SimulatorView MinHeight 760) vs the Window Height 820.
  - Suggested fix (for CoderBot): lower MinHeight to about 600, or raise the default window height.
- **NEW-02 (Low, pre-existing, needs manual confirmation): the Simulator tab's horizontal `GridSplitter` (row 1) sits between the editor row (`3*`) and the `Auto` "Running CPEs" row (row 2), not the feed row (row 3).**
  - Effect: dragging it resizes the editor against the running-CPE strip (whose DataGrid has `MaxHeight` 140) and turns the Auto row into a fixed pixel height. The feed/detail area cannot be enlarged with the splitter.
  - Location: `src/AcsTool.App/Views/SimulatorView.xaml` (row definitions and the GridSplitter at `Grid.Row="1"`).
  - Expected: the splitter trades space between the editor and the feed/detail.
- Points to confirm by hand: whether the mouse wheel over a DataGrid scrolls the grid (expected) and the page when the grid reaches its end; no scrollbar flicker when resizing near the MinWidth/MinHeight thresholds (the viewport-binding feedback loop); a 2-scrollbar nesting on the Simulator tab (outer page + inner editor) that might confuse users.

### 10.5 Updated Windows UI checklist (adds to §7; run W-1…W-14 first)

Changes from §7:
- **W-5**: clicking "Send Inform now" twice within 2 s should now give `Completed` for both sessions (BUG-02 fixed).
- **W-12** still shows BUG-01 (`Abandoned` after about 30 s).

| ID | Step | Expected |
|---|---|---|
| W-15 | Start at the default size (1280×820) at 100% DPI; open each tab | Proxy tab: no scrollbars. Simulator tab: note whether a vertical scrollbar appears (NEW-01) and how much is hidden |
| W-16 | Resize the main window slowly down to its minimum (640×480), then maximize | Content is never clipped without a scrollbar. Scrollbars appear only below the view minimum (Proxy 760×420, Simulator 900×760) and disappear when larger. No flicker or jitter near the thresholds. Feed and detail grids grow with the window when maximized |
| W-17 | At a small size: scroll each tab with the scrollbars and the mouse wheel (over empty space, over the feed DataGrid, over the parameters grid) | Every control can be reached, including the Start button, "Send Inform now", the Raw/Pretty tab and the status bar. The wheel scrolls the grid under the cursor; the page scrolls elsewhere |
| W-18 | Simulator tab: select a profile in a short window | The profile editor scrolls on its own; the parameters grid keeps at least about 140 px; toolbar buttons wrap instead of being cut off |
| W-19 | Simulator tab: drag the horizontal splitter under the editor | Note which areas resize (NEW-02: expected editor ↔ feed; predicted editor ↔ Running CPEs strip) |
| W-20 | Proxy tab at a narrow width (about 800 px) | The listen/port/upstream/TLS/cookies fields and the Start/bound-URL row wrap onto further lines; nothing overlaps |
| W-21 | Settings window: resize down to 420×320 and back; check both pages | Vertical scrolling works; labels are not cut off; OK/Cancel stay visible |
| W-22 | Repeat W-15…W-17 at 150% DPI (or on a 1366×768 display) | The window fits the work area or can be resized to fit; tabs scroll instead of clipping |

### 10.6 Verdict for the fix branch

**APPROVE MERGE of `fix/review-m0-m4` (dfae3b3).** There are no blockers:
- the build and all tests are green in Release and under `CI=true` with 0 warnings;
- the review fixes I probed behave correctly;
- BUG-02 is fixed;
- there are no Critical or High findings.

Still open, but not blocking: BUG-01 (Medium; fix it before or at the start of M5), BUG-03 (Low), NEW-01 and NEW-02 (Low; UI, need confirmation on Windows).
M4 acceptance remains **conditional** on the Windows manual checklist (W-1…W-22). The tab layout fix in particular has only been linted, never rendered.
