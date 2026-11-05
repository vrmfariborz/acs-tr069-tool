# Review: feat/packet-trace @ a9d2399 (PT0.3, transport endpoints on exchanges)

| Field | Value |
|---|---|
| Reviewer | ReviewerBot |
| Date | 2026-11-05, ~09:05 EST (America/New_York, UTC-5) |
| Range | `d3fb391..a9d2399`, one commit: `a9d2399 feat(core): PT0.3 transport endpoints on exchanges`. It's based on main `d3fb391` and fast-forwards. |
| Mode | Review only, in the private clone `/tmp/rv-pt`, with `CI=true`, Perf excluded and ephemeral ports. No source changes, commits, merges or pushes. The shared checkout wasn't touched. |
| Verdict | **OK TO MERGE.** 0 Blocker, 0 Major, 4 Minor, 5 Nit. Nothing blocks the merge. m1 and m4 are cheap to fix now. m2 and m3 must be settled before PT0.4 consumes the tracker (they decide what PT0.4 writes). |

## 1. CI

Run 42038592710, checked through `gh`: branch `feat/packet-trace`, headSha `a9d2399067c0b6a94822ce1fd30b38c9ccc22fef`, **conclusion success**. It ran from 08:45 to 08:47 EST (13:45:28Z to 13:47:14Z).

| Job | Tests |
|---|---|
| linux | 614 passed, 3 skipped, 0 failed (617). Core is 337 passed + 2 skipped. PacketCapture is 15 passed + 1 skipped (libpcap is present). |
| windows (with publish and `--smoke-exit`) | 615 passed, 2 skipped, 0 failed (617). |

## 2. Local verification

