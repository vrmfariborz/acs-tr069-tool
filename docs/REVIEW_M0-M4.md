# Code review: ACS TR-069 Proxy & Inform Simulator, MVP (M0–M4)

| | |
|---|---|
| Commit reviewed | `b67eb19` ("docs: note perf test timing method"), working tree clean |
| Date | 2026-10-29 |
| Reviewer | ReviewerBot (review only; no source changes) |
| Inputs | `docs/PLAN.md` (incl. §5.2 checklist), `docs/IMPLEMENTATION_NOTES.md`, all of `src/` and `tests/` |

## Summary

| Severity | Count |
|---|---|
| Blocker | 0 |
| Major | 6 |
| Minor | 15 |
| Nit | 8 |

**Overall.** The proxy core is solid. Request and response bodies stay as bytes end to end. Headers are copied with `TryAddWithoutValidation`/`NonValidated`. Hop-by-hop stripping, chunked→Content-Length, empty POST `Content-Length: 0`, the bodiless 204 and Basic/Digest pass-through all work and have tests. Error classification follows the plan's algorithm, and every branch is unit-tested. XML parsing is XXE-safe. I found no sync-over-async, no `async void` outside WPF handlers, and no lock held across an `await`.

The Major findings are in three other places:
- **Settings wiring at startup.** Persisted proxy and capture settings never reach the Proxy tab, the session store or the simulator engine. Pressing Start overwrites the persisted proxy settings with defaults.
- **One end-rule interaction with the 2 s grace deviation.** A completed session gets relabelled `Superseded`.
- **Gaps in the bounded-memory design.** Summaries aren't counted against the budget. Simulator responses are never truncated. The paused feed queue is unbounded.

**Build and tests (run on a private clone in `/tmp`; nothing was built in the shared repo folder; no fixed ports used).**
- .NET SDK 8.0.427. `./build.sh` → restore + Release build: **0 warnings, 0 errors**, including the WPF App, compile-only. `CI=true` (warnings as errors) also builds clean.
- Tests: **447/447 passed**: Core 302, Proxy 55, Simulator 59, Presentation 31. Two more full runs of `dotnet test AcsTool.sln` (303 Core, Perf included) were also all green. The `Category=Perf` parser test passes.
- Coverage from cobertura, each project's own test assembly: Core 87.2 % line, Proxy 89.8 %, Simulator 89.8 %, Presentation 83.5 %. NFR-09 (≥ 80 % on Core/Proxy/Simulator) is met.
- I wrote throw-away probe tests in the clone (deleted afterwards) to confirm M1, M2, M3 and m2, and to check response-direction fidelity. Response fidelity **passed**: a body with a BOM, CRLF and non-ASCII bytes came back byte-identical. Two `Set-Cookie` and two `WWW-Authenticate` lines were relayed as separate lines. The upstream `Date` and `Server` headers were relayed without Kestrel duplicating them.

**Not verified here:** WPF rendering and the T4.6 manual Windows smoke test (no Windows host). DPAPI at runtime (Windows-only API; reviewed statically). HTTPS upstream / TLS modes (M6 scope; reviewed statically only).

---

## Blocker

None.

---

## Major

### M1 — Persisted proxy settings never reach the Proxy tab, and Start overwrites them with defaults
- **Where:** `src/AcsTool.App/App.xaml.cs:57-60`; `src/AcsTool.Presentation/Proxy/ProxyViewModel.cs:72-77` and `:144-153`; `src/AcsTool.Presentation/ServiceCollectionExtensions.cs:33-35`
- **What:** `App.OnStartup` resolves `MainViewModel` (line 57) before `InitializeAsync()` loads `settings.json` (line 60). `MainViewModel`'s constructor pulls in the singleton `ProxyViewModel`. That constructor copies `BindAddress`, `Port`, `UpstreamUrl`, `TlsMode` and `CookieMode` from `SettingsHolder.Current`, which still holds `AppSettings.Default` at that point. Nothing refreshes them later: no subscription to `SettingsHolder.Changed`, and `MainViewModel.InitializeAsync` only calls `Settings.LoadFrom`. Then `StartAsync` writes the view model's stale fields back into `SettingsHolder.Current` (line 153), and `ShutdownAsync` saves them.
- **Evidence:** A probe persisted Port 17999 and upstream `http://acs.persisted:9000`, then ran `InitializeAsync`. Result: holder = 17999, `SettingsViewModel` = 17999, but `ProxyViewModel.Port` = 7547 and `UpstreamUrl` = `http://localhost:7557`. The same thing happens to edits saved in the Settings window: bind/port/upstream/cookie mode are silently replaced on the next Start.
- **Why it matters:** The user's proxy configuration is lost on every launch, and pressing Start with the defaults overwrites the persisted file. This also contradicts the comment at `SettingsViewModel.cs:115`, "Proxy changes apply on the next Start".
- **Fix:** Add `ProxyViewModel.LoadFrom(ProxySettings)`. Call it from `MainViewModel.InitializeAsync` after the load, and from a `SettingsHolder.Changed` handler while the proxy is stopped, marshalled via `IUiDispatcher`. Alternatively, make the Proxy tab fields a view over `SettingsHolder.Current.Proxy`. Add a VM test: persisted settings → `InitializeAsync` → `ProxyViewModel` shows them, and Start keeps them.

### M2 — Store capture limits and simulator concurrency are frozen at defaults
- **Where:** `src/AcsTool.Core/CoreServiceCollectionExtensions.cs:28`; `src/AcsTool.Simulator/Engine/SimulationEngine.cs:33`; `src/AcsTool.Presentation/ServiceCollectionExtensions.cs:46-48`
- **What:** `InMemorySessionStore` is built from `SettingsHolder.Current.Capture` when the DI container first resolves it, and so is the `SemaphoreSlim` in `SimulationEngine`. Both are resolved through `MainViewModel` before settings are loaded (same root cause as M1). The values are never updated. So `MaxSessions`, `CaptureBudgetBytes` and `MaxConcurrentSessions` from `settings.json`, or from the Settings window, never take effect. The status bar does show the *persisted* budget (`MainViewModel.cs:98`), so it disagrees with what the store actually enforces. The feed's `MaxItems`/`NewestOnTop` are copied once at `InitializeAsync` and not updated after a Settings save.
- **Evidence:** In the probe with persisted `MaxSessions = 10`, the store held 30 ended sessions. `SimulationEngine.MaxConcurrentSessions` was 32 although 3 was persisted.
- **Why it matters:** NFR-05 limits are configured in the UI but ignored. Users who lower the budget to stay within memory get no protection.
- **Fix:** Give the store a `CaptureSettings` provider, e.g. `Func<CaptureSettings>` or `SettingsHolder`, that it reads on each eviction pass, or add an `UpdateLimits(CaptureSettings)` method called from `SettingsHolder.Changed`. Do the same for the feed VMs. For the engine, either document "restart required" or recreate the gate when no session is in flight. At minimum, resolve the view models only *after* `LoadAsync` (load settings in `App.OnStartup` before resolving `MainViewModel`).

