# Implementation notes (MVP: M0–M4)

Status, deviations from `docs/PLAN.md` and known gaps. Every deviation includes the reason for it.

## Status per milestone

| Milestone | Status | Main contents |
|---|---|---|
| M0 (T0.1–T0.3) | done | Solution, `Directory.Build.props` with a single `AcsToolTfm` (net8.0 at the time; net10.0 since the .NET 10 migration), central package management, analyzers (`latest-recommended`, warnings become errors when `CI=true`), `build.sh`/`build.ps1`/`publish.ps1`, GitHub CI (Linux build+test; Windows build+publish+`--smoke-exit`). |
| M1 (T1.1–T1.8) | done | `AcsTool.Core`: CWMP model and namespaces, tolerant parser, summarizer, deterministic builders (InformBuilder), in-memory session store with change feed, AuthRedactor, net helpers (UpstreamSender, ErrorClassifier, PhaseTracker, TLS, connect callback), settings and persistence. |
| M2 (T2.1–T2.6) | done | `AcsTool.Proxy`: Kestrel `ProxyServer`, `CpeSessionTracker`/`ProxySession`, `HeaderRewriter`, `UpstreamForwarder`, `ProxyRequestHandler`, DI. `AcsTool.Testing.FakeAcs` with Basic/Digest auth, cookies, delays, status codes and abort. |
| M3 (T3.1–T3.7) | done | `AcsTool.Simulator`: profiles, validator, JSON profile store, parameter store, HTTP transport, RPC dispatcher (GRM/GPN/GPV/SPV), client session state machine, `SimulatedCpe` and `SimulationEngine`, sim → proxy → FakeAcs end-to-end test. |
| M4 (T4.1–T4.6) | done (code + automated tests); manual Windows smoke not run here | `AcsTool.Presentation` (UI-free view models, `SessionFeedPump`, banners and abstractions) and the `AcsTool.App` WPF shell (Generic Host, Serilog file log, DPAPI secrets, views, Light theme, `--smoke-exit`). |

## Deviations

### Build and tooling
- `global.json` requires SDK 10.0.403 or newer within 10.0.x (`rollForward: latestFeature`). Until the .NET 10 migration it required 8.0.427+; before the CI fix below it used `latestMajor`, which let SDK 10 analyzers in. The TFM is net10.0 through `AcsToolTfm` (see "Migration to .NET 10").
- NSubstitute is not used; the tests rely on hand-written fakes (`FakeAcs`, `FakeTransport`, `RecordingSink`, TestKit). AvalonEdit is not used either; the MVP raw and pretty views are read-only monospace `TextBox`es. Reason: fewer dependencies, and the fakes were simpler and more deterministic.
- Some CA rules are suppressed in `.editorconfig`:
  - Product code: CA2007 (ConfigureAwait; the library convention is enforced by review), CA1848/CA1873 (LoggerMessage delegates), and CA1707/CA1711/CA1716/CA1720 (naming of CWMP domain types such as `Fault`, `Event`, `...Stream`).
  - Tests only: CA1707, CA2007, CA1001, CA1861, CA2000, CA5394.
  - Reason: these rules are noise for this code base. All other `latest-recommended` rules stay on, and the build has 0 warnings.
- `build.sh` excludes `Category=Perf` tests by default (`--include-perf` runs them). That is why Core.Tests runs 302 tests through the script and 303 through plain `dotnet test`.
- The CI Windows smoke step uses `Start-Process -Wait -PassThru`. Reason: `AcsTool.exe` is a WinExe, and PowerShell neither waits for it nor checks its exit code.

### Proxy (M2)
- Request headers are forwarded in Kestrel's order (not wire order). `Expect` is not forwarded. Kestrel `MaxRequestBodySize` is `null`, and the handler enforces `MaxRequestBodyBytes` itself so the 413 is recorded.
- The end-of-session rules run before the response is relayed to the CPE. The per-session `Gate` semaphore is never disposed, so a late `Release` stays safe.
- **End rule extension (§3.6.4).** The plan ends a session only on an empty ACS response to an empty CPE POST. Many CPEs close the connection once the ACS answers their last RPC response with 204 (they already sent their empty POST). Others send one more empty POST, as in the plan's T2.5 flow. The proxy now handles both:
  - `ProxySession.CpeSentEmpty` records that the CPE has sent an empty POST.
  - When the ACS then answers a CPE response with an empty body, the session goes into `AwaitingFinalEmpty`.
  - A trailing empty POST ends the session at once.
  - Otherwise the sweeper ends it as Completed/AcsFault/CpeFault after `CompletionGrace` = 2 s of idle time. The sweeper runs every 1 s (the plan said 5 s; shortened for this rule), so the UI can show "in progress" for up to about 3 s.
