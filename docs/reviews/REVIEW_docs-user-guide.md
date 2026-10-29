# Review: docs/user-guide (branch docs/user-guide @ 3ede6e9, base main 7b8c906)

Reviewer: ReviewerBot · 2026-10-29 · review only, nothing committed or pushed.
Scope: `docs/user-guide/*.md` (9 pages + roadmap.md) and the root README link. The code was read at 3ede6e9 in the worktree, read-only. Tests were run in a private clone at `/tmp/rv-docs`.

## Verdict

**Fixes needed before push. All of them are doc-only.** The guide is accurate on the large majority of points. settings-reference.md keys, types, defaults and ranges are correct, and so are all Settings window, Proxy, feed, detail and Simulator labels, badges, paths, logging, the profile format, and the build, CI and test facts. Two Major issues remain:

1. The guide claims exchange-level proxy notes are visible in the UI, but they are never rendered.
2. Simulator walkthrough 3 doesn't work as written.

Then there is a set of Minor wording and label mismatches, mostly in troubleshooting.md.

### Verified at runtime (Linux, /tmp clone, Category!=Perf)
545 tests: 542 passed, 3 skipped, 0 failed.
- Presentation 54
- Core 325 (3 skips)
- Proxy 99
- Simulator 67

The skips are `Dns_NonexistentInvalidTld_Dns`, `PemBundle_IntermediateFirst_TrustsTheRoot` and `Utf8BomPem_Loads`. This matches architecture.md:114-117 exactly.

### Not verified (Windows-only): don't treat as confirmed
- WPF rendering: layout, colours, the 🔒 glyph, PerMonitorV2 DPI, and window sizes beyond the XAML values.
- DPAPI behaviour at runtime. Only the code was checked (entropy `AcsTool.v1`, CurrentUser).
- Windows Firewall prompt, `publish.ps1` output, `--smoke-exit` on Windows.
- HTTPS upstream against a real ACS. Only unit and integration tests passed.

## Doc findings

### Major

**M1. Exchange notes are claimed visible but are never shown in the UI.**
Evidence:
- ProxyRequestHandler.cs:85-88 passes exchange notes to CompleteExchange.
- InMemorySessionStore.cs:184 stores them only on ExchangeRecord.
- SessionDetailView.xaml:23 binds only the session-level `Notes`.
- No exchange-level notes view exists.

Affected lines:
- proxy-mode.md:27 ("every change … is noted on the exchange")
- proxy-mode.md:125 and the table at 137-143 (`AuthChallenge` note)
- proxy-mode.md:155 (3xx Location note)
- proxy-mode.md:191/197 notes column
- proxy-mode.md:299 ("The first exchange carries the note `AuthChallenge`")
- proxy-mode.md:305-306 ("look at the notes on the InformResponse exchange")
- ui-tour.md:266 (example `• ACS answered the Inform with a SOAP Fault`)
- troubleshooting.md:45 ("a session note says so")

Fix, option A (preferred, code): show `ExchangeItemViewModel.Notes` in the Summary tab for the selected exchange. Then the docs stand as written.
Fix, option B (doc): reword these lines as "recorded on the exchange (not yet shown in the UI; see roadmap)" and add a roadmap.md item "Show per-exchange notes". In ui-tour.md:266, use only notes that really are session-level.

**M2. Simulator walkthrough 3 doesn't work as written.** simulator-mode.md:234: "Untick the custom event and send again: RetryCount … is 1."
PendingEventQueue keeps events after a failed Inform (SimulatedCpe.cs:392-395), so `99 BOGUS` is still queued and is resent no matter what the Events ticks say.
Fix: "3. **Stop** and **Start** the CPE (this clears the pending events), untick the custom event, then send again…". Also rework the RetryCount sentence: Stop/Start resets RetryCount to 0. To show RetryCount=1, instead make the ACS unreachable for one attempt, then fix it.

### Minor