### M3 — A new Inform inside the 2 s grace window relabels a completed session as `Superseded`
- **Where:** `src/AcsTool.Proxy/CpeSessionTracker.cs:82-88` (together with `:281-286` and `:372-375`)
- **What:** With the deviation, a session whose ACS answered the CPE's last response with an empty body waits in `AwaitingFinalEmpty` for up to grace + sweep interval (2–3 s). If the CPE opens its next session in that window, `Resolve` takes the "same IP + DeviceKey" branch and ends the old session as `Superseded`. The old session had actually completed. This happens with the simulator, which never sends a trailing empty POST, when "Send Inform now" is clicked twice in a row. It also happens with real CPEs that chain sessions (queued events, `M Download` → TransferComplete).
- **Evidence:** Probe: Inform → empty → GPVResponse → 204, then a new Inform on a new connection. The stored outcomes were `Superseded x3` and `InProgress x1`.
- **Why it matters:** Wrong outcome badges for healthy sessions, and "Superseded" points users at a CPE bug that isn't there.
- **Fix:** In the supersede loop, `EndLocked(old, old.AwaitingFinalEmpty ? FinalOutcome(old) : SessionOutcome.Superseded)`. Apply the same rule when a request lands on a connection or IP whose current session is `AwaitingFinalEmpty`. Add a tracker unit test with `FakeTimeProvider`.

### M4 — The capture budget leaves out message summaries and headers; summaries hold every parameter of the untruncated body
- **Where:** `src/AcsTool.Core/Parsing/MessageSummarizer.cs:220-227` (`AddParams` adds one field per parameter); `src/AcsTool.Core/Sessions/InMemorySessionStore.cs:96` and `:123` (only `Body.Length` is counted); `src/AcsTool.Proxy/ProxyRequestHandler.cs:63-64` and `:81-82` (parsing uses the full, up-to-10-MiB body)
- **What:** `CapturedBytes`, and therefore eviction, only counts captured body bytes. Each `ExchangeRecord` also keeps a `MessageSummary` whose `Fields` hold every parameter name and value as UTF-16 strings, plus the header list. Summaries are built from the **full** body, not the `MaxCapturedBodyBytes` copy. A 10 MiB GPV response captured at 1 MiB still keeps all 10 MiB of values, roughly 20 MiB as UTF-16, outside the budget. With 5,000 sessions of large Informs, real usage can be many times the 256 MiB budget.
- **Why it matters:** NFR-05 / checklist "Bounded memory": the budget doesn't bound memory.
- **Fix:** (a) Cap summary fields, e.g. the first N = 200 parameters plus a "… and K more" field. The detail pane can re-parse the captured body lazily when needed. (b) Count an estimate of summary and header size in `CapturedBytes`. A cheap estimate is the sum of `2 × (key.Length + value.Length)` over fields and headers, computed in `AddExchange`/`CompleteExchange`.

### M5 — Simulator response captures are never truncated
- **Where:** `src/AcsTool.Simulator/Session/CwmpClientSession.cs:202`
- **What:** `HttpMessageCapture.From(..., r.Body.Span, int.MaxValue)`. The request capture is truncated (`HttpCwmpTransport.cs:103`), but the response keeps the whole body, up to the transport's 16 MiB guard (`HttpCwmpTransport.cs:100`). The proxy path truncates both directions.
- **Why it matters:** This violates `MaxCapturedBodyBytes` (NFR-05). A misbehaving ACS sending large GPN/GPV requests to many simulated CPEs fills memory quickly, and the truncation banner never shows for simulator sessions.
- **Fix:** Pass the configured `CaptureSettings.MaxCapturedBodyBytes` into `ClientSessionContext` and use it here. Add a test in the style of the existing truncation test.

### M6 — The paused feed buffers every batch without limit
- **Where:** `src/AcsTool.Presentation/Feed/SessionFeedViewModel.cs:20` and `:64-67` (`_paused.Add(batch)`), replayed at `:213-224`
- **What:** While paused, every `FeedBatch` is appended to a `List<FeedBatch>` together with its `SessionSnapshot`s and resync lists. At 200 req/s that is about 10 batches/s, each with up to 500 snapshots. Snapshots of sessions the store has already **evicted** stay reachable, including their captured bodies, so a long pause holds memory well past the capture budget. On resume, every batch is replayed in order on the UI thread. PLAN §3.8 rule 6 asks for "a pending dictionary" (coalesced).
- **Why it matters:** This breaks the bounded-memory design, and resuming after a long pause can freeze the UI.
- **Fix:** Coalesce while paused into `Dictionary<Guid, SessionChangeKind>` (use `SessionFeedPump.Max` for precedence) plus a `cleared` flag, without keeping snapshots. On resume, read fresh snapshots via `store.Get(id)`, or do a single `Resync(store.GetAll())`. Extend `Pause_AccumulatesAndAppliesOnResume` to check that memory stays bounded and that evicted sessions don't reappear.

---

## Minor

### m1 — Sessions in flight when the proxy stops end as `UpstreamError` with a 502, not `Canceled`
- **Where:** `src/AcsTool.Proxy/CpeSessionTracker.cs:238-241`; `src/AcsTool.Proxy/ProxyServer.cs:109`, `:127`
- **What:** `StopAsync` cancels `_stopping`. In-flight forwards are classified `Canceled`, which is correct, but `EvaluateEnd` maps every `IsFailure` to `SessionOutcome.UpstreamError`, and `RelayAsync` sends a 502. The later `EndAllAsync(Canceled)` is a no-op for these sessions. PLAN §3.6.4 says "Proxy stopped → `Canceled`".
- **Fix:** `if (result.IsFailure) return result.Error?.Kind == UpstreamErrorKind.Canceled ? SessionOutcome.Canceled : SessionOutcome.UpstreamError;`. Add a test that stops the harness while FakeAcs is delaying.