- The T2.5 acceptance test says "5 exchanges", but the flow it describes has 4 HTTP requests: Inform, empty, GPVResponse, empty. The test asserts those 4, in order.
- Set-Cookie rewriting was pulled forward from M5: `Domain` is stripped; the Secure flag is stripped only when the CPE leg is HTTP and the upstream is HTTPS; a `Path` that the CPE's request path would not match (upstream base path or `PathMode=Fixed`, e.g. `Path=/tr069` while the CPE posts to `/acs`) is rewritten to `Path=/` with a note (review m13).
- All four upstream TLS modes are implemented. Only `Strict` is exercised by tests; HTTPS tests are M6.
- The DNS-failure test depends on the environment. In sandboxes where every name resolves (a DNS sinkhole), it accepts Connect/Timeout instead of `Dns`.

### Simulator (M3)
- Folder layout is `Profiles/`, `Parameters/`, `Transport/`, `Rpc/`, `Session/`, `Engine/`, which differs slightly from the plan's tree. The types are the same.
- `ParameterStore` returns `FaultOr<T>` instead of the plan's separate `LookupResult`/`NamesResult`/`SpvResult`. Reason: one shape for "value or CWMP fault (+ per-parameter faults)".
- Profile `Count` must be 1. Multi-instance expansion (`{i}`, `{i:D6}`) exists in the engine but is rejected by validation until fleet support lands (M8).
- There is no automatic retry/backoff or periodic Inform scheduling (M8). A failed session puts the CPE in `Error` with `RetryCount++` and keeps the pending events. The next manual "Send Inform now" retries.
- Follow-ups (reboot, download, etc.) are not executed (M7). AddObject/DeleteObject and attributes are not implemented (M7). Unimplemented RPCs return fault 9000, and GetRPCMethods lists only the implemented methods.
- Raw Inform templates are not supported (M9).
- Exchanges are recorded after the transport returns. The transport captures the request headers it sends itself instead of using a `CaptureHandler`.

### Presentation / App (M4)
- `SynchronousUiDispatcher` lives in `AcsTool.Presentation` (not `AcsTool.Testing`). Reason: the tests and the design-time/headless paths both use it, and Presentation must not depend on Testing.
- `SessionFeedPump.IsCollecting` was added so tests can wait deterministically.
- Feed filtering is done in the view model (not a WPF `ICollectionView`). Reason: it is testable on Linux.
- The ProxyViewModel test binds to a free ephemeral port rather than port 0, because port validation is 1–65535.
- Not done (scheduled for v1 or later): the in-app log panel / `InAppLogSink` (logs go to the rolling file only), the dark theme, and the export dialog (M9).

## Known gaps against MVP acceptance criteria
- T4.6 manual smoke checklist (below) has not been run on Windows. The WPF app was only compiled and published (self-contained win-x64) on Linux. Its XAML is compiled there, but it was never rendered.
- The parser performance test (`Category=Perf`, 1 MB Inform in under 50 ms) takes the best of 5 timed runs, because a single timed run failed at 71 ms (Debug build, test assemblies running in parallel). It is excluded from `build.sh` by default but runs under plain `dotnet test`.
- HTTPS proxy listener and TLS-mode tests are not in scope until M6.