| # | Location | Doc says | Code (file:line) | Exact fix |
|---|---|---|---|---|
| m1 | settings-reference.md:110 | connect timeout "per address" | ConnectCallbackFactory.cs:504-505: one CTS is created before the address loop, so the limit is the total across all resolved addresses | "TCP connect timeout to the ACS, total across all resolved addresses (1–300 in the window)." |
| m2 | settings-reference.md:120 | profile `tls`: "not editable and only its `mode` default `Strict` matters" | HttpCwmpTransport.cs:309 passes `profile.Tls`, so a hand-edited profile `tls` is honoured | "…as `tls` in a profile file. It is not editable in the window, but a hand-edited value is used." |
| m3 | roadmap.md:12 | "Profiles always use Strict validation" | same as m2 | "Profiles use Strict unless `tls` is hand-edited in the profile file" |
| m4 | architecture.md:68 | "one `HttpMessageInvoker` per proxy start" | CpeSessionTracker (CreateLocked, ≈:211): one handler/invoker per CWMP session, AllowAutoRedirect=false | "one `HttpMessageInvoker` per CWMP session" |
| m5 | troubleshooting.md:9 | "Close the banner with ✕" | MainWindow.xaml banner button text is `dismiss` | "Close the banner with **dismiss**." |
| m6 | troubleshooting.md:13 | lists only the "could not be loaded" CustomCa banner | CpeSessionTracker.cs:61 also gives "TLS mode CustomCa needs a CA certificate file (Settings → TLS)." | Add a row for that message (cause: `customCaPath` empty). See bug (f). |
| m7 | troubleshooting.md:15 | suggests switching to Fixed mode | contradicts proxy-mode.md:45, which recommends no path in the upstream URL | Make it match proxy-mode.md:45: "remove the path from the upstream URL and put it in the CPE's ACS URL" |
| m8 | troubleshooting.md:18 | "fields outlined in red (hover for the message)" | SimulatorView.xaml has no validation adorners; errors are listed under the command bar | "Fix the fields listed in the error list under the command bar." |
| m9 | troubleshooting.md:19 | "Cannot start 'X': …" covers the serial-already-running case | SimulatorViewModel.cs:181 covers editor validation only; the engine throws ProfileValidationException (SimulationEngine.cs:11, :90), which is shown as "Profile is invalid: …" | Add a row: ✖ *Profile is invalid: … A CPE with serial … is already running* |
| m10 | troubleshooting.md:27-28 | "green Listening line" / "Listening on" | ProxyViewModel StateText is `● Running on …` | "the **● Running on** line" / "one of the **Running on** addresses" |
| m11 | troubleshooting.md:63 | heading `⚠ Connect failed` / `⚠ Timeout` | there is no such badge; it is `✖ Upstream error` with CONNECT/TIMEOUT in the red line (SessionItemViewModel.cs:113) | "### `✖ Upstream error` (CONNECT … / TIMEOUT …)" |
| m12 | troubleshooting.md:66 | `⚠ Auth failed` | badge glyph is 🔒 (SessionItemViewModel badge strings) | "`🔒 Auth failed`" |
| m13 | troubleshooting.md:76 | `✓ Completed` | badge is `✔ Completed` | use ✔ |
| m14 | tr069-primer.md:50, :131 | "Messages tab" / "Messages list" | no such tab; the detail pane has an exchanges grid | "in the detail pane's exchanges list" |
| m15 | tr069-primer.md:59 | column "Msgs" | feed column header is **Ex** | "**Ex** counts the exchanges" |
| m16 | proxy-mode.md:191 | "A Fault answering a later message does not end the session" | CpeSessionTracker EvaluateEnd :280-350: a later Fault with HTTP 500 ends the session as HTTP error | "…does not end the session unless it is sent with a non-2xx status (then the session ends as HTTP error)." |
| m17 | proxy-mode.md:228 | the Response column shows the error's short form | for BODYTOOLARGE the Response column shows the HTTP status; the short form is only in the red line | add "(for BODYTOOLARGE the Response column shows the status)" |

### Nit
- simulator-mode.md:219: "untick `1 BOOT`, tick `0 BOOTSTRAP` and `1 BOOT`" reads as self-contradictory. Use "tick `0 BOOTSTRAP` (keep `1 BOOT` ticked)".
- ui-tour.md:121: "sorted by name". Profiles are sorted only at load (JsonProfileStore). Use "sorted by name when loaded".
- ui-tour.md:197: `Stopped` rows are removed from the list on Stop, so `Stopped` is rarely seen. Mention this.
- ui-tour.md:238: the red line is LastError, **or** the last session note for error outcomes (SessionItemViewModel.cs:113).
- proxy-mode.md:239: with `CANCELED` the proxy may still answer 502 before closing (ProxyRequestHandler RelayAsync :357-367). Use "(502 or connection closed)".
- proxy-mode.md:56: the IgnoreErrors banner is dismissable and is shown only when the upstream is https (ProxyViewModel UpdateAdvisories).
- troubleshooting.md:90: "after a restart" should be "on the next proxy **Start**". The value is copied at StartAsync.
- getting-started.md:72: `--smoke-exit` also writes settings.json, via ShutdownAsync.
- ui-tour: Pretty/Raw is per exchange, not global.
- simulator-mode.md:198: `⊘ Canceled` also covers an rpcBehaviors NoResponse hang that is ended by Stop.
- architecture.md:91: `HttpClient` should be `HttpMessageInvoker` (HttpCwmpTransport.cs:54, :79).

