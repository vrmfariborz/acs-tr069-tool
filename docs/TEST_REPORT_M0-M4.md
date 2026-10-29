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

## 11. .NET 10 migration `chore/net10` (head `42e3723`)

Verified by TesterBot on 2026-10-29 in a separate worktree (branch `tester/net10`, based on `42e3723`). `bin/obj` were cleaned before each run. Box: Linux, 8 cores, SDKs 8.0.427, 9.0.320 and 10.0.403.

### 11.1 Results (actual output)

| Step | Result |
|---|---|
| `dotnet --version` (global.json 10.0.403, rollForward latestFeature) | `10.0.403` |
| `dotnet build AcsTool.sln -c Release` | 0 Error(s), 0 Warning(s) |
| `dotnet test -c Release` (on `42e3723`) | Core 312/0/1, Proxy 97/0/0, Simulator 67/0/0, Presentation 54/0/0, so **530 passed / 0 failed / 1 skipped** (the skip is the live DNS test, OBS-03) |
| `CI=true ./build.sh` (on `42e3723`) | 0 warnings; 529 / 0 / 1 (Core 311 + 1 skipped; coverage run) |
| `CI=true ./build.sh` with the tests from 11.4 | 0 warnings; Core 322/0/3, Proxy 99/0/0, Simulator 67/0/0, Presentation 54/0/0, so **542 / 0 / 3** |
| `dotnet list package --vulnerable` | none |
| `dotnet list package --deprecated` | `xunit 2.9.3` "Legacy", alternative `xunit.v3` (informational only) |
| No SYSLIB*, NU1510 or CA* warnings under AnalysisLevel 10.0-recommended | confirmed |
| Stress: Engine + EndToEnd + TesterScenario tests (11 tests), 10 sequential runs | 10/10 green, 43 s in total |
| Stress: 16 busy loops on 8 cores, 2 rounds × 3 parallel processes | 6/6 green (loadavg 9.45 → 12.46), 15 s |
| `dotnet publish src/AcsTool.App -c Release -r win-x64 --self-contained true` (from Linux) | exit 0, 0 warnings; 169 MB, 409 files; `AcsTool.exe` 162,816 B; runtimeconfig `includedFrameworks` NETCore/WindowsDesktop/AspNetCore 10.0.14; `System.Security.Cryptography.ProtectedData.dll` is bundled; no trim, single-file or R2R settings, so no IL warnings |

### 11.2 Custom CA matrix (openssl 3.5.7 files, driven through the real code)

A throwaway harness called `TlsValidatorFactory.LoadCertificate` and `ProxyServer.StartAsync` (via `CpeSessionTracker.PrepareTls`). As a comparison it also called the obsolete `new X509Certificate2(byte[])`, which the pre-migration code used for non-PEM files. "Validates" means: the loaded cert used as the only `CustomRootTrust` anchor validates `leaf ← intermediate ← root`.

