# Not yet available / roadmap

These features are **planned but not in the app yet** (as of `main` at 7b8c906). Some of them already have settings or profile fields in the file format, which do nothing yet. The plan is in [`docs/PLAN.md`](../PLAN.md) §5 and [`docs/M5_TASKS.md`](../M5_TASKS.md). Milestone numbers follow M5_TASKS (TLS is the new M5; see decision D1 below). Order and scope can still change.

## Planned next: M5, HTTPS listener and TLS (M5_TASKS.md)

| Feature | Today |
|---|---|
| **HTTPS listener** for CPEs (planned: separate port, default 7443, off by default; self-signed certificate generated and kept in `%APPDATA%\AcsTool\certs`, or a PFX, or the Windows store) | The proxy listens on plain HTTP only. |
| **TLS page in the Settings window** (mode, pins, CA file, protocols) and a **"Fetch server certificate"** probe that shows the SHA-256 to pin | Only the mode is on the Proxy tab; pins and CA path are [hand-edited in settings.json](proxy-mode.md#configuring-pinnedthumbprint-or-customca-hand-edit). |
| **TLS details** in the detail pane (chip, pop-up, thumbprint) and banners for TLS problems before Start | The detail header shows a short TLS line; the thumbprint is captured but not shown. |
| **Simulator TLS settings** in the profile editor; TLS facts on simulator sessions | Profiles use Strict unless `tls` is hand-edited in the profile file; simulator sessions record no TLS info. |
| Optional **revocation checking** for Strict | Revocation is never checked. |
| **CustomCa** fixes: multi-certificate PEM bundles, PEM files with a UTF-8 BOM | Only the first certificate is used; BOM files are rejected. |
| **Cookie correlation fix** (review item m3): CPEs behind a load balancer sharing an affinity cookie | Two CPEs with the same cookie pair can be grouped into the same session. |

Not in M5 (deferred): upstream **client certificates** (mutual TLS; `clientCertPfxPath` is unused), the negotiated protocol/cipher on the upstream leg, HTTP/2 inbound.

## Simulator and proxy consistency

Two cases are classified differently by the simulator and the proxy (see [troubleshooting.md](troubleshooting.md#known-differences-between-the-simulator-and-the-proxy)). The follow-up fixes are deferred:

- A SOAP Fault with a 2xx status later in a session: the simulator will end the session **⚠ ACS fault**, like the proxy, instead of **✔ Completed**.
- A `403` to the Inform: the simulator will report **🔒 Auth failed**, like the proxy, instead of **⚠ HTTP error**.

## Auth and cookies follow-ups

- A late trailing empty POST (or an empty POST after a Fault) currently shows up as an extra orphan row (m2).
- Simulator capture doesn't yet show cookies set by the response (m4).

## M7: more RPCs and follow-ups

- RPCs in the simulator: **GetParameterAttributes / SetParameterAttributes, AddObject, DeleteObject, Reboot, FactoryReset, Download, ScheduleInform**. Today they get Fault 9000.
- **Follow-up actions**: reboot (state Rebooting, then `1 BOOT` + `M Reboot`), factory reset (`0 BOOTSTRAP`), download with `7 TRANSFER COMPLETE` + TransferComplete, scheduled Informs (`3 SCHEDULED`). `simulator.rebootDelaySeconds` and `downloadDurationSeconds` are for this and are unused today.
- **Value change notifications**: editing a parameter with notification on would queue `4 VALUE CHANGE` (passive) or start a session (active). Today the **N** column has no effect.
- **Connection requests**: receiving the ACS's connection-request GET.

## M8: multiple CPEs, periodic Informs, retry

- **Periodic Informs** (`periodic` in the profile: interval, phase time, jitter), including `PeriodicInformInterval` set by the ACS.
- **Automatic retry with backoff** after failed sessions (`retry`: TR-069 minimum wait and multiplier, max retries), with a WaitingRetry state.
- **Multi-instance profiles**: one profile → many CPEs (`instances.count`, serial pattern, staggered start). Today `count` must be 1.
- UI: Periodic & retry editors with a backoff preview, an RPC-behavior editor (today `rpcBehaviors` is [JSON only](simulator-mode.md#advanced-per-rpc-behavior-overrides-profile-json)), a live parameter view of a running CPE.

## M9: export and raw Inform template

- **Export** sessions to JSON or a ZIP of raw bodies (Authorization redacted by default), an export dialog, and *Copy as XML*. The `Documents\AcsTool\exports` folder is reserved for this. Session import is a stretch goal.
- **Raw Inform template** with placeholders, "Generate from profile", and an option to send deliberately invalid XML (`rawInformTemplate`, `useRawTemplate`, `sendInvalidTemplate` in the profile are unused today).

## M10: polish

- **Log panel** in the app, with log level switchable at runtime (`logging.*` is unused today).
- **Settings window completion**: all pages, **Dark theme** (`ui.theme`), persisted UI preferences (`ui.autoScroll`, `ui.showRawByDefault`), XML syntax highlighting in the Raw tab.
- Performance benchmarks recorded in `docs/PERF.md`.
- Packaging and hardening.

## Housekeeping

- CI hygiene: pin actions to SHAs, Dependabot, `permissions: contents: read`, a fixed Ubuntu image.
- Move the tests to xUnit v3.

## Decisions waiting for Fariborz (M5_TASKS §6)

D1 milestone renumbering · D2 HTTPS on a separate port 7443 · D3 default self-signed certificate · D4 revocation off by default · D5 mTLS deferred · D6 no upstream cipher capture · D7 HTTP/1.1 only inbound · D8 simulator TLS UI in M5 · D9 merge with the Windows manual checklist pending. Each one has a proposed default in M5_TASKS.md; none of them is confirmed yet.
