# Review: fix/feedpump-drain @ 9559ae7 (base main 7b8c906)

Reviewer: ReviewerBot · 2026-10-29 (Europe/Warsaw) · review only.

I reviewed the branch in private clones (`/tmp/rv-fpd`, plus `/tmp/rv-fpd-mut` for the revert check). Nothing was merged, committed or pushed, and no branches were switched in the shared checkouts.

## Verdict: **OK TO MERGE**
There are no Blocker, Major or Minor findings, only two Nits, both optional.

## CI (verified with the GitHub API)
Run 40861190447 on `fix/feedpump-drain` at head_sha `9559ae75…`: **success**. It ran from 11:54 to 11:56 (UTC+1).
- **linux:** success. Presentation 55, Core 325 (323 passed, 2 skipped), Proxy 99, Simulator 67. That is 546 tests, 0 warnings.
- **windows:** success, with the same counts and 0 warnings. `publish.ps1` and `--smoke-exit` both passed.

## Local (`./build.sh`, Perf excluded)
- Build: 0 warnings, 0 errors.
- Tests: 546 total, 543 passed, 3 skipped, 0 failed (Presentation 55, Core 325 with 3 skipped, Proxy 99, Simulator 67). The extra skip compared with CI is the DNS test, which skips on this box because its resolver answers every name.

## Repeat runs (no servers; FakeTimeProvider and loopback only)
- **Feed-pump tests** (`FullyQualifiedName~FeedPump`: 8 tests, including `TenThousandChangesOverOneSimulatedSecond` and the un-skipped repro): **30/30 runs green**.
- **`Timeout_ServerAcceptsButNeverResponds_TimeoutAwaitingResponse`:** **30/30 runs green**. Each run takes about **3 s**; it was about 0.5 s before.
- I did not run anything under artificial CPU stress, to avoid disturbing TesterBot on the shared box.

## Does the repro prove the fix? Yes
- With the fix reverted (SessionFeedPump.cs from 62caac9) and the test un-skipped: **0/10 pass**. Each run fails with "after dispatch 1 the feed has [00D09E-EARLY]; LATE missed the flush".
- With the fix restored (9559ae7): **10/10 pass**. The mutation clone was put back to 9559ae7.

## SessionFeedPump drain (SessionFeedPump.cs:155-161)
- **No double flush or duplicate batch.** The extra `Drain` only moves items from the channel into the same `pending` dictionary, which is keyed by session id and coalesced with `Max`. Each `TryRead` takes an item once, and there is still exactly one `FlushAsync` per `CollectAndFlushAsync`. The batch cap holds: `Drain` stops at `MaxBatchIds` (:166), and anything left over makes the outer `WaitToReadAsync` (:100) return at once, so it starts the next batch.
- **Cancellation is unchanged.** `Drain` is synchronous, made only of non-blocking `TryRead` calls, and bounded by `MaxBatchIds`, so it adds no wait to shutdown.
  - If `StopAsync` cancels while the loop is waiting: when `more` completes first, `await more` throws OCE and nothing is flushed. When the deadline wins, the loop breaks and one flush runs. That second path was already there before this change.
  - `StopAsync` awaits `_loop` (:82-85), so no flush can run after `DisposeAsync` returns.
- **Exception paths.** `Drain`, `Max` and `Rank` don't throw. If the collect loop throws (an OCE, or a failure in `WaitToReadAsync`), the final drain is skipped. The items stay in the channel, though, and the next `RunAsync` iteration or the resync path picks them up, so nothing is lost.
- **Ordering and threading.** The pump is the channel's only reader, and `Drain` runs on the pump task (plan §3.8 rule 5). `_collecting` stays true until after the final drain, which keeps `IsCollecting`-based test sync correct. An item written after the final drain goes into the next batch, which is the same as the existing behaviour.
- **Effect in the app:** a change written just before the deadline is now in that batch instead of the next one, so there is up to one flush interval (100 ms) less UI lag. FLAKY-01 was never data loss.

## FLAKY-02 timeout change (tests/AcsTool.Core.Tests/NetTests.cs:186-207)
- **No DNS dependency.** The test already uses `127.0.0.1` with a local `TcpListener` on port 0. `ConnectCallbackFactory` short-circuits IP literals (ConnectCallbackFactory.cs:24) and never calls `Dns.GetHostAddressesAsync` (:33). The phase reads `Dns` only because `PhaseTracker` starts in the pre-connect phase. The 500 ms was spent on cold-start overhead (JIT, coverage instrumentation, handler and connection-pool setup) before the connect callback ran, not on a DNS lookup. The code comment says this correctly ("before it reaches the connect callback"); the commit message's "DNS lookup" framing is slightly off. Switching to an IP isn't needed because it already is one.
- **Still meaningful.** The server accepts and never answers, so the request must time out. The asserts `Kind == Timeout` and `Phase == AwaitingResponse` prove that DNS (none) and connect (the listener accepts) both finished and the timeout fired while waiting for the response headers. A connect or pre-connect timeout would give a different phase. The connect timeout is 5 s, more than the 3 s request timeout, so the two can't be confused.
- **It doesn't hide a product bug.** The production request timeout defaults to 30 s, with a minimum of 1 s in the window, so a few hundred ms of one-time setup can't mislabel real traffic in practice.
- **Cost:** about 2.5 s more per Core run. That's acceptable.

## Nits (optional)
- **n1 (NetTests.cs:194-200):** to keep the test fast and still immune to cold start, do one warm-up request (or a short `TcpClient` connect) first, then measure with a smaller timeout such as 1 s. Or keep 3 s and add a sanity assert that the elapsed time is ≥ the timeout. As it stands it's fine.
- **n2 (SessionFeedPump.cs:159-161):** when the deadline loses to cancellation during shutdown (a path that was already there), the pump still dispatches one last batch to the UI. If you want, skip `FlushAsync` when `ct.IsCancellationRequested`. That would spare a WPF dispatcher call while the app closes. Not a regression.

## Other diff notes
- 62caac9 copies TesterFeedPumpRaceTests.cs byte for byte from ea92dd5 (skipped). 3131a48 changes only the `[Fact]` attribute plus the 4-line drain with its comment. No other src or test changes.
- **State observed after the review (11:5x UTC+1):** main already contains this branch as merge 93980dc, whose second parent is 9559ae7, so the tree I reviewed is exactly what was merged (the merge message says Fariborz approved it). It is followed by 479639b, which drops the "skipped" wording for FLAKY-01 in architecture.md:117 and getting-started.md:49. The docs worktree `/workspace/projects/acs-tr069-tool-docs` no longer exists, so I read main directly with `git show`.
- **Follow-up nit on main:** docs/user-guide/architecture.md:114 still says "584 tests, **580 passed, 4 skipped**". With the repro un-skipped it should read **584 tests, 581 passed, 3 skipped** on Linux; I did not run the suite at main to confirm this. Also check whether TEST_REPORT §12.4 still calls the repro skipped.
