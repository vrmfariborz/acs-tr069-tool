# Review: feat/packet-trace @ 257a272 (PT0.4, proxy and simulator fill in exchange endpoints)

| Field | Value |
|---|---|
| Reviewer | ReviewerBot |
| Date | 2026-11-05, ~09:35 EST (America/New_York, UTC-5) |
| Range | `f4b0a31..257a272`, one commit: `257a272 feat(proxy,simulator): PT0.4 populate exchange endpoints`. It's based on main `f4b0a31` and fast-forwards. |
| Mode | Review only, in a private clone `/tmp/rv-pt` (re-cloned from GitHub after the box was replaced mid-review), with `CI=true`, Perf excluded and ephemeral ports. Scratch tests were deleted afterwards and the clone is clean. No commits, pushes or merges. The shared checkout wasn't touched. |
| Verdict | **FIXES NEEDED: 1 Major** (small, simulator only), 1 Minor, 6 Nit. Everything else is correct. |

## 1. CI

Run 42041297516, checked through `gh`: branch `feat/packet-trace`, headSha `257a272dceab765e5fc51df082e41c9754f338ce`, **conclusion success**. It ran from 09:03 to 09:04 EST (14:03:18Z to 14:04:56Z).

| Job | Tests |
|---|---|
| linux | 624 passed, 3 skipped, 0 failed (627). Core is 340+2; PacketCapture is 15+1 (libpcap present); Proxy 107, Simulator 99, Presentation 63. |
| windows | 625 passed, 2 skipped, 0 failed (627). |

## 2. Local verification

- **Build:** `CI=true` Release build has **0 warnings, 0 errors**.
- **Tests:** **624 passed, 3 skipped, 0 failed**. Core is 339+3 (the live DNS test skips in this sandbox); PacketCapture 16/16.
- **Repeat runs:** the new and affected tests (EndpointTests, SnapshotUpstream*, TransportTests, EngineTests) passed **12/12** runs.
- **Coverage** (package line / branch): Core 86.3 / 65.2, Proxy 90.4 / 80.7, Simulator 89.2 / 70.0, PacketCapture 100 / 85.7. **All 35 instrumented changed lines in `src/` are covered.**
- **Mutation checks** (each key line reverted, then the related tests run):

| Mutation | Result |
|---|---|
| Carried is always true when connected | caught (Core PooledReuse unit test only) |
| `Reset()` keeps `_connectedThisExchange` | caught |
| Carried ignores `_headers` | caught (5 tests: Core, Proxy ×2, Simulator engine) |
| `MarkConnected` doesn't set the flag | caught |
| `ExchangeEndpoints.Create` ignores Carried | caught (3 tests) |
| Simulator has no redirect check | caught |
| Proxy doesn't call `SetExchangeEndpoints` (main path / 413 path) | caught (3 / 1) |
| `LocalPort` not set | caught (3) |
| `CwmpClientSession` doesn't store endpoints | caught |
| Proxy `Snapshot` doesn't normalise local or remote | not caught. That's expected: `Create` and the store normalise again (n1). |
| `RecordingSink` doesn't forward | **not caught** (n4) |

## 3. Checks requested

1. **m2 / m3: is "this exchange used the socket" ever wrong?**
   - **m3 is fixed.** `SnapshotUpstream()` reads local, remote, serial, connected and carried under one `lock (_lock)`, and both producers use only that.
   - **m2 is fixed for the proxy** in every case I checked:
     - **Pooled reuse:** the response-headers path claims the reused socket. Verified: `FakeAcs` saw the same client port.
     - **Transparent retry:** a dead pooled connection gets a new connect during the exchange, which sets the flag, so the new socket is claimed.
     - **Auth retry:** the proxy passes 401s through, so each resend is its own exchange. For simulator Digest, all three requests used one socket and serial 1 matched what FakeAcs saw (scratch run). A server that closes after the 401 gets a new connect inside the same exchange, so the new socket is claimed.
     - **Concurrent exchanges:** `session.Gate` serialises exchanges for each proxy session, which owns its tracker, including NAT'd CPEs sharing one session. Each simulator transport is sequential.
   - **In the simulator, a redirected exchange is correctly null, but a *later* exchange that isn't redirected is wrong** (M1, reproduced).