### Verified OK (no change)
- settings-reference: every key, type and default. AppSettings.cs:18-34, with camelCase and string enums from the source-gen context. Window ranges from SettingsViewModel.cs:24-37.
- The unused `simulator.*`, `ui.*`, `logging.*` and `clientCert` keys are correctly marked unused (grep found no consumers).
- ui-tour: every Settings window label, footer and IsDefault/IsCancel. F5 and Ctrl+L. Status bar format. Banner colours (Converters.cs). Feed filters and ErrorsOnly. Pause/Clear. Detail header, TLS line, truncation banner "Captured X of Y". MessageSummarizer 200-item cap text. All Simulator labels, templates, forced params and validator messages.
- Paths %APPDATA%\AcsTool, profiles and %LOCALAPPDATA%\AcsTool\logs. Serilog `acstool-.log` (daily, 7 files, 10 MB, Information).
- Internals: BannerService same-key replace, feed pump 100 ms / 500 ids, store channel 10,000 DropOldest.
- Build and CI: global.json 10.0.403, Version 0.1.0, AnalysisLevel 10.0-recommended, ci.yml checkout@v7 / setup-dotnet@v6, build.sh `--no-coverage` / `--include-perf`, the ACSTOOL_SKIP_NET_TIMEOUT CI guard (build.sh:11, build.ps1:8, ci.yml:15).
- Fixtures cover CWMP 1.0–1.2.
- Roadmap: no not-yet-available item is presented as a feature elsewhere. The "Not available yet" lists in README.md:63-66 and simulator-mode.md:259-269 only point to roadmap.md. M5 items (7443, certs dir, D1–D9) match docs/M5_TASKS.md.
- Links: root README.md:6 → docs/user-guide/README.md resolves. A script checked every relative link and #anchor in README.md and docs/user-guide/*.md: 0 broken.

## Bug triage (writer's suspects a–f + others)

| ID | Item | Verdict | Severity | Evidence | Fix |
|---|---|---|---|---|---|
| a | Simulator reports HTTP error where the proxy says ACS fault (SOAP Fault on HTTP 500) | Real bug (inconsistent classification); the docs already describe it (simulator-mode.md:194, troubleshooting.md:69) | Minor | CwmpClientSession.cs:69-81 checks the HTTP status before parsing the Fault; CpeSessionTracker.cs:299-310 checks the Fault first | Parse the SOAP Fault before the non-2xx check (except 401/407) and return AcsFault. Then drop the troubleshooting section and the simulator-mode note. |
| b | No default profile on first run | By design (SimulatorViewModel.LoadAsync seeds nothing). Doc issue in IMPLEMENTATION_NOTES.md:61-69 | Minor (doc) | IMPLEMENTATION_NOTES.md smoke step 8 says "select the default profile"; step 1 says "Proxy running" but the actual text is "Proxy: Running" | Step 8: "click **+ From TR-098**, then select it". Step 1: "status bar shows `Proxy: Running`". Optionally seed a sample profile on first run. |
| c | settings.json written on every exit (also `--smoke-exit`) | By design (MainViewModel.ShutdownAsync saves `_settings.Current`) | Nit | MainViewModel ShutdownAsync | Optional: skip the save when nothing changed. Document it in getting-started (see Nit). |
| d | Restore defaults wipes TLS pins, CA path and maxRequestBodyBytes | Real bug | Minor | SettingsViewModel.RestoreDefaults (≈:141) → LoadFrom sets `_base` (:59) → Build uses `_base.Proxy with {…}` (:85), so hand-edited `proxy.tls` and `maxRequestBodyBytes` reset on Save | Keep `_base.Proxy.Tls` and `MaxRequestBodyBytes` in RestoreDefaults (as is done for Simulator/Ui/Logging), or confirm first and document it in settings-reference. |
| e | N (notification) column never produces `4 VALUE CHANGE` | By design for now (PLAN T7.3 / M7; roadmap.md:161 says so). Latent bug: `CpeTrigger.Edit` is handled but never posted | Minor (latent) | SimulatedCpe.cs:157-163; ParameterStore.cs:246-265 ApplyLocalEdit returns true only for active notification, so passive edits (FR-S-15) wouldn't queue the event even once wired | In M7, post Edit from the editor, and queue VALUE CHANGE for passive notification (next Inform) as well as active. |
| f | CustomCa error tells the user to use "Settings → TLS", which doesn't exist yet | Real bug (message) | Minor | CpeSessionTracker.cs:61 | Message: "TLS mode CustomCa needs `proxy.tls.customCaPath` in settings.json." Add the troubleshooting row (m6). |
| g | Exchange notes are recorded but never displayed | Real UI gap (root cause of M1) | Minor (code) / Major (doc) | SessionDetailView.xaml:23; InMemorySessionStore.cs:184 | Bind exchange Notes in the Summary tab, or reword the docs (M1). |
| h | Connect timeout is a total, not per address | By design, but undocumented. With multi-A/AAAA hosts a slow first address eats the budget | Nit | ConnectCallbackFactory.cs:504-505 | Doc fix m1. A per-address budget is optional. |