### m2 — A trailing empty POST that arrives after the grace window creates a spurious orphan session
- **Where:** `src/AcsTool.Proxy/CpeSessionTracker.cs:94-100`, `:372-375`
- **What:** If a slow CPE sends its trailing empty POST more than 2–3 s after the ACS's empty response, the original session has already been completed and unindexed. The POST then becomes a new "Unknown session from {ip}" with one exchange and `Completed`. Probe confirmed: `Completed x3` plus `Unknown session … Completed x1`.
- **Fix:** Keep a small, time-bounded "recently completed" index (connection id → session id, about 30 s). Attach a lone empty POST/204 to that session as a late exchange instead of creating an orphan. At least add a note such as "late trailing empty POST" so users understand the extra row. Consider making `CompletionGrace` configurable.

### m3 — Correlation by cookie pair can mix up CPEs that share a cookie value
- **Where:** `src/AcsTool.Proxy/CpeSessionTracker.cs:137-146` (lookup), `:214-221` (`_byCookie[pair] = session`, last writer wins)
- **What:** Every `name=value` pair from upstream `Set-Cookie` is indexed globally. Load-balancer affinity cookies (`SERVERID=node1`, `AWSALB…`) and constant cookies (`lang=en`) are shared by many CPEs. A CPE that reconnects mid-session (common after a 401 with `Connection: close`) is then matched to *another* CPE's session before the IP fallback is tried. The request is recorded in the wrong session and forwarded over the other session's upstream connection (D6 affinity broken; in ManagedPerCpe it also uses the other CPE's cookie jar).
- **Fix:** Only accept a cookie match when the candidate's `ClientIp` equals the request's IP, or when the pair maps to exactly one active session. Track `pair → set<session>` and ignore ambiguous pairs. Document the remaining limitation.

### m4 — The simulator request capture shows cookies that were not sent
- **Where:** `src/AcsTool.Simulator/Transport/HttpCwmpTransport.cs:100-103`, `:121-125`
- **What:** `CaptureRequestHeaders` runs *after* `SendAsync` and reads `Cookies.GetCookieHeader(_url)`. The container has already absorbed the response's `Set-Cookie`, so the first Inform's capture shows a `Cookie` header that never went on the wire. Later exchanges show the post-response jar, not the jar at send time.
- **Fix:** Snapshot `Cookies.GetCookieHeader(_url)` before `SendAsync` and use that snapshot in the capture.

### m5 — An unexpected exception in a simulated CPE kills its loop and leaves the session `InProgress` forever
- **Where:** `src/AcsTool.Simulator/Engine/SimulatedCpe.cs:185-207` (no catch around `RunAsync`), `:177-182`
- **What:** `CwmpClientSession.RunAsync` only catches cancellation. Any other exception (builder, parameter store, sink) propagates to `LoopAsync`'s catch-all, which ends the loop permanently. The store session never gets `EndSession`, so it stays InProgress and is never evictable. Later "Send Inform now" posts go into a channel nobody reads, and the user gets no feedback beyond the Error state.
- **Fix:** In `CwmpClientSession.RunAsync`, add `catch (Exception ex) when (ex is not OperationCanceledException)` that calls `End(SessionOutcome.ProtocolError, …, note: ex.Message)`. In `LoopAsync`, catch per session so the loop keeps running.

### m6 — `SessionFeedPump` dies permanently on the first exception
- **Where:** `src/AcsTool.Presentation/Feed/SessionFeedPump.cs:89-122`
- **What:** The `catch (Exception)` sits outside the `while` loop. An exception from a consumer's `Apply` (e.g. an `ItemsControl` consistency exception) or from the dispatcher stops all feed and status-bar updates for the rest of the process. Only a log line records it, with no banner.
- **Fix:** Put the try/catch around each batch inside the loop, log, set the resync flag, and continue. Isolate consumers from each other (one try per consumer). Add a test with a throwing consumer.

### m7 — Pretty-print toggle race can show stale or wrong text
- **Where:** `src/AcsTool.Presentation/Detail/SessionDetailViewModel.cs:206-221`, `:228-239`
- **What:** `OnPrettyChanged` and `Update` start `UpdateDisplayAsync` as fire-and-forget. A continuation from an earlier record or toggle can finish after `Update()` reset the caches, or after `Pretty` was turned off. It then writes the old pretty text into `_requestDisplay`/`_responseDisplay`. Exceptions in the task are unobserved.
- **Fix:** Capture a version counter or the `Record` reference before awaiting, and assign only if it is unchanged and `Pretty` is still true. Keep one `CancellationTokenSource` per item for the pending format.

### m8 — A DPAPI decrypt failure silently wipes the stored password
- **Where:** `src/AcsTool.App/Services/PlatformServices.cs:80-83`; `src/AcsTool.Presentation/Simulator/CpeProfileEditorViewModel.cs:125`, `:175`
- **What:** If a profile was protected by another user or machine (copied profile folder, roaming `%APPDATA%`), `Unprotect` returns `""`. The editor shows an empty password, and the next Save writes `PasswordProtected = null`, erasing the original ciphertext. The simulator then connects without the password. There is no banner.
- **Fix:** Return a result with a failure flag, or throw a typed exception. Show a banner "password could not be decrypted for this Windows user; re-enter it". Keep the original `PasswordProtected` unless the user actually changes the field.

### m9 — Simulator password shown in clear text and kept decrypted for every profile
- **Where:** `src/AcsTool.App/Views/SimulatorView.xaml:85-86`; `src/AcsTool.Presentation/Simulator/CpeProfileEditorViewModel.cs:125`; `src/AcsTool.Presentation/Simulator/SimulatorViewModel.cs:97-101`
- **What:** The password is bound to a plain `TextBox`, so it is visible on screen and in screenshots. All profiles are decrypted into `Password` strings when they load and stay that way for the app's lifetime.
- **Fix:** Use a `PasswordBox` with a small attached behaviour or code-behind setter, plus an optional "show" toggle. Decrypt lazily in `ToProfile` and transport creation only, and keep `PasswordProtected` in the VM otherwise. Full in-memory zeroing isn't practical in .NET, so this is about limiting exposure.

### m10 — Credentials in the upstream URL go to the log and capture but are not forwarded
- **Where:** `src/AcsTool.Proxy/ProxyServer.cs:86` (Serilog), `src/AcsTool.Proxy/CpeSessionTracker.cs:167` (`TargetUrl` in every session), `src/AcsTool.Proxy/UpstreamForwarder.cs:61` (`Scheme://Authority` drops userinfo), `src/AcsTool.Presentation/Proxy/ProxyViewModel.cs:107-110` (validation accepts userinfo)
- **What:** `http://user:pass@acs/acs` passes validation. The password is then written to the rolling log file and shown in every session's target URL, yet it is never used.
- **Fix:** Reject userinfo in `ValidateUpstreamUrl` ("put credentials on the CPE; the proxy relays Authorization"), or strip it and mask it with `uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, …)` before logging or storing.

### m11 — Proxy tab validation and error handling don't match `ProxyServer`
- **Where:** `src/AcsTool.Presentation/Proxy/ProxyViewModel.cs:112-115`, `:155-179`; `src/AcsTool.Proxy/ProxyServer.cs:60-64`, `:71`
- **What:** (a) The VM accepts `localhost` as a bind address, but `ProxyServer.StartAsync` only takes IP literals and fails with "Address localhost is not available on this PC". (b) Only `ProxyBindException` is caught. Any other exception escapes the async command and leaves `State = Starting` permanently; `CanStartStop` returns false for Starting, so the Start button stays disabled until restart. This includes exceptions outside `ProxyServer.StartAsync`'s filter list (e.g. `OperationCanceledException`, `ArgumentOutOfRangeException`), which also leak `_stopping` and the built app.
- **Fix:** Map `localhost` → `IPAddress.Loopback` in the server, or reject it in the VM. In the VM, add a general catch that disposes the server, sets `Faulted` and shows a banner. In `ProxyServer.StartAsync`, use `try/finally` so the app and CTS are cleaned up on any failure.

### m12 — Tracker robustness: timer callback without a guard, and slow work under the global lock
- **Where:** `src/AcsTool.Proxy/CpeSessionTracker.cs:54`, `:361-382` (timer → `Sweep`), `:164-193` (`_handlers.Create` and `_sink.StartSession` under `_lock`); `src/AcsTool.Core/Net/Tls.cs:31-37`
- **What:** (a) An exception inside the `ITimer` callback, e.g. from a logger, sink or dispose, crashes the process (NFR-07). (b) `CreateLocked` builds the `SocketsHttpHandler` under the tracker-wide lock. In `CustomCa` mode that includes reading and parsing the CA file from disk (`Tls.cs:36`). Every new session repeats this, and the resulting `X509Certificate2` is never disposed. A missing CA file throws out of `Resolve`, so every Inform gets a 500.
- **Fix:** Wrap `Sweep` in try/catch with logging. Load the CA and pins once per `ProxyServer` start: validate on Start and raise a banner, then reuse the result across sessions and dispose it on stop. Create the handler outside the lock, insert under the lock, and dispose the handler if another thread won.

### m13 — `IMPLEMENTATION_NOTES.md` claims Set-Cookie `Path` rewriting that doesn't exist
- **Where:** `docs/IMPLEMENTATION_NOTES.md:36` vs `src/AcsTool.Proxy/HeaderRewriter.cs:75-99`
- **What:** The notes say "strip `Domain`, rewrite `Path`". The code only strips `Domain` and (conditionally) `Secure`. With an upstream base path or `PathMode=Fixed`, an ACS cookie `Path=/tr069` won't be sent back by a CPE posting to `/acs`, which breaks ACS session affinity. The UI warns only about Digest in that case.
- **Fix:** Either implement the rewrite (map the upstream base or fixed path prefix back to the CPE's path, or drop `Path`, and add a note), or correct the notes and extend the path warning to mention cookies.

### m14 — Test suite: wall-clock timing and missing negative cases
- **Where:** `tests/AcsTool.Proxy.Tests/ForwardingTests.cs:316-324` (1.2 s real delay), `:339-342` (2.2 s real delay); `tests/AcsTool.Proxy.Tests/ProxyHarness.cs:39` (`TimeProvider.System` with the real 1 s sweeper running alongside the manual `Sweep()` calls); `tests/AcsTool.Simulator.Tests/TransportAndEngineTests.cs:127-130` (asserts stop < 1 s by `Stopwatch`)
- **What:** The §5.2 checklist says "no wall-clock waits > 2 s in unit tests". The grace test sleeps 2.2 s on the real clock, while the background sweeper can end the session before the manual `Sweep`. The assertions tolerate that today, but the test isn't deterministic. The < 1 s stop assertion can fail on a loaded CI box. All runs passed here (3/3).
- **Missing tests** (each would have caught a finding above):
  - persisted settings → VMs/store (M1/M2)
  - new Inform during grace (M3)
  - summary/budget accounting (M4)
  - simulator response truncation (M5)
  - pause memory (M6)
  - Canceled-on-stop (m1)
  - late trailing empty POST (m2)
  - ManagedPerCpe forwarding: implemented but only the settings round-trip is tested
  - Basic auth through the proxy
  - response-direction byte fidelity, multi-valued `WWW-Authenticate`/`Set-Cookie` relay: verified by my probe, but no repo test
  - pump consumer exception (m6)
  - DPAPI failure path (m8)
- **Fix:** Pass a `FakeTimeProvider` into `ProxyHarness.StartAsync` (the parameter already exists) for the idle and grace tests and drive `Advance` instead of `Task.Delay`. Relax the stop assertion to "≤ 1 s of FakeAcs delay" or measure with a generous ceiling. Add the tests listed above.

### m15 — Settings window and window-close robustness
- **Where:** `src/AcsTool.App/Views/SettingsWindow.xaml.cs:17-29`; `src/AcsTool.Presentation/ServiceCollectionExtensions.cs:39`; `src/AcsTool.App/Views/MainWindow.xaml.cs:20-37`
- **What:** (a) `SettingsViewModel` is a singleton and edits apply directly to it. Closing the window with the title-bar ✕ doesn't run `Cancel`, so unsaved edits show up the next time it opens. (b) In `MainWindow.OnClosing`, an exception from `app.StopHostAsync()` goes to `DispatcherUnhandledException`, which sets `Handled = true`. The window stays disabled (`IsEnabled = false`) and `Shutdown` never runs, so the app hangs.
- **Fix:** (a) Call `LoadFrom(holder.Current)` when the window opens, or handle `Closing` without `DialogResult` as Cancel. (b) Wrap the shutdown sequence in `try/finally { Application.Current?.Shutdown(0); }`.

---

## Nit

- **n1** `src/AcsTool.Proxy/ProxyRequestHandler.cs:174`: `BodyTooLarge` is recorded with `RequestPhase.Dns`. Nothing was sent upstream, so the phase is misleading. Use a neutral phase, or add `RequestPhase.ReadingRequest`.
- **n2** `src/AcsTool.Proxy/CpeSessionTracker.cs:157`: "ambiguous correlation by IP" is appended on *every* ambiguous request (no dedupe, unlike `AddSessionNote`), so notes grow with session length. It also calls into the store while holding the tracker lock. Dedupe the note and collect it so it is written after leaving the lock.
- **n3** `src/AcsTool.App/App.xaml.cs:31`: the Serilog level is hard-coded. `LoggingSettings.MinimumLevel`/`FileLoggingEnabled` exist but are ignored. Fine for MVP (v1 item), but either wire them up or hide the record from the UI and settings until then.
- **n4** `src/AcsTool.Proxy/CpeSessionTracker.cs:86` + `ProxySession.cs:62-68`: superseding disposes the old session's `HttpMessageInvoker` while its exchange may still be in flight. My probe showed the in-flight request still completes on .NET 8, because disposing the handler doesn't abort active connections. That isn't a documented guarantee, though. Consider marking the session ended and deferring `Dispose` until its Gate is released.
- **n5** `src/AcsTool.Presentation/Proxy/ProxyViewModel.cs:105`: `ActiveSessions` is only raised on state changes and is not bound anywhere. Remove it, or refresh it from the status-bar batch.
- **n6** `src/AcsTool.Presentation/Detail/SessionDetailViewModel.cs:24`: `RevealAuthorization` stays on when the selection moves to another session. Reset it in `Load()` so revealing is a per-session action.
- **n7** `src/AcsTool.Proxy/ProxyRequestHandler.cs:73`: the forward only observes `serverStopping`. If the CPE disconnects, the upstream exchange keeps running until the request timeout. That may be intentional (it captures the ACS reply), but it isn't documented. PLAN §3.6.2 step 7 links the token to the caller.
- **n8** `src/AcsTool.Proxy/UpstreamForwarder.cs:35-37`: content headers on a bodiless GET/HEAD/OPTIONS are dropped silently (`msg.Content?.Headers`). This is harmless for CWMP; add a note on the exchange for fidelity.

---

## PLAN.md §5.2 ReviewerBot checklist

| Checklist item | Result | Notes / refs |
|---|---|---|
| Fidelity: byte-identical bodies, only documented header changes, `TryAddWithoutValidation`, content vs request headers split | **Pass** (minor caveats) | Bodies stay `ReadOnlyMemory<byte>` end to end (`ProxyRequestHandler.cs:55-73`, `UpstreamForwarder.cs:31`, `UpstreamSender.cs:36-47`). `TryAddWithoutValidation` and the `ContentHeaders` split are in place (`UpstreamForwarder.cs:33-43`). SHA-256 request test plus my response probe both pass. Header *order* isn't preserved (documented deviation). Caveats: m13, n8. |
| No sync-over-async, no `async void` except WPF handlers, `ConfigureAwait(false)` in libraries | **Pass** | grep found no `.Result`/`.Wait()`/`GetResult()`. `async void` only in `App.OnStartup`, `MainWindow.OnClosing`, `SettingsWindow.Save_Click`. Every `await` in Core/Proxy/Simulator has `ConfigureAwait(false)`; VMs correctly don't. |
| Cancellation: tokens passed down; linked, disposed CTSs | **Partial** | Linked CTSs are created with `using` (`UpstreamSender.cs:32`, `ConnectCallbackFactory.cs:49`, `CwmpClientSession.cs:53-54`). The forward isn't linked to `RequestAborted` (n7). The VM commands use `CancellationToken.None` (acceptable for short operations). |
| Disposal: per-session handler/invoker, SemaphoreSlim, CTSs, ProxyServer, SimulatedCpe; no HttpClient per request | **Pass** (minor) | `ProxySession.Dispose` → invoker (`disposeHandler: true`). The Gate is intentionally not disposed, which is fine because no wait handle is allocated. Server and engine are disposed. No `new HttpClient` in src. Gaps: the CustomCa `X509Certificate2` (m12) and handler disposal during an in-flight request (n4). |
| Threading: no UI collection mutated off the UI thread; Gate released in `finally`; no lock across `await`; ParameterStore written only from the CPE loop | **Pass** (with m6/m7) | The pump marshals via `IUiDispatcher.InvokeAsync`. CPE state goes through `Post`. Gate released in `finally` (`ProxyRequestHandler.cs:101-104`). Tracker and store locks contain no awaits. ParameterStore edits go through the `CpeTrigger.Edit` channel and the store is also locked. |
| XML safety: `DtdProcessing.Prohibit`, `XmlResolver = null`, size limits, no default `LoadXml` | **Pass** | `CwmpParser.cs:18-26`, `Util.cs:70`. The XXE fixture is tested. |
| Secrets: none in logs/exceptions/exports; DPAPI on Windows | **Partial** | Authorization is masked in the detail grid (`SessionDetailViewModel.cs:249-250`; tested). DPAPI is CurrentUser with entropy (`PlatformServices.cs:64-87`). No request headers or bodies are logged. Issues: m8 (silent wipe), m9 (plaintext TextBox), m10 (userinfo URL logged). Export is M9, so it couldn't be checked. |
| Error classification: known exception types map to a specific kind | **Pass** (m1) | Every §3.5.5 branch is implemented and unit-tested, with live DNS/refused/timeout tests. Only the *session outcome* mapping for `Canceled` is wrong (m1). |
| Bounded memory: captures truncated, store evicts, feed caps, no unbounded per-request collections | **Fail** | M2 (limits not applied), M4 (summaries/headers unbudgeted), M5 (simulator responses untruncated), M6 (pause queue). Tracker indexes are cleaned up at session end (pass). n2 is small per-session growth. |
| Spec conformance: cwmp:ID echoed, MaxEnvelopes=1, exact event codes, soap-enc arrayType counts, fault codes from `FaultCodes` | **Pass** | `CwmpClientSession.cs:166` echoes the ID. `CpeProfile.MaxEnvelopes = 1`. `EventCodes` constants are exact. `arrayType` is computed from the count (`CwmpMessageBuilder.cs:288`). Round-trip and golden tests exist. |
| Layering: Core without ASP.NET/WPF; Proxy and Simulator independent; VMs without `System.Windows` | **Pass** | Checked the csproj references. Presentation is net8.0, so the compiler enforces the last point. |
| Testability seams: `TimeProvider`, `IRandom`, `IAppPaths`, `IUiDispatcher`; no `DateTime.Now`/`new Random()`/`GetFolderPath` in libraries | **Pass** | grep found none in libraries (`GetFolderPath` only in the App's `WindowsAppPaths`). Tests don't always *use* the seams (m14). |
| Tests: acceptance criteria covered, `Method_Scenario_Expected` naming, no `Thread.Sleep`, no wall-clock waits > 2 s | **Partial** | Coverage is good and naming is mostly consistent. No `Thread.Sleep`. Violations: a 2.2 s real-clock wait and wall-clock assertions (m14), plus the missing negative cases listed in m14. |
| UX: banners with actionable text, async long operations, no modal dialogs for transient errors | **Partial** | Bind errors map to actionable banners (tested). `IDialogService` is unused, so there are no modal dialogs for transient errors. Gaps: no banner for pump death (m6), DPAPI failure (m8) or an unexpected start failure (m11). |

---

## Assessment of IMPLEMENTATION_NOTES deviations

| Deviation | Verdict |
|---|---|
| `global.json` `rollForward: latestMajor`, TFM pinned via `AcsToolTfm` | **Acceptable.** The TFM stays net8.0. Note that .NET 8 support ends 2026-11-10 (12 days after this review); plan Q1's switch to net10.0 should be scheduled. |
| No NSubstitute/AvalonEdit; hand-written fakes; plain TextBox viewers | **Acceptable.** The fakes are deterministic and readable. FakeAcs is a strong test asset. |
| CA suppressions (CA2007 etc.) | **Acceptable**, with a caveat: the ConfigureAwait convention is "enforced by review". This review checked it and found it complete. Consider `ConfigureAwaitChecker` or keeping CA2007 on for library projects only. |
| `build.sh` excludes `Category=Perf` | **Acceptable.** The best-of-5 perf timing is a reasonable defence against noise; it passes here. |
| CI Windows smoke uses `Start-Process -Wait -PassThru` | **Acceptable**, and correct for a WinExe. |
| Headers forwarded in Kestrel order; `Expect` not forwarded; handler enforces `MaxRequestBodyBytes` | **Acceptable.** Ordering is a documented fidelity limit. Dropping `Expect` is correct since Kestrel already answered the 100-continue. The 413 is recorded (tested). Note that the draining loop reads an oversized body to the end (bounded memory, unbounded time). Consider a hard abort above, say, 2× the limit. |
| End rules run before the response is relayed; Gate never disposed | **Acceptable.** The upstream body is already buffered, so ending first is safe. Not disposing the Gate is fine (no wait handle). |
| **End-rule extension: `AwaitingFinalEmpty` + 2 s idle grace, 1 s sweeper** | **Acceptable in principle, and more TR-069-faithful than the plan.** Per §3.7.1.4 the session is over once both sides have signalled "no more requests", so CPEs that close are correct. The implementation is careful: the sweeper skips sessions whose Gate is held, `LastActivity` is refreshed under the tracker lock in both `Resolve` and `ObserveResponse`, and a trailing empty POST finishes at once. I found no premature-termination race for requests already admitted. Two consequences need fixing: **M3** (a chained new Inform → `Superseded`) and **m2** (a late trailing POST → orphan row). The real-clock test should move to `FakeTimeProvider` (m14). Consider making the grace configurable. |
| T2.5 "5 exchanges" vs 4 HTTP requests | **Acceptable.** The described flow is indeed 4 requests, and the test asserts their order and status codes. |
| Set-Cookie rewriting pulled forward from M5; Secure stripped only for HTTP CPE leg + HTTPS upstream | **Partially accurate.** Domain/Secure behaviour matches FR-P-12. The "rewrite Path" claim is false (**m13**). |
| All four TLS modes implemented, only Strict tested | **Acceptable for MVP** (M6 scope). Static review of `Tls.cs` found the CustomCa per-session load/leak (**m12**). Revocation is `NoCheck` for every mode, including Strict; the plan wanted `NoCheck` for non-Strict only. Document this or reconsider it in M6. |
| DNS-failure test accepts Connect/Timeout in sinkholed environments | **Acceptable**, provided the assertion still requires a non-`Unknown` kind (it does). |
| Simulator folder layout; `FaultOr<T>`; Count must be 1; no retry/periodic; follow-ups/Add/Delete deferred; no raw templates | **Acceptable.** These are scope-consistent MVP cuts. They are clearly documented, and unimplemented RPCs return 9000. |
| Exchanges recorded after the transport returns; transport captures its own request headers | **Acceptable**, but it produces **m4** (cookie header captured after the response). |
| `SynchronousUiDispatcher` in Presentation; `IsCollecting`; filtering in VM; ephemeral port in VM test | **Acceptable.** All are test-motivated, and none leaks WPF into Presentation. |
| No in-app log panel, dark theme or export dialog (v1/M9) | **Acceptable** for MVP. Settings records for logging exist but are ignored (n3). |
| Manual Windows smoke (T4.6) not run | **Open.** This is still an MVP acceptance gap. I couldn't verify it either. M1 will show up in step 1 of that checklist if the user had saved settings earlier. |

---

## Suggested order of work
1. **M1 + M2** share one root cause: resolve the view models after loading settings, or make them react to `SettingsHolder`. Add one VM test.
2. **M3** is a one-line fix plus a tracker test.
3. **M5, M6, M4**, in that order: tighten the bounded-memory design.
4. **m1, m5, m6, m11, m12**: robustness.
5. The test hygiene items in m14.

## Re-review (fix/review-m0-m4 @ dfae3b3)

**Reviewer:** ReviewerBot · **Date:** 2026-10-29 · **Range:** `main` (e0be76f) … `fix/review-m0-m4` (dfae3b3), 15 commits, 43 files, +1261/−147.
This was a review only. I built and tested in a private clone at `/tmp/rr-acs` and used ephemeral ports. One throw-away probe test was used and then deleted.

### Verdict: **Approve with conditions**

The branch is a clear improvement. 14 of the 15 claimed fixes hold up, and each has a targeted test. One condition blocks the merge:

1. **R-M1** (below): fix it and add a VM test. Proxy settings saved in the Settings window while the proxy is running are still thrown away on the next Start. That is the same data loss as M1, so M1 is only partly fixed. The fix is about five lines.

Strongly recommended for the same follow-up commit, though not blocking on its own: **m9** (PasswordBox; see the deferred-items call) and **R-m1** (Simulator tab double scrollbars at the default window size).

### Status of each claimed fix

| ID | Status | Evidence (code → test) |
|---|---|---|
| M1 | **Partially fixed** | Load path fixed: `MainViewModel.InitializeAsync` calls `Proxy.LoadFrom`, and `ProxyViewModel.cs:80-85` follows `SettingsHolder.Changed` while stopped. Tests: `PersistedSettings_ReachProxyVmStoreFeedAndEngine_StartKeepsThem`, `SettingsWindowSave_WhileStopped_UpdatesProxyTabAndLimits`. **Still broken when settings are saved while Running**: see R-M1 (confirmed with a probe). |
| M2 | **Fixed** | `CoreServiceCollectionExtensions.cs:28-35` subscribes `store.UpdateLimits`. `InMemorySessionStore.UpdateLimits` evicts immediately. `SimulationEngine.cs:78-85` rebuilds the gate when no CPE is running (documented). Tests: the same VM test (MaxSessions=10 keeps 10 of 30; engine gate=3), plus `UpdateLimits_Lowered_EvictsImmediately…`. Caveat: R-n3. |
| M3 | **Fixed** | `CpeSessionTracker.cs:125` uses `old.AwaitingFinalEmpty ? FinalOutcome(old) : Superseded`. Test: `Resolve_NewInformDuringCompletionGrace_EndsOldAsCompletedNotSuperseded` (FakeTimeProvider, 500 ms into the grace window). |
| M4 | **Fixed** | `MessageSummarizer.AddCapped` caps every list at 200 and adds an "… and K more" field. `InMemorySessionStore.EstimateBytes` counts body, headers, summary fields and warnings on both request and response. Tests: `Summarize_HugeGpvResponse_FieldsCappedWithMoreMarker`, `CapturedBytes_CountsHeadersAndSummaryFields_NotJustBodies`. Remaining gap: R-n4. |
| M5 | **Fixed** | `CwmpClientSession.cs:209` uses `r.MaxCapturedBodyBytes`, which `HttpCwmpTransport.cs:107` sets from capture settings. Tests: `ResponseCapture_TruncatedToTransportLimit`, `Response_CarriesConfiguredCaptureLimit`. |
| M6 | **Fixed** | `SessionFeedViewModel.CoalescePaused/ResumeFromPause` coalesce by id and keep no snapshots. Above 10,000 ids, or on a resync, the feed resyncs fully. Sessions evicted during the pause turn into Removed. Tests: `Pause_CoalescesById_EvictedSessionsDoNotReappear`, `Pause_ManyDistinctSessions_FallsBackToResync_Bounded`. |
| m1 | **Fixed** | `CpeSessionTracker.cs:282-283`: Canceled → `SessionOutcome.Canceled`. Test: `ProxyStoppedDuringInFlightExchange_SessionCanceled`. The CPE still receives a 502 during shutdown (R-n2, cosmetic). |
| m5 | **Fixed** | `CwmpClientSession` catch-all → `End(ProtocolError, "Internal error…")`. `SimulatedCpe.cs:168-178` keeps the loop alive in an Error state. Tests: `UnexpectedException_EndsStoredSessionAsProtocolError`, `UnexpectedSessionException_LoopSurvives_NextTriggerRuns`. |
| m6 | **Fixed** | `SessionFeedPump`: each batch has its own try/catch, each consumer is isolated, a resync is forced and a warning banner is shown. Test: `Pump_ThrowingConsumer_OthersStillServed_BannerShown_NextBatchResyncs`. Caveat: R-n1. |
| m8 | **Fixed** | `PlatformServices.cs:80-84` throws `SecretUnprotectException` (Cryptographic or Format). The editor keeps the ciphertext unless the password is retyped (`CpeProfileEditorViewModel` `_passwordEdited`). Load shows a banner. `SimulationEngine.cs:64-74` refuses Start with a validation error. Test: `UndecryptablePassword_BannerShown_CiphertextKeptOnSave_StartRefused` (fake protector; real DPAPI can't be run on Linux). |
| m10 | **Fixed** | Both validators reject userinfo (`ProxyViewModel` and `SettingsViewModel`, using `UrlUtil.UserInfoRejectedMessage`). The log line (`ProxyServer.cs:101`) and the session target (`CpeSessionTracker.cs:206`) are redacted. `UpstreamForwarder.BuildUpstreamUri` already drops userinfo (it uses `Authority`), so captures are clean too. Tests: `UrlUtil_RedactsUserInfo`, `UpstreamWithCredentials_RejectedWithClearMessage`, `UpstreamUrlWithCredentials_NotForwarded_RedactedInSessionTarget`. |
| m11 | **Fixed** | `ProxyServer.cs:61-62` maps `localhost` to `IPAddress.Loopback`. All start failures now clean up the app, the CTS and TLS, and set Faulted. The VM catch-all sets Faulted and shows a banner. Tests: `Start_Localhost_BindsLoopback`, `Start_UnexpectedFailure_FaultedAndCleanedUp_ThenRestartable`, `StartFailsWithNonBindError_FaultedWithBanner_ButtonUsable`. |
| m12 | **Partially fixed (as declared)** | The sweeper is guarded (`CpeSessionTracker.Sweep` → `SweepCore`; test `Sweep_SinkThrows_DoesNotEscapeTimerCallback`). The custom CA is loaded once per Start (`PrepareTls`), and a missing or invalid file fails Start with `ProxyStartException` (test `Start_CustomCaFileMissing_…`). It is disposed after `EndAllAsync`, once Kestrel has already been disposed, so in-flight sessions can't use a disposed CA. Deferred: the handler is still created under the lock. That is acceptable now that it does no I/O. |
| m13 | **Fixed** | `HeaderRewriter.RewriteSetCookie` adds a `cpePath` parameter and an RFC 6265 §5.1.4 `PathMatches` check, and rewrites Path to "/" with a note (`ProxyRequestHandler.cs:85` passes `req.PathAndQuery`). Notes corrected. Tests: `RewriteSetCookie_PathNotMatchedByCpePath_RewrittenToRoot`, `SetCookiePath_ForUpstreamBasePath_RewrittenSoCpeReplaysIt`. |
| m14 | **Fixed** | The 1.2 s and 2.2 s `Task.Delay` waits are gone; idle, grace and E2E tests run on `FakeTimeProvider`. New negative and fidelity tests: Basic auth through the proxy, the ManagedPerCpe jar, response-direction byte identity, multi-valued header relay. Remaining real-time code is the existing network tests in `NetTests` (5 s caps) and the 10 ms poll in `TestUtil.WaitUntil`, both acceptable. |

### WPF layout commit c8c0861

The approach is correct: each tab holds a ScrollViewer, and the view is sized to the viewport with Min sizes, so star rows and DataGrid virtualization keep a finite height. Fixed widths became star/Auto + MinWidth, toolbars became WrapPanels, and `SessionDetailView` rows changed from 160/* to 1*/2*. It is pure XAML with no code-behind, so MVVM is unaffected, and the `XamlLayoutTests` lint is a reasonable stand-in for rendering on Linux. Issues:
- **R-m1 (Minor)** `MainWindow.xaml:44` sets `SimulatorView MinHeight="760"`. At the default 1280×820 window, the tab viewport is roughly 660–690 px (title bar, header, the always-shown firewall banner, the tab strip and the status bar). So the Simulator tab **always** shows an outer vertical scrollbar, and the feed at the bottom starts partly hidden. Inside it, `SimulatorView.xaml:29` (`DockPanel MinHeight="520"` in a 3* row of about 435 px) also shows the inner editor scrollbar. The result is two nested vertical scrollbars at the default size, and the mouse wheel over the editor never reaches the outer one. **Fix:** set the view MinHeight to the sum of the row minimums (~560) and the editor MinHeight to about 360, or drop the inner ScrollViewer. Check on Windows.
- **R-n5 (Nit)** I can't verify the viewport-bound Width/Height pattern on Linux. At the threshold where a scrollbar appears or disappears, it can oscillate for a frame. Include "resize slowly across MinWidth/MinHeight" in the Windows smoke checklist. `ResizeMode="CanResize"` is the default, so it's redundant but harmless.

### New findings in the main...fix diff

| ID | Severity | Location | Finding | Fix |
|---|---|---|---|---|
| **R-M1** | **Major** | `src/AcsTool.Presentation/Proxy/ProxyViewModel.cs:80-85`, `:213-225` | The `Changed` handler applies persisted proxy settings only while `CanEdit && _server is null`. Nothing reloads them when the proxy stops. If proxy settings are saved in the Settings window while the proxy is running (the footer says "Proxy changes apply on the next Start"), then Stop → Start writes the Proxy tab's stale values back over them. Probe: changed upstream to `http://changed.example:9000/acs` while running, then Stop → Start; the persisted value reverted to the old upstream. | In `StopAsync` (after `State = Stopped`) and in the Faulted paths, call `LoadFrom(_settings.Current.Proxy)`. The tab is read-only while running, so there are no user edits to lose. Add a VM test for save-while-running → Stop → Start. |
| R-n1 | Nit | `src/AcsTool.Presentation/Feed/SessionFeedPump.cs:125-128` | The forced resync after a failure is only delivered when the store produces the next change. On an idle store the list stays out of sync until then. | After `ReportFailure`, also queue an empty flush, e.g. a `Task.Delay(FlushInterval)` then `FlushAsync(empty)`. |
| R-n2 | Nit | `src/AcsTool.Proxy/ProxyRequestHandler.cs:141` | Exchanges canceled by a proxy stop still return 502 to the CPE. The outcome is now correct (m1). | Return 503 for `UpstreamErrorKind.Canceled`. Optional. |
| R-n3 | Nit | `src/AcsTool.Simulator/Engine/SimulationEngine.cs:81-85` | CPEs that are stopping (already removed from `_cpes`) still hold the old gate, so for a moment concurrency can exceed the new limit. The old gate is never disposed. | Documented. Acceptable. |
| R-n4 | Nit | `src/AcsTool.Core/Parsing/MessageSummarizer.cs:225` | List *length* is capped, but a single field *value* is not (one multi-MB parameter value is copied into the summary). It now counts toward the budget, so memory stays bounded. | Truncate field values to about 1–4 KB with "…". |
| R-n6 | Nit | `ProxyViewModel.cs:80-85` | A Settings-window save while stopped overwrites unsaved edits on the Proxy tab. That is arguably correct, since the Settings window showed the persisted values. | None needed; optionally mention it in the footer text. |

I found no new Blockers. Thread safety of the new code is fine: `UpdateLimits` and `Limits` take `_gate`; `_customCa` is read and written under `_lock`; the feed VM's pause state is touched only on the UI thread via `Apply`.

### Build and tests (private clone `/tmp/rr-acs` @ dfae3b3)

- Build (Release, also `CI=true --no-incremental`): **0 warnings, 0 errors**.
- Tests, full suite with Perf included: **484/484 passed**, 0 skipped. Core 309, Proxy 67, Simulator 63, Presentation 45. Previously 447 without Perf (448 with), so **+36 tests**.
- Line coverage before → now: Core 87.2 → **87.0** %, Proxy 89.8 → **89.3** %, Simulator 89.8 → **89.7** %, Presentation 83.5 → **85.1** %. All are above the 80 % gate.
- Not verified: WPF rendering and the Windows smoke checklist (c8c0861 especially), real DPAPI (m8 tested only with a fake protector), and HTTPS/TLS at runtime for the CustomCa path (only the missing-file path is tested).

### Deferred items: does each block the merge?

| Item | Blocks? | Reasoning |
|---|---|---|
| m12 (handler under lock) | No | With the CA preloaded, handler creation does no I/O and is cheap. The deferral is reasonable. |
| m2 (late empty POST → orphan row) | No | Cosmetic: an extra orphan row. No data loss and no wrong outcome on the real session. Fix in M5. |
| m3 (cookie-pair cross-CPE correlation) | No | Needs several CPEs that share a cookie value (e.g. an LB cookie) and fall through to the cookie tier. Connection id and IP+DeviceKey cover the common case. Track it for M5. |
| m4 (simulator captures post-response cookies) | No | Only the capture is inaccurate; traffic on the wire is correct. |
| m7 (pretty-print race) | No | UI cosmetic. |
| **m9 (password in a plain TextBox)** | **No, but bundle it with R-M1** | See below. |
| m15 (Settings ✕ keeps edits, OnClosing hang) | No | Edge-case UX. No data corruption. |
| n1–n8 | No | Nits. |

**m9 call.** I don't consider m9 a merge blocker for *this* branch, for three reasons. The branch didn't introduce it, and merging strictly improves security (m8, m10). The secret is a lab CPE→ACS credential that the user types in themselves, and it's already DPAPI-encrypted at rest. The exposure is shoulder-surfing or screen-sharing, not leakage into storage, logs or exports. I'd treat it as a release blocker for the MVP sign-off rather than a merge blocker.

That said, the swap is small, about 30–40 lines:
- a `PasswordBox` with a small attached-property helper (`PasswordBoxAssistant.BoundPassword`) or a `PasswordChanged` code-behind handler that sets `vm.Password`;
- a "show" toggle if wanted;
- a guard so that loading a profile doesn't set `_passwordEdited` (the m8 logic). The toolkit's equal-value check already prevents a loop, but add a VM test that loading plus a re-push of the same value keeps the ciphertext.

This branch already edited that exact line (`SimulatorView.xaml:89`), and a follow-up commit is needed for R-M1 anyway, so I recommend doing m9 in that same commit. If DevOrchestrator wants it as a hard condition, I won't object: the cost is low and the risk is contained.

### Required before merge

1. Fix R-M1 and add its test. Then M1 becomes Fixed.

Recommended in the same commit: m9 (PasswordBox) and R-m1 (Simulator MinHeight). After that, run the Windows smoke checklist, including resizing across the minimum sizes.