2. **PT0.3 m1 / m4:** both are fixed in `82f018f`, which is on main.
   - m1: the explicit `void ISessionSink.SetExchangeEndpoints` (InMemorySessionStore.cs:201), plus a test through `ISessionSink` (SessionStoreTests.cs:230).
   - m4: `BeginConnect` clears `_remoteEndPoint` (PhaseTracker.cs:61).
3. **Downstream endpoints:**
   - `ConnectionId`, CPE remote and proxy local come from `ctx.Connection`, and the tests check them against the CPE client's own socket local endpoint. A reconnect gives a new remote and a new connection id.
   - The 413 orphan path records downstream only.
   - IPv4-mapped normalisation: with a scratch test that binds the proxy to `::` (dual-mode) and has the CPE connect to 127.0.0.1, both downstream endpoints were stored as IPv4.
4. **Byte-for-byte / hot path:**
   - Forwarded bytes and headers are unchanged. Proxy.Tests pass 107/107, including the byte-for-byte and auth pass-through tests.
   - The added work per exchange is a few small allocations, one extra store lock and one `Updated` notification. The feed pump coalesces notifications by session id. This happens before `RelayAsync` (n2, n3), costs microseconds, and is negligible next to the body copies.
5. **Thread-safety:** all tracker state is under `_lock`. The store write is under `_gate`. Producers are serialised per session as above. A narrow race remains where a background connect lands after a connect-phase timeout (m1).
6. **Tests are meaningful:** 11 of 13 mutations are caught (table above). Three good tests were added: redirect, refused, and 413.
7. **Coverage and analyzers:** see §2.
8. **Docs:** IMPLEMENTATION_NOTES and the `UpstreamSnapshot` XML docs match the code. The exception is "After an auto-redirect no upstream socket is claimed", which is true only for the redirected exchange itself (M1).

**Known gap: a pooled socket that times out before headers reports upstream null.** That's **acceptable, and null is the right choice.**
- A null makes PT1 fall back to the time-window match (§6.4), which is visible to the user.
- A wrong value would silently put packets under the wrong exchange.
- It's documented in the `UpstreamSnapshot` docs and IMPLEMENTATION_NOTES, with the per-request stream stamp in PT2 as the exact fix.

n4 (the multi-address connect test) is still open and optional.

## 4. Findings

### Major

**M1. Simulator: after a redirect, a later exchange that isn't redirected claims the redirect target's socket.**
- Where: `src/AcsTool.Core/Net/PhaseTracker.cs:113-121` (the `|| _headers` branch of `SnapshotUpstream`), and `src/AcsTool.Simulator/Transport/HttpCwmpTransport.cs:108-111`.
- Reproduced with a scratch test (since deleted):
  - FakeAcs `acs` answers request #1 with a 307 to `target` and request #2 with a 200.
  - Exchange 1 is redirected, so upstream is null (correct).
  - Exchange 2 is **not** redirected. It reuses the original `acs` pooled connection: FakeAcs `acs` saw client `127.0.0.1:49858` for both requests.
  - But the stored endpoints are `UpstreamLocal=127.0.0.1:34878, UpstreamRemote=127.0.0.1:36081 (target's port), Serial=2`. That's the **redirect target's** socket, claimed with `CarriedCurrentExchange=true`.
- This is a wrong value, not a null, and it's the m2 scenario.
- Root cause: the simulator's handler has two connection pools (original and redirect target) sharing one tracker. `_headers` proves that *a* socket carried the exchange, while the tracker holds the *last connected* socket. Those are only the same thing when there's a single pool. The proxy is safe because it never redirects and has one upstream authority.
- Fix (recommended, general):
  - Record the connection's target authority in the tracker. The ConnectCallback has `context.DnsEndPoint`, so call something like `tracker.BeginConnect(host, port)` / `MarkConnected(..., authority)`.
  - Give `SnapshotUpstream(string host, int port)` (or a check in `ExchangeEndpoints.Create`) a rule: claim only when the last-connected authority equals the final request URI's `Host:Port` (`req.RequestUri`).
  - That covers the redirected exchange, later exchanges after a redirect, and a redirect to the same authority on another path, which would then be claimed correctly.
  - Add the scratch case as a regression test: `Endpoints_RedirectOnlyFirst_SecondExchangeClaimsOriginalSocketOrNull`, asserting `UpstreamLocal` is null or equals `acs.Received[1].RemoteEndPoint`.