Already known and tracked (not new): roadmap m2/m3/m4, CustomCa NET10-01/02 (UTF-8 BOM PEM, intermediate-first bundle; these are the 2 skipped tests).

---

# Re-review 1 (HEAD 3346833, one commit on 3ede6e9) · 2026-10-29 (Europe/Warsaw)

**Verdict: OK to push after one small Minor fix (N1). Every original finding is Fixed.** Nothing was committed or pushed. No branches were switched, and fix/doc-findings was not touched.

## Scope checks
- **Docs only:** yes. `git diff --name-only 3ede6e9 3346833` lists 10 files, all `.md`: `docs/IMPLEMENTATION_NOTES.md` plus 9 under `docs/user-guide/`. No `src/`, `tests/` or build files changed. Nothing was rebuilt, because the code is identical to 3ede6e9 (545 / 542 / 3 from the first review still stands).
- **Links and anchors:** the same script as before found 85 relative links in README.md and docs/user-guide/*.md, 0 broken. The new anchors resolve:
  - `#known-issues-fixes-planned`
  - `#customca-error-points-to-settings--tls`
  - `#restore-defaults-wipes-hand-edited-tls-settings`
  - `roadmap.md#planned-fixes-for-known-issues`
  - the moved `#the-proxy-says-acs-fault-the-simulator-says-http-error-for-the-same-session`
- **Stale text:** a grep for the old wording found nothing. Searched for: `per address`, `Msgs`, `Messages tab/list`, `Listening`, `✓ Completed`, `⚠ Connect`, `⚠ Auth`, `outlined in red`, `per proxy start`, `HttpClient`, `always use Strict`, `after a restart`, `✕`.

## Status of earlier findings

| ID | Status | Notes |
|---|---|---|
| M1 exchange notes | **Fixed** | Every affected line is reworded with "recorded … not shown in the UI yet", and there is a roadmap item plus a troubleshooting known issue. The ui-tour.md:266 examples are now real session-level notes: `ambiguous correlation by IP` (CpeSessionTracker.cs:196), `CPE disconnected before response relayed` (ProxyRequestHandler.cs:164), and `ACS fault <code> <string>` (simulator, CwmpClientSession.cs:80, set via End → session note, :220-225). Walkthrough 5 now uses the Headers tab. Capture uses `result.Headers` before rewriting (ProxyRequestHandler.cs:83). |
| M2 walkthrough 3 | **Fixed** (see N1 for step 4) | Traced step by step below. |
| m1 connect timeout total | Fixed | settings-reference.md:110 |
| m2 profile tls honoured | Fixed | settings-reference.md:120, :218 (profile table) |
| m3 roadmap Strict | Fixed | roadmap.md:12 |
| m4 invoker per session | Fixed | architecture.md:68, which also adds "no automatic redirects" (correct) |
| m5 dismiss | Fixed | |
| m6 CustomCa empty-path row | Fixed | The "Proxy failed to start: " prefix is correct (ProxyViewModel.cs:208). |
| m7 Digest/Fixed contradiction | Fixed | troubleshooting.md banner row and the Auth-failed section both now say to move the path to the CPE's ACS URL |
| m8 error list | Fixed | SimulatorView.xaml:39 (the Errors ItemsControl is docked at the top under the command bar) |
| m9 Profile is invalid | Fixed | Format `Field: Message` (ProfileValidator.cs:8). Serial (SimulationEngine.cs:90) and Password (:72) examples are correct. "Cannot start" row narrowed correctly (SimulatorViewModel.cs:181). |
| m10 Running on | Fixed | |
| m11 Upstream error heading | Fixed | |
| m12 🔒 | Fixed | |
| m13 ✔ | Fixed | |
| m14 exchanges list | Fixed | |
| m15 Ex | Fixed | |
| m16 later Fault + 500 | Fixed | |
| m17 BODYTOOLARGE | Fixed | |
| 11 nits | Fixed (all 11) | simulator-mode:219; ui-tour:121 (+ "added at the end", SimulatorViewModel.cs:135), :197, :238, Pretty; proxy-mode:239, :56 (banner set only in StartAsync → UpdateAdvisories, ProxyViewModel.cs:174, :251-253, so "comes back on the next Start" is right); troubleshooting:90; getting-started:72; simulator-mode:198 (`simulated hang` = Canceled, CwmpClientSession.cs:160); architecture:91 (HttpCwmpTransport.cs:54) |
| (a) (d) (f) + exchange notes as Known issues | Fixed / accurate | troubleshooting.md "Known issues (fixes planned)" and roadmap "Planned fixes for known issues" describe current behaviour correctly |
| (b) IMPLEMENTATION_NOTES steps 1 and 8 | Fixed | `Proxy: Running`; "+ From TR-098 first" |

New text also checked and correct: architecture.md:30 "413 and a one-shot session labelled *Request body too large*" (ProxyRequestHandler.cs:170, CpeSessionTracker.cs:153 `oneShot: true`, ends ProtocolError) and architecture.md:36 (step 6 notes).

## Walkthrough 3 traced against the code
- **Step 1, tick `99 BOGUS`:** Send Inform now posts the editor's *current* ticks (`vm.SelectedEvents()` → `CpeTrigger.Manual`, SimulatorViewModel.cs:233-236), so ticking works even on a running CPE. "Start the CPE if it isn't running" is harmless but redundant, because Send Inform now starts it (SimulatorViewModel.cs:229). Optional nit.
- **Step 2, Fault → ACS fault or HTTP error, Error, Retry 1:** correct. The events are kept because `InformAccepted` is false (SimulatedCpe.cs:222-226), and the state becomes `Error`, RetryCount+1 (:242).
- **Step 3, Stop + Start, then untick and send:** correct. Stop removes the CPE (SimulationEngine.cs:119). Start creates a new `SimulatedCpe` (:95) with an empty queue and RetryCount 0 (SimulatedCpe.cs:107). Start without boot posts nothing (:103), and the untick after Start takes effect because ticks are read at Send time.
- **Step 4, RetryCount demo:** the mechanism is right. An unreachable proxy gives a CONNECT error → Upstream error, Retry 1. The next Inform is built with `State.RetryCount` = 1 (SimulatedCpe.cs:207) and shown in the Summary (MessageSummarizer.cs:90). Completed resets it to 0 (:235-237). There is one gap; see N1.

## New issues

| ID | Sev | Location | Issue | Fix |
|---|---|---|---|---|
| N1 | Minor | simulator-mode.md:235 (walkthrough 3 step 4) | "point the profile at the proxy and stop the proxy". If the user changes the URL on a running CPE, nothing changes: a running CPE keeps its start-time profile and transport (SimulatedCpe.cs:105), and Send Inform now doesn't restart an already-running CPE (SimulatorViewModel.cs:184-187). The Inform then still goes to the old URL and may succeed, so Retry never becomes 1. | "…(e.g. set **URL** to the proxy, `http://127.0.0.1:7547/acs`, then **⏹ Stop** and **▶ Start CPE** so the CPE uses it, and stop the proxy): …" |
| N2 | Nit | simulator-mode.md:232 (step 1) | "Start the CPE if it isn't running" is unnecessary, because Send Inform now starts it. | Drop it, or say "(Send Inform now starts the CPE if needed)". |

Not verified (Windows-only, unchanged): WPF rendering, DPAPI at runtime, Firewall, HTTPS against a real ACS.