## Manual smoke checklist (Windows, T4.6)
1. Run `AcsTool.exe`. On the Proxy tab, set listen port 7547 and the upstream URL of a local ACS, then Start. The status bar shows `Proxy: Running`.
2. On the Simulator tab, create a TR-098 profile with ACS URL `http://127.0.0.1:7547/acs`, save it, start it, and click "Send Inform now".
3. The feed shows a Proxy session and a Simulator session for the same device, and both end Completed. A proxied session without a trailing empty POST completes within about 3 s.
4. Select a session. The Raw tab shows the XML, the Pretty toggle formats it, and Authorization is redacted until revealed.
5. Close the window. The proxy and simulator stop, and the process exits within a few seconds.
6. Run `AcsTool.exe --smoke-exit`. It exits with code 0.
7. Resize the main window slowly across its minimum size on both tabs. Scrollbars appear only below the minimum, the Simulator tab shows no scrollbar at the default size, and the Simulator splitter resizes the profile editor against the lower pane. (Rendering can't be checked on Linux.)
8. Password binding (review R2-M1). On a fresh install (no profiles or settings file yet), click **+ From TR-098** first (no profile is created automatically), select the new profile and type a password into the Password field. Use credentials that an ACS requiring Basic or Digest auth accepts. Save and restart the app. Start the profile and click "Send Inform now". The CPE authenticates: the session shows an auth challenge followed by success and ends Completed, not AuthFailed. The password field shows dots and never plain text.

## Review fixes (docs/REVIEW_M0-M4.md, branch fix/review-m0-m4)

Fixed, each with tests: M1, M2, M3, M4, M5, M6, m1, m5, m6, m8, m10, m11, m12 (partly), m13, m14, plus the user-reported clipped-tab layout bug.

Decisions and deviations:
- **M1/M2:** Persisted settings reach their consumers reactively through `SettingsHolder.Changed`, not by loading settings before DI resolution. Loading twice would lose the corrupt-settings-file banner. Consumers are the Proxy tab (only while stopped and editable), store limits, feed size/order and the status bar. A changed simulator concurrency limit takes effect the next time a profile starts while no CPE is running. The old gate is not disposed, because in-flight waiters may still hold it.
- **M4:** Summaries are still parsed from the full captured body, but each list keeps at most 200 items plus an "… and K more" field. The Raw view holds the full captured body. The capture budget is an estimate: body bytes + 2 × (header and summary field characters) + warnings.
- **M6:** While paused, the feed coalesces updates by session id. Above 10,000 pending ids, or when a resync batch arrives, it drops the list and does a full resync from the store on resume. Sessions evicted during the pause therefore do not reappear.
- **m8:** An undecryptable DPAPI password throws `SecretUnprotectException`. The editor shows a warning and keeps the stored ciphertext unless the password is retyped, and Start is refused with a validation error. Real DPAPI behaviour can't be tested on Linux, so tests use a fake protector.
- **m10:** Upstream URLs containing user:password@ are rejected by both validators. As a second line of defence, the proxy redacts userinfo in logs and session targets (warning event 1013).
- **m13:** Upstream Set-Cookie Path values that the CPE's request path doesn't path-match (RFC 6265 §5.1.4) become `Path=/`, with a note on the exchange. The earlier note on cookie paths was corrected.
- **m14:** Idle/grace and end-to-end tests now use `FakeTimeProvider`. Coverage was added for Basic auth through the proxy, the ManagedPerCpe cookie jar, and byte-identical responses with multi-valued headers.
- **UI layout:** Every tab (main and settings windows) is hosted in a ScrollViewer. Views are bound to the viewport size, with MinWidth/MinHeight, so star rows and DataGrid virtualization still work when there is room, and scrollbars appear only when the window is smaller. The simulator profile editor also scrolls on its own. Fixed widths were replaced by star/Auto + MinWidth. Rendering can't be checked on Linux; `XamlLayoutTests` lint the XAML instead, and visual confirmation on Windows is still pending.

Deferred:
- **m12 (part):** The upstream `SocketsHttpHandler` is still created inside the tracker lock. The custom CA is now preloaded at Start (a missing or invalid file fails Start with `ProxyStartException`), so creating the handler does no I/O. Moving it out of the lock would mean restructuring `Resolve`, which is out of proportion to the remaining risk.
- **Outside the requested scope, unchanged:** m2 (a late trailing empty POST becomes an orphan row), m3 (cookie-pair correlation ambiguity), m4 (simulator capture shows post-response cookies), m7 (pretty-print race), m15 (Settings window ✕ / OnClosing robustness), and nits n1–n8.

Follow-up after the re-review and TesterBot's re-verification (fast-forwarded to TesterBot's `tester/m0-m4-fix`, d355a8a):
- **R-M1:** The Proxy tab reloads the persisted proxy settings after Stop and after a failed Start. The Settings window (a singleton view model) also refreshes from the current settings each time it opens. Before this, a save made while the proxy was running was overwritten by stale tab values on Stop → Start, and the Settings window could show stale values and write them back. M1 is now fully fixed.
- **m9:** The simulator password is a `PasswordBox` bound through the `PasswordBoxAssistant` attached property; values pushed by the view model are not echoed back. A PasswordBox can't hold null, so it reports "" after an empty or undecryptable password is loaded. The editor therefore doesn't count a null ↔ "" change as an edit, which keeps the m8 ciphertext-keeping intact. There is no "show password" toggle.
- **R-m1/NEW-01:** The Simulator view MinHeight went from 760 to 560, and the row minimums (240 + 300) fit within it. The editor MinHeight went from 520 to 360. At the default window size there is no outer scrollbar. The inner editor scrollbar can still appear when the Running CPEs list is tall.
- **NEW-02:** The Simulator layout now has three rows: editor, splitter, and a lower pane holding Running CPEs plus feed/detail. The splitter resizes the editor against that whole pane, not against the Auto-sized Running CPEs row.
- **BUG-01:** A SOAP Fault answering the Inform ends the proxy session as AcsFault immediately. This is checked before the HTTP status, because SOAP 1.1 faults usually come with HTTP 500. A Fault answering any later CPE message still leaves the session open, because the ACS may continue. TesterBot's repro is un-skipped and the characterization test was flipped.
- **BUG-03:** The environment-dependent network tests use `Xunit.SkippableFact` (1.5.23; xUnit 2.9 has no built-in dynamic skip), so they report Skipped instead of Passed.
- **R2-M1** (re-review 2): The PasswordBox never forwarded typing for profiles whose password starts out null. Those are all the default profiles and every profile without a password. The cause was that PasswordChanged was only hooked when the bound value changed, and a null → null binding never fires. A separate `PasswordBoxAssistant.Attach="True"` now hooks the event independently of the value, with no double subscription. The echo logic moved to the UI-free `PasswordSync`, which is unit-tested with a fake box, and a XAML lint test asserts `Attach="True"`. Loading a profile is still not counted as a user edit (m8/m9). Smoke step 8 covers this on Windows.
- **R2-n1:** The Fault-to-Inform rule (BUG-01) does not apply to HTTP 401 or 407. A 401 still goes through the auth challenge and retry logic. A 407 keeps its existing HttpError handling.
- **R2-n4:** The nested DockPanels in `SimulatorView.xaml` were reindented. This is whitespace only.
- **CI and `ACSTOOL_SKIP_NET_TIMEOUT`:** Nothing in CI sets the variable (checked `.github/workflows`, `build.sh` and `build.ps1`; there are no runsettings files). There are now guards against it being set: the Linux CI job has a step that fails if it is set, and `build.sh` / `build.ps1` exit with an error when `CI=true` and the variable is set.