| File | net10 loader | Validates | Proxy start | Old byte[] ctor |
|---|---|---|---|---|
| root PEM / `.crt` / DER / CRLF PEM | OK, root | yes | Running | OK |
| OpenSSL `BEGIN TRUSTED CERTIFICATE` | OK, root | yes | Running | OK |
| PFX, no password (key+cert / cert only / `-legacy` RC2) | OK, root | yes | Running | OK |
| PFX, password `secret` | CryptographicException: *The certificate data cannot be read with the provided password, the password may be incorrect.* | – | ProxyStartException: *Custom CA file '…' could not be loaded: The certificate data cannot be read with the provided password, the password may be incorrect.* | same CryptographicException |
| PFX leaf + int + root, no password | OK, **root** (a CA is preferred) | yes | Running | OK, **leaf** (behavior change, an improvement) |
| PEM bundle root, then intermediate | OK, root | yes | Running | OK |
| PEM bundle intermediate, then root | OK, **intermediate** | **NO** | Running | OK, intermediate (NET10-02) |
| key PEM, then cert PEM | OK, root | yes | Running | OK |
| leaf cert as "CA" | OK, leaf | NO | Running (no warning) | OK |
| UTF-8 BOM + PEM | CryptographicException: *The certificate contents do not contain a PEM with a CERTIFICATE label, or the content is malformed.* | – | ProxyStartException (same text) | **OK** (NET10-01) |
| garbage / whitespace only / truncated DER | CryptographicException: *ASN1 corrupted data.* | – | ProxyStartException | same |
| truncated PEM / corrupted base64 | CryptographicException: *…do not contain a PEM with a CERTIFICATE label, or the content is malformed.* | – | ProxyStartException | ASN1 corrupted data |
| empty file (0 bytes) | CryptographicException: *ASN1 corrupted data.* | – | ProxyStartException | **NullReferenceException** (escaped PrepareTls' catch before; fixed by the migration) |
| missing file | FileNotFoundException | – | ProxyStartException: *…Could not find file '…'.* | same |

There were no crashes. Every invalid input becomes a `ProxyStartException` with the file path.

### 11.3 Findings

- **NET10-01 (Low/Medium, pre-existing; the PEM path is unchanged since net8).** A PEM file that starts with a UTF-8 BOM is rejected. Windows editors can save one this way. Fix idea: strip a BOM, or decode as UTF-8, before the PEM sniff. Repro: `TesterCaMatrixTests.Utf8BomPem_Loads` (skipped).
- **NET10-02 (Low/Medium, pre-existing).** Only the first certificate of a PEM bundle is used. An intermediate-first `ca-bundle` loads without error, the proxy starts, and every TLS handshake then fails the chain. Leaf-as-CA behaves the same way, silently. Fix idea: `X509Certificate2Collection.ImportFromPem` and add all of them to `CustomTrustStore`, and/or warn when no self-signed CA is present. On Linux, a non-self-signed anchor in CustomTrustStore did not validate. Repro: `PemBundle_IntermediateFirst_TrustsTheRoot` (skipped).
- **NET10-03 (Low).** The password-protected PFX error says "with the provided password", but the UI has no CA password field. Suggest a hint such as "password-protected PFX is not supported; export the CA as PEM/DER".
- **NET10-04 (Low).** The PFX pick is "first certificate with CA=true". For leaf+int+root the root came out first here, but if a file lists the intermediate first, the intermediate would be picked (see NET10-02). Prefer a self-signed CA. `LoadPkcs12Collection` is called without `X509KeyStorageFlags.EphemeralKeySet`. On Windows that may persist the PFX private keys of the CA file, even though only the public anchor is needed. Check this on Windows (W-24).
- **NET10-05 (Info).** xunit 2.9.3 is flagged deprecated ("Legacy", successor xunit.v3). This isn't a blocker.
- **DPAPI review.** `DpapiSecretProtector` is unchanged: the same `"dpapi:"` prefix, entropy `AcsTool.v1` and `CurrentUser` scope. Only the PackageReference was removed. On net10.0-windows the assembly comes from Microsoft.WindowsDesktop.App. The self-contained publish bundles it, and a framework-dependent install needs the Desktop Runtime anyway. There is no PlatformNotSupported path. The App is Windows-only, and Core/tests use `PlainSecretProtector` via `TryAddSingleton`. The blob format is the OS's `CryptProtectData`, so values saved by the net8 build should decrypt. This **cannot be verified on Linux** (W-23).

### 11.4 Tests added (branch `tester/net10`)

- `tests/AcsTool.Core.Tests/TesterCaMatrixTests.cs` contains 13 tests, 11 active and 2 skipped repros (NET10-01/02). Both repros were confirmed to fail when un-skipped.
  - Covered: password PFX, empty, whitespace, truncated PEM/DER, CRLF, key-before-cert PEM, root-first bundle, leaf+chain PFX picks a CA, cert-only PFX, and leaf-as-CA characterisation.
- `ProxyServerTests.Start_CustomCaFileEmptyOrGarbage_ProxyStartExceptionWithClearMessage` (2 cases).

### 11.5 Windows smoke checklist additions for .NET 10 (run after W-1…W-22)

| # | Step | Expected |
|---|---|---|
| W-23 | With the **previous (net8) build**, save an upstream/Digest password in a profile and in settings. Close it, install or run the net10 build as the same Windows user, and reopen | Passwords are shown/used unchanged (the Digest 401 flow still works). There is no "secret could not be decrypted" banner. `%APPDATA%\AcsTool` files still hold `dpapi:` values. Save again and reopen in net10: round trip OK |
| W-24 | Proxy → TLS mode Custom CA with root CA as PEM, DER (`.cer`), and password-less PFX, against an HTTPS upstream signed by that CA | Each format starts and the TLS handshake succeeds. For PFX, `certmgr.msc` / `%APPDATA%\Microsoft\Crypto\RSA` gains no new persisted key (NET10-04) |
| W-25 | Custom CA files that should fail: password-protected PFX, an empty file, and a PEM saved by Notepad as "UTF-8 with BOM" | A clear start error with the file path and no crash. The BOM case currently fails (NET10-01). The PFX message mentions a password (NET10-03) |
| W-26 | Custom CA = an `intermediate, root` PEM bundle | Currently: the proxy starts, then the TLS chain fails (NET10-02). Record the exact UI/log text |
| W-27 | Copy `out/win-x64` (from `publish.ps1`) to a **clean Windows 10/11 machine or VM without any .NET installed**. Run `AcsTool.exe`, then `AcsTool.exe --smoke-exit` | The app window opens with no "install .NET" prompt. `--smoke-exit` returns 0. Logs appear under `%LOCALAPPDATA%\AcsTool\logs` |
| W-28 | On the same machine: start the proxy, bind a port, pass the Windows Firewall prompt, and run one simulator session | It works without any .NET installation (Kestrel/AspNetCore bundled) |
| W-29 | File → About (or the log header) | Runtime reported as .NET 10.0.x |
| W-30 | At 150% DPI with a dark/high-contrast Windows theme | WPF on net10 renders as before (no new theme regressions; the app keeps the Light theme) |

### 11.6 Verdict

**PASS (conditional on W-23…W-28 on Windows).** The build, tests, CI, stress and self-contained publish are clean on .NET 10. The CA loader migration did not regress any format the old code accepted, apart from the pre-existing BOM/bundle issues (NET10-01/02, not caused by the migration). It improves the empty-file case and leaf+chain PFX handling. DPAPI compatibility and the clean-machine launch can only be proven on Windows.

## 12. Doc-review fixes `fix/doc-findings` (`8d8c10c..b8d33bb`) and the FeedPump flake

Verified by TesterBot on 2026-10-29 in separate worktrees. Branch `tester/doc-findings` is based on `b8d33bb`. `bin/obj` were cleaned before each run.

### 12.1 Results on `b8d33bb` (actual output)

| Step | Result |
|---|---|
| `dotnet build AcsTool.sln -c Release` | 0 Error(s), 0 Warning(s) |
| `dotnet test -c Release` | Core 323/0/3, Proxy 102/0/0, Simulator 72/0/0, Presentation 57/0/0, so **554 passed / 0 failed / 3 skipped** (the DNS test plus the NET10-01/02 repros) |
| `CI=true ./build.sh` | 0 warnings; **553 / 0 / 3** (Core 322 + 3 skipped) |
| `CI=true ./build.sh` with the tests below | 0 warnings; Core 322/0/3, Proxy 102/0/0, Simulator 97/0/0, Presentation 59/0/1, so **580 / 0 / 4** (the extra skip is the FLAKY-01 repro) |

*Update 2026-10-29: the FLAKY-01 repro is no longer skipped (fixed on main, see 12.4).*

### 12.2 Probes

**(a) Simulator Fault classification.** `TesterDocFindingsTests` runs over real HTTP (FakeAcs + HttpCwmpTransport), both directly and simulator → proxy → FakeAcs.

| ACS answer to the Inform | Simulator | Proxy (same exchange) |
|---|---|---|
| 500 + cwmp Fault (`text/xml`) | AcsFault, note `ACS fault 8005 …` | AcsFault |
| 500 + Fault with `text/html` / no Content-Type | AcsFault | AcsFault |
| 500 + plain SOAP Fault (no cwmp detail) | AcsFault (note `ACS fault 0 `) | AcsFault |
| 500 empty / 500 HTML | HttpError | HttpError |
| 200 + Fault | AcsFault | AcsFault |
| 407 + Fault / 407 empty | HttpError | HttpError |
| 401 + Fault (no credentials) | AuthFailed, note `ACS rejected the credentials (401)` | – |
| 401 Digest, wrong password | AuthFailed | – |

After an InformResponse, a 500 Fault, a 500 empty answer or a 407 Fault to the empty POST gives HttpError, and a 401 gives AuthFailed. In every failure case the CPE state is `Error` with Retry 1.

**(d) Restore defaults.** `TesterDocFindingsSettingsTests`:
- Restore defaults + Save resets every proxy and capture field the window shows.
- It keeps `proxy.tls` (mode, pins, CA path), `maxRequestBodyBytes`, and the `simulator`, `ui` and `logging` sections.
- The Proxy tab follows the change: port, upstream and cookies are reset, and the TLS drop-down keeps the hand-set mode, e.g. **IgnoreErrors**.
- Cancel after Restore defaults discards the reset.
- Every field that gets reset has a binding in `SettingsWindow.xaml`.

**(f)** The message is now *TLS mode CustomCa needs proxy.tls.customCaPath in settings.json.* (Proxy test added by the branch.)

**(g)** The Summary tab shows a **Proxy notes** list for the selected exchange (`HasNotes` → `BoolToVisibility`, a key defined in `Themes/Light.xaml`). Simulator exchanges never carry exchange notes (`CompleteExchange` without notes), so the label never appears on a simulator session. The XAML is only linted on Linux, never rendered.

### 12.3 Findings

- **DF-01 (Low, pre-existing).** For a SOAP Fault without a cwmp `detail`, the note reads `ACS fault 0 ` (code 0, empty string). The SOAP `faultcode`/`faultstring` (`soap:Server Internal error`) are parsed but not shown. This affects the 2xx path too.
- **DF-02 (Info).** Restore defaults keeps the TLS **mode** too, so `IgnoreErrors` survives a "restore defaults". This is consistent with "the window doesn't edit TLS", but the user guide (`proxy-mode.md:108`, `settings-reference.md:177`, `troubleshooting.md:101`, `ui-tour.md:300`) still says it resets `tls`.
- **DF-03 (Low, pre-existing, observation).** In the simulator, a TransferComplete exchange ignores the HTTP status: a 401 or 500 only gives the note "TransferComplete not acknowledged", and the session continues with the empty POST.
- **Docs to update when this branch merges** (`docs/user-guide`, 329062b, documents the old behavior as "known issue, fix planned"):
  - `simulator-mode.md:194,233`
  - `proxy-mode.md:27,108,125,137,143,145,157,199,301,310` ("not shown in the UI yet")
  - `troubleshooting.md:13,58,94–107`
  - `roadmap.md:19–26`
  - `settings-reference.md:177–181`
  - `ui-tour.md:300`

### 12.4 FLAKY-01: `FeedPumpTests.TenThousandChangesOverOneSimulatedSecond` (Medium, test reliability)

**Stress results** (`b8d33bb`, Release):

| Run | Result |
|---|---|
| 50 sequential runs | 50/50 green |
| 16 busy loops on 8 cores (loadavg 21–25), 5 rounds × 4 parallel processes | **2 of 20 failed** |
| Full Presentation assembly ×10 (2 parallel processes, same load) | 10/10 green |

Exact failure texts:
- `Assert.All() Failure: 49 out of 50 items in the collection did not pass. … Error: The given key '…' was not present in the dictionary.` This is round 0: `firstInstances` was incomplete.
- `Assert.All() Failure: 48 out of 50 items in the collection did not pass. … Assert.Equal() Failure: Collections differ … Expected: ["n9997"] Actual: ["n8997"]`. This is round 10: the last batch missed that round's changes.

**Cause.** In `SessionFeedPump.CollectAndFlushAsync`, `FakeTimeProvider.Advance` completes the flush deadline **inline on the test thread**. The channel's `WaitToReadAsync` task completes too, but its continuations are asynchronous, so the signal reaches `Task.WhenAny(more, deadline)` only through a thread-pool work item. When the pool is busy, the deadline wins and the loop `break`s **without a final `Drain`**. Changes written before `Advance` then go into the *next* batch.

In production this only delays an update by one flush (harmless). The test assumes, without waiting for it, that everything written before `Advance` is in that flush. Its `WaitUntilAsync(() => pump.IsCollecting)` returns at once, because the pump is usually already collecting.

**Deterministic repro:** `TesterFeedPumpRaceTests` (skipped, FLAKY-01). It holds the pool's workers for a moment, writes one change, then calls `Advance`. 5/5 runs fail with *"after dispatch 1 the feed has [00D09E-EARLY]; LATE missed the flush"*.

**Fixes:**
- **Proposed fix (src, for CoderBot):** one line, `Drain(reader, pending, ref cleared);` after the collect loop and before `flushCts.CancelAsync()`. In a throwaway worktree (deleted afterwards) the repro and all 8 FeedPump tests passed 5/5 with it, and `CI=true` built Presentation with 0 warnings.
- **Alternative (test only):** before each `time.Advance`, wait for `store.Changes.Count == 0` (the bounded channel can count).

> **Update 2026-10-29:** the proposed fix landed on main (`3131a48`, merged in `93980dc`) and `TesterFeedPumpRaceTests` is **no longer skipped**; it runs and passes in every build. The skip counts above are as recorded at `b8d33bb`. Current skips are the NET10-01/02 repros, plus the DNS test where every name resolves. On `fix/timeout-test-hardening` the live client-timeout tests also gained a 30 s hang guard and elapsed-time bounds (the never-responding-server test: 2.5–20 s for its 3 s timeout).

### 12.5 Windows smoke checklist additions (after W-1…W-30)

| # | Step | Expected |
|---|---|---|
| W-31 | Proxy to an ACS whose `Set-Cookie` has `Domain=` and `Path=/tr069`, CPE posts to `/acs`. Select the InformResponse exchange → **Summary** | A **Proxy notes** heading with bullets `• Set-Cookie Domain stripped` and `• Set-Cookie Path /tr069 rewritten to / (CPE posts to /acs)`. On an exchange without notes, no heading and no empty gap |
| W-32 | Digest through the proxy (401 → retry) → exchange 1 **Summary** | `• AuthChallenge` in the notes list; long notes wrap in a narrow detail pane |
| W-33 | Select a **simulator** session's exchanges → Summary | No "Proxy notes" heading |
| W-34 | Hand-edit `settings.json`: `proxy.tls` = IgnoreErrors + pins + `customCaPath`, `maxRequestBodyBytes` = 2000000. Start the app → Settings → **Restore defaults** → **Save** → close → reopen `settings.json` | `tls` and `maxRequestBodyBytes` are unchanged. Port/upstream/capture are back to defaults. The Proxy tab TLS drop-down still shows IgnoreErrors (DF-02) |
| W-35 | Settings → Restore defaults → **Cancel** | Fields show the previous values again; nothing is written |
| W-36 | `proxy.tls.mode` = `CustomCa` with `customCaPath` null → ▶ Start | Error banner *TLS mode CustomCa needs proxy.tls.customCaPath in settings.json.* |
| W-37 | Simulator → an ACS that answers the Inform with HTTP 500 + SOAP Fault, while the proxy is in between | Both feeds show **⚠ ACS fault**; the simulator note is `ACS fault <code> <string>`; Running CPEs row `Error`, Retry 1 |

### 12.6 Verdict

**`fix/doc-findings` (b8d33bb): PASS.** Merge it together with, or before, a docs update; see the list in 12.3. FLAKY-01 is pre-existing and doesn't block. A one-line src fix is proposed.