- **Build:** `CI=true` Release build has **0 warnings, 0 errors**.
- **Tests:** **614 passed, 3 skipped, 0 failed**.
  - Core: 336 passed + 3 skipped (2 TesterCaMatrix and the live DNS test, which skips in this sandbox).
  - PacketCapture: 16/16 (there's no libpcap here).
  - Presentation 63, Proxy 102, Simulator 97.
- **Repeat runs:** the 14 new tests passed **25/25** runs.
- **Coverage:**
  - Core package overall: 86.3% line, 65.2% branch.
  - Every instrumented changed line in `ConnectCallbackFactory`, `EndPointUtil`, `PhaseTracker` and `InMemorySessionStore.SetExchangeEndpoints` is covered.
  - The only uncovered line is the never-read `ExchangeEndpoints.DownstreamConnectionId` getter (SessionModel.cs:71).
- **Mutation checks** (each key line reverted, then the new tests run; all mutations reverted afterwards and the clone is clean):

| Mutation | Result |
|---|---|
| ConnectCallbackFactory: drop the `socket.LocalEndPoint` argument | caught (live pooled-reuse test) |
| BeginConnect: don't clear `_localEndPoint` | caught |
| BeginConnect: no `_connectionSerial++` | caught (3 tests) |
| MarkConnected: no Normalize | caught |
| Store: no `Normalized()` | caught |
| Store: no `Notify(Updated)` | caught |
| EndPointUtil: identity | caught (3 tests) |
| Reset(): also clears local | caught (2 tests) |
| **Store method signature drifts (`long seq`), so it no longer implements the interface member** | **NOT caught.** The solution builds with 0 warnings and all store tests pass. See m1. |

## 3. Checks requested

1. **Types match the design (§6.2, TASKS PT0.3).**
   - `ExchangeEndpoints` has the same six members in the same order and types.
   - `ExchangeRecord.Endpoints` is an init property, with the positional members unchanged.
   - The DIM no-op matches the design.
   - The store replaces the record under `_gate`, raises one `Updated`, leaves `CapturedBytes` unchanged and ignores unknown sessions or seq numbers.
   - `PhaseTracker` gets `LocalEndPoint` and `ConnectionSerial`, incremented in `BeginConnect`. `MarkConnected` gets an optional `local` parameter.
   - `HttpMessageCapture`, `EstimateBytes` and session start are untouched.
   - `CompleteExchange` uses `old with { … }`, so endpoints set before completion survive. The only `new ExchangeRecord` is in `AddExchange`.
2. **Default interface method.**
   - The only real sink in `src/` is `InMemorySessionStore`, through `ISessionStore : ISessionSink`, and it implements the method.
   - There are no decorators in `src/`.
   - The delegating test sinks `RecordingSink` (Simulator.Tests) and `EndThrowsSink` (Proxy.Tests) drop the call. IMPLEMENTATION_NOTES records that for PT0.4.
   - The DIM is appropriate, but the implementation is implicit and untested through the interface (m1).
3. **Threading and memory ordering.**
   - All new fields are read and written under `_lock`, which gives full fences. Interlocked isn't needed.
   - The serial is unique and monotonic **per tracker**, and the tracker is per session: proxy `CpeSessionTracker.cs:207`, simulator `HttpCwmpTransport.cs:55`. It counts ConnectCallback invocations, so failed attempts leave gaps (n1).
   - Pooled reuse: `Reset()` keeps local and serial, which is verified live. A server close leads to a new socket and serial 2, also verified live.
   - Multiple addresses: the callback loops over the addresses sequentially under one total timeout. `MarkConnected` only runs for the socket that connected, so the recorded local endpoint is that socket's. The ACSs are single-hop and there's no Happy Eyeballs racing.
   - Attribution is "last socket connected" rather than "socket that carried this exchange" (m2). Separate reads aren't atomic (m3). `RemoteEndPoint` goes stale after a failed reconnect (m4).
4. **IPv4-mapped normalisation.** `EndPointUtil.Normalize` uses the same rule as `ProxyRequestHandler.Snapshot` (`IsIPv4MappedToIPv6` → `MapToIPv4`, port kept). It's applied both in `MarkConnected` (local) and in the store (all four endpoints). Pure IPv6 is kept. Tested.
5. **Disposal on a failed connect.** Unchanged and correct: the socket is disposed on SocketException, on the timeout OCE, on caller cancellation and on any other exception (bare `catch`). `MarkConnected` sits inside the `try`. On failure the local endpoint stays null, which is tested with a refused port.
6. **No leakage into the proxy or UI.** No file under Proxy, Simulator, Presentation, App or PacketCapture changed, and nothing outside Core references `SetExchangeEndpoints`, `Endpoints`, `EndPointUtil`, `LocalEndPoint` or `ConnectionSerial`.
7. **Byte-for-byte behaviour unchanged.** The callback still returns the same `NetworkStream(socket, ownsSocket: true)` with no wrapper. The only addition is a `socket.LocalEndPoint` read (getsockname) after connect. Proxy.Tests 102/102 pass, including the byte-for-byte and auth pass-through tests.
8. **Tests are meaningful.**
   - 8 of the 9 mutations are caught.
   - The live test checks the recorded local endpoint against what the server saw as the client endpoint (`_accepted`), which is a strong check.
   - It uses ephemeral loopback ports and in-test servers that are disposed. No long-lived servers.
9. **Analyzers:** 0 warnings.

**PT0.2 Minors:** m1 to m4 (and n1 to n4, n6) were already fixed **before** the PT0.2 merge, in `9fe54b6` and `a2a76b7`, which are inside `d3fb391`, not in this diff. I spot-checked them:
- The exact SharpPcap copyright line is in place.
- The test candidates include `libpcap.so.0`.
- The probe uses `LibPcapLiveDeviceList.New()` and has the zero-device hint.
- The Presentation assembly-load smoke tests are there.
- Design §3 states that the Linux runner has libpcap.

n5 is deferred to PT1.15, as recorded.

## 4. Findings

### Blocker / Major
None.

### Minor

**m1. Silent loss if the store's method stops implementing the DIM.**
- Where: `src/AcsTool.Core/Sessions/InMemorySessionStore.cs:199`, `tests/AcsTool.Core.Tests/SessionStoreTests.cs:219-280`.
- The store implements `ISessionSink.SetExchangeEndpoints` implicitly, and every test calls it on the concrete type.
- With a signature drift (verified with `long seq`), everything builds with 0 warnings and all tests pass. But PT0.4's callers, which hold an `ISessionSink`, would hit the no-op and the endpoints would silently vanish. That's exactly the risk a DIM introduces.
- Fix (either one):
  - (a) Call it through the interface in the tests, e.g. `ISessionSink sink = store; sink.SetExchangeEndpoints(...)`, in `SetsAndRaisesOneUpdated`.
  - (b) Implement it explicitly: `void ISessionSink.SetExchangeEndpoints(...)`, delegating to the public method. Drift then becomes a compile error.

**m2. Upstream attribution is "last socket connected", not "socket that carried this exchange".**
- Where: `src/AcsTool.Core/Net/PhaseTracker.cs:26-30`, `SessionModel.cs:66-69`, `HttpCwmpTransport.cs:75`.
- The proxy is correct today: one upstream server, `MaxConnectionsPerServer = 1`, `AllowAutoRedirect: false`.
- It's wrong in two cases:
  - (i) **Simulator with redirects** (`AllowAutoRedirect: true`, up to 5). The redirect target is a separate pool and its connection stays pooled. If the original connection reconnects while the target's connection is reused, the tracker shows the original socket for an exchange whose final response came from the target. Every exchange re-POSTs to the original `_url`, so this recurs.
  - (ii) **An exchange that fails before ConnectCallback runs** (for example, cancelled or timed out while waiting for the pooled connection). The tracker still holds the previous exchange's socket and serial.
- Design §6.4 (line 392) groups exchanges per upstream conversation by `UpstreamConnectionSerial`, so a wrong serial puts packets under the wrong exchange.
- Fix, to decide before PT0.4:
  - Document the semantics on `LocalEndPoint` and `ConnectionSerial`.
  - In PT0.4, only report upstream endpoints when this exchange reached Connected (for example, the tracker's phase is past Connect for this exchange, or it got response headers) and, in the simulator, only when no redirect happened (`req.RequestUri == _url`).
  - Otherwise report upstream as null, or mark the endpoints as "inferred".
  - The exact alternative is a non-copying stream wrapper in the ConnectCallback that stamps (local, remote, serial) into the tracker on the first write of each request. That touches the byte path, so it's PT2 territory.

**m3. No atomic snapshot of the upstream tuple.**
- Where: `PhaseTracker.cs:24-30`.
- `LocalEndPoint`, `ConnectionSerial` and `RemoteEndPoint` are read under three separate lock acquisitions (`RemoteEndPoint` with none).
- Since .NET 6, SocketsHttpHandler runs connection attempts decoupled from the request that triggered them. So a background connect (after a cancelled exchange, or a pool refill) can land between PT0.4's reads and produce a mixed tuple, such as serial N+1 with local N or stale remote.
- Fix: add `public (IPEndPoint? Local, IPEndPoint? Remote, long Serial, bool Connected) SnapshotUpstream()` (or a small record) that reads everything under one `lock (_lock)`. PT0.4 should use only that.

**m4. `BeginConnect` doesn't clear `RemoteEndPoint`.**
- Where: `PhaseTracker.cs:47-56`.
- After a failed reconnect, the tracker reports Local=null and Serial=new with the previous connection's `RemoteEndPoint`. PT0.4's rule "upstream null when the failure happened before connect" would then need a special case.
- No `src/` code reads `tracker.RemoteEndPoint` (only NetTests.cs:312), so clearing it is safe.
- Fix: add `RemoteEndPoint = null;` in `BeginConnect`. Extend `Connect_Refused_LocalEndPointNull_SerialCounted` to run a successful connect first and then assert that Remote and Local are both null with serial 2.

### Nit

- **n1 – serial wording.** `SessionModel.cs:69` and design §6.2 say "socket number; a new socket increments". The code counts **ConnectCallback invocations**: failed attempts count, and multiple addresses count once. That's fine (unique and increasing per session, with gaps), but say so: "unique, increasing per session; failed attempts leave gaps". `PhaseTracker.cs:29` already says "attempts".
- **n2 – `RemoteEndPoint` inconsistent with `LocalEndPoint`.** At `PhaseTracker.cs:24` and `:76` it's neither normalised nor read under the lock (it's an auto-property written inside the lock). Reference reads can't tear, but make it match `LocalEndPoint`: a backing field, a locked getter and `EndPointUtil.Normalize`.
- **n3 – `UpstreamRemote` doc.** `SessionModel.cs:68` says "ACS ip:port actually connected". With `UseSystemProxy` (proxy setting), it's the HTTP proxy's endpoint. Add "(or the HTTP proxy when one is used)".
- **n4 – no multi-address test.** No test covers "first address refused, second connects; the local endpoint is the second socket's and the serial is 1". `localhost` resolving to `::1` and `127.0.0.1` with an IPv4-only listener would do it, though the order depends on the resolver, so skip it if `::1` isn't first. Optional.
- **n5 – duplicated normalisation rule.** `ProxyRequestHandler.cs:199-203` still has its own copy. Switch it to `EndPointUtil.Normalize` in PT0.4, when that file is touched anyway.

## 5. Not verifiable on Linux / here

- Windows socket behaviour for `LocalEndPoint` (dual-stack, IPv6 scope ids) and the Windows connect-timeout path. Windows CI passed the same tests, but I couldn't inspect them live.
- Real-ACS redirect chains and HTTP-proxy (`UseSystemProxy`) attribution (m2, n3). This box has no system proxy and no redirecting ACS. The analysis is by code reading only.
- The background-connect race (m3) is reasoned from SocketsHttpHandler's pool design and wasn't reproduced. That would need a cancelled request while a connect is in flight.
- The multi-address loop with a real dual-stack ACS name (n4). The DNS live test is sandbox-skipped here.