Deferred after the final review (re-review 3, @ 1b8fe03):
- **Still deferred from earlier rounds:** R2-n2 (an empty POST after a Fault becomes an orphan session; to be folded into the m2 fix), R2-n3 (Settings opened from code-behind), and R-n1–R-n6, along with m2, m3, m4, m7, m15, part of m12, and n1–n8.

## CI fix (branch fix/ci-analyzers)

- **Root cause:** After the merge, every Actions run failed in both jobs with `CA1859` in `tests/AcsTool.Core.Tests/SessionStoreTests.cs(17,60)`. Under `CI=true`, warnings are errors. `global.json` pinned 8.0.400 with `rollForward: latestMajor`. setup-dotnet installed SDK 8.0.427, but the runner images also come with .NET 9 and 10 SDKs (`dotnet --info` lists 10.0.403 on Linux and 10.0.402 on Windows), and `latestMajor` picks the newest installed SDK. CI therefore built with SDK 10, whose analyzers flag this case; local builds used 8.0.427, which doesn't.
- **Reproduced locally:** With SDK 10.0.403 installed side by side in `~/.dotnet`, the same single CA1859 error appeared. Pinning `AnalysisLevel` alone did not help, because the rule is part of the 8.0 set and the SDK 10 analyzer simply detects more cases. With the fix, SDK 10.0.403 reports no other new analyzer findings.
- **Fix:**
  - `SessionStoreTests.Drain` now takes `InMemorySessionStore`.
  - `global.json` sets `8.0.427` with `rollForward: latestFeature`. 8.0.427 is a **minimum**, not an exact pin (review n4). The build uses the newest installed 8.0.x SDK at or above it, never 9 or 10. setup-dotnet installs the latest 8.0 SDK (`--channel 8.0`), so newer 8.0 patch releases are picked up automatically, which is intended for security fixes. `AnalysisLevel` 8.0-recommended limits analyzer drift. For a bit-exact toolchain, `rollForward: disable` would be needed, with manual bumps.
  - Both CI jobs use `setup-dotnet` with `global-json-file: global.json`, so CI and local builds can't drift, and print `dotnet --info`.
  - `AnalysisLevel` is pinned to `8.0-recommended` instead of `latest-recommended`.
