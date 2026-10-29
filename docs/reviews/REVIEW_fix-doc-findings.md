# Review: fix/doc-findings (8d8c10c..b8d33bb, base main 7b8c906)

Reviewer: ReviewerBot · 2026-10-29 (Europe/Warsaw) · review only.

I read the branch from a private clone at `/tmp/rv-fdf` (detached at b8d33bb). I did not switch branches in `/workspace/projects/acs-tr069-tool`, which is checked out at fix/doc-findings by CoderBot, or in the docs worktree. Nothing was committed or pushed.

## Verdict

**OK to merge.** There are no Blocker or Major findings. All four fixes are correct, small and tested. The fault classification in (a) now matches the proxy's `EvaluateEnd` for the Inform. Two pre-existing simulator/proxy mismatches outside (a)'s scope are listed below as follow-ups; neither is a regression. The docs branch needs a follow-up commit once this merges (see the last section).

## CI (verified with the GitHub API)

Run 40856922430 (`ci`, run #41, push) on `fix/doc-findings` at head_sha `b8d33bbf…`: **completed / success**. It started at 11:21 and finished at 11:23 (UTC+1).
- **linux** job: success. Steps include the network-skip guard and `./build.sh`.
- **windows** job: success.
  - `build.ps1 -NoCoverage`: 0 warnings, 0 errors.
  - Tests: Presentation 57, Core 325 (323 passed, 2 skipped), Proxy 102, Simulator 72. That is 556 total, 554 passed, 2 skipped; the DNS test runs on Windows.
  - `publish.ps1` and `--smoke-exit` passed.

## Local build and tests (/tmp clone, `./build.sh`, Perf excluded, ephemeral ports)

- Build: 0 warnings, 0 errors (analyzers on, AnalysisLevel 10.0-recommended).
- Tests: **556 total, 553 passed, 3 skipped, 0 failed.** That is 11 more than main (545): Presentation +3 (57), Proxy +3 (102), Simulator +5 (72), Core unchanged (325).
- Skipped: `Dns_NonexistentInvalidTld_Dns` (the resolver answers on this box), plus NET10-01 and NET10-02 (known CustomCa issues).
- Coverage: every changed line is hit.

  | File | Lines | Hits |
  |---|---|---|
  | CwmpClientSession.cs | 64-89 | all |
  | SettingsViewModel.cs | 145-151 | 2 |
  | SessionDetailViewModel.cs | 192, 194 | 2 |
  | CpeSessionTracker.cs | 61 | 3 |

  Package line coverage from each project's own tests: Simulator 89.0 %, Proxy 90.1 %, Presentation 86.3 %, Core 86.2 %.
- **Mutation check.** I reverted the three source fixes to 7b8c906 and re-ran only the new tests. They fail as expected:
  - `FaultInformResponse_WithHttp500_AcsFault_LikeProxy`
  - `Settings_RestoreDefaults_KeepsHandEditedTlsAndMaxRequestBody`
  - all 3 cases of `Start_CustomCaWithoutPath_MessagePointsToSettingsJson`

  So the tests aren't tautological. The 401/407 and "500 with non-Fault or InformResponse body" tests also pass on the old code; they guard the new check order rather than prove the fix, which is fine. I then restored the source to b8d33bb.
- Merge check: `git merge-tree` of docs/user-guide (329062b) with b8d33bb merges **cleanly**. Both branches touch docs/IMPLEMENTATION_NOTES.md, but in different hunks.

## (a) Fault classification compared with the proxy

Proxy (`CpeSessionTracker.EvaluateEnd`, CpeSessionTracker.cs:274-340) and simulator (CwmpClientSession.cs:64-89 after the change):

| Case | Proxy | Simulator (new) | Match |
|---|---|---|---|
| Inform → Fault, 2xx | AcsFault | AcsFault | ✔ |
| Inform → Fault, 500 | AcsFault (Fault checked before status) | AcsFault | ✔ (the fix) |
| Inform → Fault, 403 | AcsFault (Fault check before the 403 rule) | AcsFault | ✔ (not tested; see n1) |
| Inform → Fault, 401 | AuthChallenge, then auth retry; second 401 → AuthFailed | Transport does the auth retry; final 401 → AuthFailed | ✔ |
| Inform → Fault, 407 | HttpError (status ≥ 400) | HttpError | ✔ |
| Inform → non-Fault 500 / InformResponse with 500 | HttpError | HttpError | ✔ |
| Later exchange → Fault, 500 | `SawAcsFault=true`, then status ≥ 400 → **HttpError** | **HttpError** | ✔ consistent; keeping HttpError is correct |
| Later exchange → Fault, 2xx | `SawAcsFault=true`, session continues; final outcome **AcsFault** (`FinalOutcome`) | Note "ACS sent Fault, which is not a request", continues, final **Completed** | ✘ pre-existing (m1) |
| Inform → 403, no Fault | **AuthFailed** | **HttpError** | ✘ pre-existing (m2) |

So the in-session 500 Fault staying HttpError is consistent with the proxy. No flag on (a) itself.

## Findings

### Blocker / Major
None.

### Minor (pre-existing, follow-ups; not regressions, not blocking)
- **m1. A later Fault with 2xx ends Completed in the simulator but AcsFault in the proxy.**
  - Where: CwmpClientSession.cs:151-158, where a Fault in the loop is noted and skipped, and :140, which returns `Completed`.
  - Proxy behaviour: CpeSessionTracker.cs:301 sets `SawAcsFault`, and `FinalOutcome` then gives AcsFault.
  - Fix: track `sawAcsFault` in the client loop and end with `AcsFault` instead of `Completed` when the final empty response arrives. Add a sim→proxy→FakeAcs test asserting that both sides agree.
- **m2. 403 on the Inform: proxy says AuthFailed, simulator says HttpError.**
  - Where: CwmpClientSession.cs:76-83 has no 403 rule (same in the loop at :130-137).
  - Proxy behaviour: CpeSessionTracker.cs:317-320.
  - Fix: map 403 to `AuthFailed` with the note "ACS refused the CPE (403)", or document the difference. The user guide's simulator outcome table currently documents the current behaviour ("any other non-2xx" → HTTP error).
- **m3. No end-to-end test for the consistency (a) claims.** The new test is unit-level against FakeTransport. Fix: add one `AcsTool.Simulator.Tests` scenario where the simulator goes through the proxy to FakeAcs, which answers the Inform with Fault + 500. Assert that the proxy session and the simulator session are both `AcsFault`. That pins the "like the proxy" contract for future refactors.

### Nit
- **n1.** No test for Inform → Fault + **403**. The new order makes it AcsFault, which matches the proxy. Fix: add `[InlineData(403, SessionOutcome.AcsFault)]` to a Fault-status theory.
- **n2. Uninformative note for a plain SOAP Fault.** CwmpClientSession.cs:74 builds the note `ACS fault {FaultCode} {FaultString}`. For a SOAP Fault without a `cwmp:Fault` detail, the parser returns `FaultCode 0` and an empty string (CwmpParser.cs:410-413), so the note reads `ACS fault 0 `. This is more likely now that 500 Faults from generic SOAP stacks are classified here. Fix: if `FaultCode == 0`, use `SoapFaultCode` / `SoapFaultString` (CwmpFault, Messages.cs:81).
- **n3. (g) is checked by structure only.** XamlLayoutTests checks the XAML structure. The DetailTests test mostly re-checks `Notes`, which already existed; only `HasNotes` and the `PropertyChanged("")` refresh are new. That's acceptable, and IMPLEMENTATION_NOTES already says it "needs a look in the Windows smoke run". Add a manual step to the smoke checklist: "select an exchange with a Set-Cookie rewrite → Summary shows **Proxy notes**".

### Verified OK
- **(d)** SettingsViewModel.cs:144-151 keeps `Tls` and `MaxRequestBodyBytes` from `_base.Proxy`. These are the only `ProxySettings` members that `Build()` (:85-100) doesn't set from the window (AppSettings.cs:18-23). Everything else, and the whole Capture section, still resets as before. The test round-trips through `ISettingsStore`, including pins, CA path and the client-cert fields.
- **(f)** CpeSessionTracker.cs:61 now says "TLS mode CustomCa needs proxy.tls.customCaPath in settings.json." The test covers null, empty and whitespace.
- **(g) MVVM and binding**
  - The Summary tab's DataContext is `SelectedExchange` (SessionDetailView.xaml:46). `Notes` and `HasNotes` resolve on `ExchangeItemViewModel`.
  - `BoolToVisibility` is defined in Themes/Light.xaml:4 and is already used in this view at :7.
  - `ExchangeItemViewModel.Update` raises `PropertyChanged(string.Empty)` (SessionDetailViewModel.cs:237), so `Notes` and `HasNotes` refresh when an in-progress exchange completes.
  - Simulator exchanges never carry notes (CwmpClientSession.cs:213 passes none), so the "Proxy notes" header never appears for simulator sessions. The label is correct.
- **(g) thread safety:** `Record.Notes` is an immutable snapshot list from the store. Updates arrive on the UI thread through the feed pump, the same as for the other exchange properties.
- **(g) memory:** no new storage; the notes were already kept on `ExchangeRecord`. HeaderRewriter de-duplicates notes (HeaderRewriter.cs:133-139), so there are a few per exchange, bounded by the response header count. They are retained under the existing `capture.maxSessions` and budget eviction.
- **Analyzers:** clean (0 warnings locally and in both CI jobs).
- **IMPLEMENTATION_NOTES (b8d33bb):** accurate.

## User-guide updates needed after merge (docs/user-guide at 329062b; N1 and N2 already fixed there)

N1 and N2 were fixed in 329062b (simulator-mode.md:232 and :235). Line numbers below are at 329062b.

- **(a)**
  - simulator-mode.md:194, "HTTP error" row: drop "This includes a SOAP Fault sent with HTTP 500 … known issue".
  - simulator-mode.md:195: "SOAP Fault **and a 2xx status**" becomes "a SOAP Fault (any status except 401/407)".
  - simulator-mode.md:233, Walkthrough 3 step 2: the session always ends **⚠ ACS fault** (the note shows `ACS fault <code> <string>`), whatever the HTTP status.
  - troubleshooting.md:98-99: remove the "proxy says ACS fault, simulator says HTTP error" known issue. simulator-mode.md:194 links to that anchor, so remove the link too.
  - roadmap.md:24: remove the item.
- **(d)**
  - settings-reference.md:175-181: Restore defaults now keeps `proxy.tls` and `proxy.maxRequestBodyBytes`. Drop the "**gone**" bullet, the 10 MiB bullet and the back-up advice.
  - proxy-mode.md:108: drop the "Careful" box.
  - ui-tour.md:300: drop "Also resets … `tls` and `maxRequestBodyBytes`" and say they are kept.
  - troubleshooting.md:58: keep only the "app writes settings on exit" cause.
  - troubleshooting.md:101-102 and roadmap.md:25: remove.
- **(f)**
  - troubleshooting.md:13: update the quoted message to *TLS mode CustomCa needs proxy.tls.customCaPath in settings.json.* and drop "despite the message (known issue)".
  - troubleshooting.md:104-105 and roadmap.md:26: remove.
- **(g)** Change every "not shown in the UI yet" to "shown under **Proxy notes** on the Summary tab when you select the exchange":
  - proxy-mode.md:27, 125, 137, 143, 145, 157, 199, 301, 310 (Walkthrough 5 can go back to reading the notes);
  - simulator-mode.md:228;
  - troubleshooting.md:47;
  - ui-tour.md:266, which should also gain a "Proxy notes" bullet in the Summary tab section;
  - architecture.md:69.

  Remove troubleshooting.md:107-108 and roadmap.md:23.
- After that, troubleshooting "Known issues (fixes planned)" and roadmap "Planned fixes for known issues" are empty. Delete both sections and the links that point to them: settings-reference.md:181, simulator-mode.md:194, and the roadmap links in the (g) lines above. Then re-run the link and anchor check.
- If m1 and m2 are deferred, they could be added as new known issues instead: a later in-session Fault gives Completed in the simulator; 403 gives HTTP error in the simulator but Auth failed in the proxy.

Not verified (Windows-only): rendering of the "Proxy notes" block in WPF.

---

# Re-review: docs/user-guide 329062b..3f9a97c (merges of fix/doc-findings and tester/doc-findings, plus doc updates) · 2026-10-29 (Europe/Warsaw)

**Verdict: OK TO MERGE into main.** There are no Blocker, Major or Minor findings, only 2 Nits that can go in a later doc commit. Nothing was committed or pushed and no branches were switched. The worktree is clean at 3f9a97c.

## Merge and scope checks
- **a28c564** (merge of fix/doc-findings): `git diff b8d33bb a28c564 -- src tests build.* publish.ps1 .github Directory.Build.props global.json` is empty, so no stray conflict edits went in. Relative to b8d33bb it adds only the user guide, the README link and the docs-side IMPLEMENTATION_NOTES smoke-step edits. Relative to 329062b, docs/user-guide is unchanged.
- **7a0f5eb**: docs only (8 `.md` files).
- **ea92dd5** (tester/doc-findings, parent b8d33bb) and **fb1f6c2** (its merge): only `docs/TEST_REPORT_M0-M4.md` §12 and three new test files. That matches the author's claim. `git diff --quiet b8d33bb 3f9a97c -- src` shows src/ is identical to b8d33bb.
- **0ad69f9 and 3f9a97c**: docs only (7 `.md` files in docs/user-guide).

## Tests (private clones, `dotnet test -c Release --filter Category!=Perf`, no servers; the tests use FakeAcs and proxy Port 0)
- **At 7a0f5eb:** 556 total, 553 passed, 3 skipped, 0 failed. This matches architecture.md as of 7a0f5eb.
- **At 3f9a97c:** **584 total, 580 passed, 4 skipped, 0 failed**, 0 analyzer warnings.
  - Core 325 (3 skipped)
  - Proxy 102
  - Simulator 97 (+25)
  - Presentation 60 (+3, 1 skipped)

  This matches the author's figures, architecture.md:114 and TEST_REPORT §12.1. The 4 skips are the DNS test, NET10-01, NET10-02 and the FLAKY-01 repro.
- **The new tests are meaningful:**
  - **`TesterDocFindingsTests`** (Simulator) runs over real HTTP with FakeAcs and HttpCwmpTransport, directly and through a real proxy.
    - It covers 9 Inform answers: 500 Fault with three content types, a plain SOAP Fault, 500 with an empty or HTML body, 200 Fault, and 407 with or without a Fault.
    - It covers 4 answers to the empty POST: 500 with a Fault, 500 empty, 401, and 407 with a Fault.
    - It covers 401 with a Fault body and a Digest wrong-password case.
    - The through-proxy theory asserts that **the simulator and proxy outcomes agree**. That closes my earlier m3 (end-to-end consistency test) and n1-style gaps, apart from 403, which is a documented difference.
  - **`TesterDocFindingsSettingsTests`** checks Restore defaults → Save → reload. It asserts that every shown field resets (record equality against the defaults), that every hidden value is kept, and that the Proxy tab follows (TLS drop-down stays IgnoreErrors). It also checks that Restore defaults → Cancel discards the reset.
  - **`TesterFeedPumpRaceTests`**: a deterministic, skipped repro of FLAKY-01. I checked SessionFeedPump.cs:137-158. The missed change stays in the channel and goes out in the next batch, so in the app the effect is about one extra flush interval of delay and nothing is lost. "Test flake" is a fair label.

## Doc updates checked against the merged code
| Item | Status | Evidence |
|---|---|---|
| (a) simulator-mode.md:194-195 outcome rows | ✔ (see Nit 2) | CwmpClientSession.cs:69-83 |
| (a) Walkthrough 3 step 2, "ACS fault whatever the HTTP status (200 or 500)" | ✔ | CwmpClientSession.cs:71-75; probes "200 Fault" and "500 Fault" → AcsFault |
| (d) settings-reference.md:177, proxy-mode.md:108, ui-tour.md:300, troubleshooting.md:58 | ✔ | SettingsViewModel.cs:144-151. The window shows every ProxySettings member except Tls and MaxRequestBodyBytes (Build :85-100), and every CaptureSettings field. |
| (f) troubleshooting.md:13 quotes the new message | ✔ exact | CpeSessionTracker.cs:61 with the prefix "Proxy failed to start: " (ProxyViewModel.cs:208) |
| (g) all "not shown yet" wording replaced | ✔ | End-rule notes such as AuthChallenge, "empty response" and the Inform-Fault note do land on the exchange: `notes` is passed to EvaluateEnd, then to CompleteExchange (ProxyRequestHandler.cs:85-88). The Proxy notes block sits below the Response fields and the error line (SessionDetailView.xaml:80-91). |
| Emptied sections reused as troubleshooting "Known differences between the simulator and the proxy" and roadmap "Simulator and proxy consistency" | ✔ | m1: CwmpClientSession.cs:151-158 and :140 vs CpeSessionTracker.cs:301 + FinalOutcome. m2: no 403 rule in the simulator vs CpeSessionTracker.cs:317. Both are described as current behaviour with fixes deferred, not as features. |
| Smoke step 9 (IMPLEMENTATION_NOTES:70) | ✔ | The Domain note needs `stripCookieDomain`, which is on by default (HeaderRewriter.cs:89-92). The list appears below the Response fields and is hidden when HasNotes is false. |
| proxy.tls hand-edit wording (proxy-mode.md:68) | ✔ | StartAsync writes `Tls = (Tls ?? Default) with { Mode = TlsMode }` into `_settings.Current` before starting (ProxyViewModel.cs:171-173), and settings are saved on exit |
| OUI upper-casing (simulator-mode.md:51) | ✔ | SimulatorView.xaml:62 `CharacterCasing="Upper" MaxLength="6"`; ToProfile upper-cases again (CpeProfileEditorViewModel.cs:204) |
| build.ps1 flags (getting-started.md:44-45) | ✔ | build.ps1: `restore` → `build -c Release --no-restore` → `test AcsTool.sln -c Release --no-build` + `--filter Category!=Perf` + coverage |
| `--smoke-exit` / generic-host args (getting-started.md:74) | ✔ | App.xaml.cs:27 (smoke flag) and :41 `Host.CreateApplicationBuilder(e.Args)` |
| DF-02, Restore defaults keeps the whole tls block including the mode | ✔ | Same as (d); covered by the tester test |
| DF-01, "ACS fault 0 " note (troubleshooting.md:105) | ✔ accurate | CwmpParser.cs:410-413 (code 0, empty string) and CwmpClientSession.cs:74. "faultcode/faultstring are parsed, see the Summary tab" is right: MessageSummarizer.cs:192-196 shows `faultcode` and `faultstring` and titles the response `Fault soap:Server …`. |
| DF-03, TransferComplete ignores the HTTP status (troubleshooting.md:106) | ✔ accurate | CwmpClientSession.cs:104-117 has no status check and the note "TransferComplete not acknowledged (…); kept pending". "Not sent yet" is right: SimulatedCpe.cs:208 always passes `[]` as the pending CPE requests. |
| Roadmap low-priority follow-ups (roadmap.md:26-29) | ✔ | Presented as future work only |
| Test counts (architecture.md:114, getting-started.md:49) | ✔ | 584 / 580 / 4, as above |
| Stale references | ✔ none | A grep for "not shown in the UI", "known-issues", "planned-fixes" and "Settings → TLS" in docs/user-guide finds nothing |
| Links | ✔ | 78 relative links, 0 broken (files and anchors) |

## Nits (non-blocking)
1. **architecture.md:117:** a comma is missing before "a DNS test" in "…`NET10-02`) a DNS test that skips…". Fix: "…`NET10-02`), a DNS test that skips…, and a deterministic repro…".
2. **simulator-mode.md:194**, the "⚠ HTTP error" row, says "…or a non-2xx answer later in the session". A later **401** is **🔒 Auth failed** (CwmpClientSession.cs:130; tester probe `AfterInform_EmptyPostAnswered(401)`). The row also omits that a 407 carrying a Fault is HTTP error. Fix: "…or a non-2xx answer other than 401 later in the session; also a 407, even with a Fault".

Not verified (Windows-only): WPF rendering of Proxy notes, DPAPI, and the Windows test counts at 3f9a97c. HEAD is not pushed, so there is no CI run for it yet.