- Minimal alternative: once the transport has seen any redirect, it claims upstream only when `CarriedCurrentExchange` came from a connect during this exchange **and** `snapshot.Remote.Port == _url.Port`. That's less exact.

### Minor

**m1. A background connect after a connect-phase timeout can be claimed by the failed exchange.**
- Where: `src/AcsTool.Proxy/ProxyRequestHandler.cs:89-91`, `HttpCwmpTransport.cs:108-111`.
- SocketsHttpHandler doesn't tie connection attempts to the request. So if the request timeout fires while the ConnectCallback is still connecting (possible in the proxy when `RequestTimeoutSeconds < ConnectTimeoutSeconds`, which the settings allow), the connect can complete between `SendAsync` returning and `SnapshotUpstream()`.
- That sets `_connectedThisExchange=true`, so a failed exchange claims a socket it never used. The window is tiny. I found it by reasoning and didn't reproduce it.
- Fix: don't claim upstream when the exchange failed before or at connect, i.e. pass `result.Error is { Phase: RequestPhase.Dns or RequestPhase.Connect } ? null : snapshot` to `ExchangeEndpoints.Create`.

### Nit

- **n1 – normalisation done three times.** IPv4-mapped normalisation happens in `Snapshot` (ProxyRequestHandler.cs:204-205), in `ExchangeEndpoints.Create`→`Normalized()` (SessionModel.cs:88) and in the store, so mutating `Snapshot` isn't observable in endpoints. Keeping it in `Snapshot` is still right, because `ClientEndpoint` uses it too. Consider adding the dual-mode `BindAddress="::"` test, which passes, so `ClientEndpoint` normalisation is pinned (that was a test gap before PT0.4).
- **n2 – avoidable allocations per exchange.** `Snapshot` converts IPAddress → string, then `RemoteEndPoint`/`LocalEndPoint` parse it back (`TryParse` ×2, Model.cs:19-22). `Create` builds two records (positional + `Normalized()`), and the store normalises again. It's negligible, but you could keep the `IPAddress` on `IncomingRequest` (internal init) and skip the second `Normalized()` in `Create`, since the store normalises anyway.
- **n3 – work before the relay.** `SetExchangeEndpoints` runs before `RelayAsync` (ProxyRequestHandler.cs:89-91), so its lock and allocations sit on the CPE-visible path. Optionally take the snapshot there and call `SetExchangeEndpoints` after `RelayAsync`, still inside `session.Gate`, which keeps the result correct.
- **n4 – `RecordingSink` forwarding untested.** The forward (SimTestKit.cs:93-94) isn't covered; the mutation survives. Add one assertion in a `ClientSessionTests` case that the stored exchange has `Endpoints`.
- **n5 – field placement.** `_connectedThisExchange` and its comment sit above `_lock` (PhaseTracker.cs:13-14). Move it next to the other state fields.
- **n6 – note wording.** In IMPLEMENTATION_NOTES ("After an auto-redirect (`req.RequestUri != _url`) no upstream socket is claimed"), add "for that exchange" and describe the M1 fix once it's in.

## 5. Not verifiable on Linux / here

- Windows Kestrel dual-mode (`::`) behaviour and Windows `LocalEndPoint` details. Linux dual-mode was verified, and Windows CI passed the same tests.
- The background-connect race (m1). It needs a connect slower than the request timeout, which loopback can't produce. It's reasoned from SocketsHttpHandler's pool design.
- A real ACS with redirect chains or load-balancer redirects (M1 was reproduced with FakeAcs), HTTP-proxy (`UseSystemProxy`) attribution, and Digest against a real ACS that closes the connection after a 401.
- n4 (the multi-address connect test) is still optional and open. This box's `localhost` resolves to `::1` only.