- **R3-n1:** The Windows job now runs `./build.ps1 -NoCoverage` (restore, build and all non-Perf tests, including the `ACSTOOL_SKIP_NET_TIMEOUT` guard) before publishing and `--smoke-exit`.
- **First test run on Windows** (found by R3-n1): two tests failed only on the Windows runner.
  - `Build_Inform_MatchesGoldenFile`: the checkout's `core.autocrlf` converted the fixtures to CRLF. `.gitattributes` now marks `tests/fixtures/** -text`, so fixtures stay byte-exact.
  - `FailedSession_ErrorStateKeepsEvents_ManualRetryIncrementsRetryCount` failed because of an observer race in the tests (review n1).
    - **The flake:** `SimulatedCpe` bumps `SessionsRun` before it publishes the final state, both before and after this branch. A test that waited only for the count and then asserted the state could see InSession.
    - **The real fix:** the tests now wait for both the count and the final state. This applies to `TransportAndEngineTests` and, since fix/ci-followups (n2), `TesterScenarioTests`.
    - **Supporting change:** the increment now comes after the event bookkeeping and just before `SetState`, so every `StateChanged` observer sees a consistent snapshot: count, `LastResult` and pending events. The Running CPEs row, which reads the count inside `StateChanged`, therefore shows the new value.
    - **Pinned by tests:** `StateChanged_CarriesNewSessionsRun_ForFinalState` and `StateChanged_CarriesNewSessionsRun_ForErrorState` deterministically record (state, count) pairs in the handler (m1).
    - **Thread safety:** `SessionsRun` is written with `Interlocked.Increment` and read with `Volatile.Read` (n2).
  - Network tests needed no Windows skip.
- **Result:** CI is green on both jobs with SDK 8.0.427. Each job runs Core 308, Proxy 97, Simulator 65 and Presentation 54 tests with 0 skipped; on the GitHub runners the DNS test actually runs.
- **Actions runtime (fix/ci-followups):** `actions/checkout` moved from v4 to v7 and `actions/setup-dotnet` from v4 to v6, the current majors, both on `node24`. The v4 actions targeted the deprecated Node 20 and were forced onto Node 24 with a warning annotation. The v6/v7 release notes list only ESM and dependency migrations plus fork-PR hardening, no input changes; `global-json-file` is still supported.

Deferred after the fix/ci-followups review (non-blocking):
- Replace the dead-port test (`TransportAndEngineTests.cs:216-219`) with a FakeAcs that returns HTTP 500.
- Pin the GitHub Actions to commit SHAs and add Dependabot to keep them updated.
- Add `permissions: contents: read` to `ci.yml`.
- Consider pinning the Linux runner to `ubuntu-24.04` instead of `ubuntu-latest`.
- TesterBot: the CPE loop's catch block sets Error without bumping `SessionsRun`.

## Migration to .NET 10 (branch chore/net10, plan Q1 resolved)

- **Targets:**
  - `AcsToolTfm` is `net10.0` and the App targets `net10.0-windows` (still `EnableWindowsTargeting`).
  - `global.json` requires 10.0.403 with `rollForward: latestFeature`, so it uses the newest installed 10.0.x and never 11.
  - `AnalysisLevel` is `10.0-recommended`. The SDK 10 analyzers reported no new findings, and no suppressions were added.
- **Packages:**
  - `Microsoft.Extensions.*` 8.0.x → 10.0.14.
  - `Microsoft.AspNetCore.TestHost` 8.0.33 → 10.0.14. Kestrel still comes through the `Microsoft.AspNetCore.App` FrameworkReference.
  - `Microsoft.Extensions.TimeProvider.Testing` 8.10.0 → 10.11.0.
  - `CommunityToolkit.Mvvm` 8.4.0 → 8.4.2.
  - `Serilog.Extensions.Hosting` 8.0.0 → 10.0.0 and `Serilog.Sinks.File` 6.0.0 → 7.0.0.
  - `Microsoft.NET.Test.Sdk` 17.14.1 → 18.11.0.
  - `xunit.runner.visualstudio` 2.8.2 → 4.0.1, which still runs v2 tests.
  - `coverlet.collector` 6.0.4 → 10.1.0.
  - `Xunit.SkippableFact` 1.5.23 → 1.5.85.
  - `xunit` stays 2.9.3, the latest v2. Moving to xunit.v3 is a separate migration, deferred: v3 has a built-in `Assert.Skip`, so it would replace SkippableFact.
  - `System.Security.Cryptography.ProtectedData` was **removed**. On net10.0-windows it is part of the WindowsDesktop shared framework, and SDK 10 raises NU1510 ("will not be pruned"), an error under CI. The self-contained publish still ships `System.Security.Cryptography.ProtectedData.dll` from the desktop runtime, and the DPAPI calls are unchanged.
- **Behaviour changes checked** (against the .NET 9 and 10 and ASP.NET Core 10 breaking-change lists):
  - **X509 (SYSLIB0057, .NET 9):** `TlsValidatorFactory.LoadCertificate` used the obsolete `new X509Certificate2(byte[])` under a pragma. It now uses `X509CertificateLoader.LoadCertificate` for DER, falling back to `LoadPkcs12Collection` for password-less PFX, which the old constructor accepted implicitly. PEM still goes through `CreateFromPem`. New tests cover PEM, DER, PFX and garbage. `new X509Certificate2(X509Certificate)` in the validation callback is a copy constructor and is not obsolete.
  - **Kestrel / ASP.NET Core 10:**
    - The proxy uses `WebApplication.CreateSlimBuilder`, not the now-obsolete `WebHostBuilder`/`IWebHost`.
    - None of the other ASP.NET Core 10 breaking changes apply: cookie-auth API redirects, IExceptionHandler diagnostics, `IPNetwork`/`KnownNetworks`, OpenAPI and Razor.
  - **HttpClient / SocketsHttpHandler:**
    - The per-session `SocketsHttpHandler` is created directly, without `IHttpClientFactory`, so the .NET 9 factory changes (header and query redaction in logs, default handler) don't apply.
    - The "HTTP/3 disabled with PublishTrimmed" change doesn't apply either, because the app is not trimmed and uses HTTP/1.1 upstream.
  - **TLS / SslStream:** no API or behaviour change hit; tests pass. On Unix .NET 10 requires OpenSSL 1.1.1+, which the runners and this box have.
  - **DPAPI:** `ProtectedData` behaves the same; only the packaging changed (see above). It can still only be exercised on Windows.
  - **WPF:**
    - .NET 10 rejects empty `<Grid.ColumnDefinitions>`/`<Grid.RowDefinitions>` with MC3063. We have none; the XAML compiles on SDK 10.
    - .NET 10 also crashes on a mistyped `DynamicResource`. We have no `DynamicResource` at all.
    - The "`GetXmlNamespaceMaps` type change" (.NET 9) is not used.
  - **Other:**
    - Runtime SIGTERM default handlers were removed. The App is WPF, and the Generic Host's lifetime handles shutdown, so nothing changes.
    - Configuration null values are now preserved, but settings use our own JSON store, not `IConfiguration` binding.
    - C# 14 span overload resolution: compiles and passes.
    - `dotnet restore` now audits transitive packages, and it reports nothing.
- **Verified locally:**
  - `CI=true ./build.sh` passed with 0 warnings and all tests run on the net10.0 runtime (10.0.14), coverage included.
  - The Release build of the App produced `net10.0-windows/AcsTool.dll`.
  - `dotnet publish -r win-x64 --self-contained` from Linux succeeded. Its runtimeconfig includes NETCore, WindowsDesktop and AspNetCore 10.0.14.
  - Running the app (smoke checklist) is only possible on Windows. CI's `--smoke-exit` step covers start and exit.
