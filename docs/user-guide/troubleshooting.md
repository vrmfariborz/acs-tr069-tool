# Troubleshooting and FAQ

Start with the place the problem shows up: a banner, a session outcome, or something you expected but don't see. The log file (`%LOCALAPPDATA%\AcsTool\logs\`, see [Logging](settings-reference.md#logging)) often has more detail.

## Banners

| Banner | Cause | Fix |
|---|---|---|
| ℹ *Windows Firewall may prompt; allow Private networks so modems can reach the proxy.* | Always shown at start-up as a reminder. | Accept the Firewall prompt the first time the proxy starts, for **Private** networks. Close the banner with **dismiss**. |
| ✖ *Port 7547 is already in use (another ACS/CPE tool?) — choose another port* | Another program (a local ACS, another AcsTool, a CPE emulator) listens on that port. | Stop the other program, or choose another port (and point the CPE at it). `netstat -ano \| findstr :7547` in a terminal shows the process ID. |
| ✖ *Access denied binding …:… — choose another port or check security software* | Windows or security software doesn't allow listening there (excluded port range, policy). | Try another port, e.g. 17547. `netsh interface ipv4 show excludedportrange protocol=tcp` lists reserved ranges. |
| ✖ *Address … is not available on this PC — choose 0.0.0.0 or a local IP* | The Listen address isn't one of this PC's addresses (often after a network change). | Use `0.0.0.0`, or an address from `ipconfig`. |
| ✖ *Proxy failed to start: TLS mode CustomCa needs a CA certificate file (Settings → TLS).* | `proxy.tls.mode` is `CustomCa` but `customCaPath` is empty. There is no TLS page in the Settings window yet, despite the message ([known issue](#customca-error-points-to-settings--tls)). | Close the app, set `proxy.tls.customCaPath` in `settings.json` (see [hand edit](proxy-mode.md#configuring-pinnedthumbprint-or-customca-hand-edit)), or pick another TLS mode on the Proxy tab. |
| ✖ *Proxy failed to start: Custom CA file '…' could not be loaded: …* | `proxy.tls.mode` is `CustomCa` and the file is missing, password-protected or not a certificate. | Fix `customCaPath` in `settings.json` (with the app closed). See [CustomCa notes](#customca-wont-load-my-pem-file). |
| ⚠ *TLS validation disabled for the upstream ACS* | TLS mode is IgnoreErrors and the upstream URL is `https://` (shown on Start). | Expected in a lab. Switch to Strict, PinnedThumbprint or CustomCa when you're done. |
| ⚠ *Upstream base URL has a path; Digest auth may fail (uri mismatch)* | Path mode PassThrough with an upstream URL like `http://acs/tr069`: the ACS sees `/tr069/acs` but the CPE signs `/acs`. | Remove the path from the upstream URL and put it in the CPE's ACS URL instead (e.g. upstream `http://acs`, CPE ACS URL `http://<PC IP>:7547/tr069`). See [Paths](proxy-mode.md#paths-and-the-acs-url). |
| ⚠ *settings.json was corrupt and was backed up to settings.json.bad-…; defaults loaded.* | The file isn't valid JSON (often a hand-edit typo). | Close the app, fix the backup (a JSON validator helps), and copy it over `settings.json`. See [Corrupt settings](settings-reference.md#corrupt-or-missing-settings-file). |
| ⚠ *The password of 'X' could not be decrypted for this Windows user; re-enter it before starting.* | The profile was saved by another Windows user or on another PC (DPAPI). | Open the profile, type the password again, **Save**. See [Passwords and DPAPI](settings-reference.md#passwords-and-dpapi). |
| ✖ *Profile 'X' has N validation error(s)* | **Save** was clicked with invalid fields. | Fix the fields listed in the error list under the editor's command bar. See [Validation rules](simulator-mode.md#validation-rules). |
| ✖ *Cannot start 'X': …* | Start was clicked while the editor's values have validation errors. | Fix the listed errors, then Start again. |
| ✖ *Profile is invalid: …* | The engine refused to start the CPE, e.g. *DeviceId.SerialNumber: A CPE with serial … is already running*, or the stored password can't be decrypted (*Password: …*). | Use another serial (or stop the other CPE), or re-enter the password and save. |
| ✖ *Unexpected error: …* | An unexpected error in the UI. The app keeps running. | Check the log for the stack trace, and report it with the steps. |

A dialog *AcsTool failed to start* means start-up itself failed (for example the settings folder can't be created). The text is the technical error; check permissions on `%APPDATA%\AcsTool`.

## Proxy problems

### The CPE never shows up in the feed
1. Is the proxy running? The status bar shows `Proxy: Running` and the Proxy tab shows the **● Running on …** line.
2. Is the CPE's ACS URL one of the **Running on** addresses, with the right port? From another machine, `http://<pc-ip>:7547/` in a browser should create a *Non-CWMP request* row.
3. Windows Firewall: allow AcsTool on Private networks (Control Panel → Windows Defender Firewall → Allow an app). If the PC's network is marked **Public**, either switch it to Private or allow Public too.
4. Listen `127.0.0.1` only accepts connections from this PC. Use `0.0.0.0` for modems.
5. Some CPEs only accept `https://` ACS URLs, which the proxy can't serve yet (no HTTPS listener).

### Rows say *Unknown session from 192.0.2.10*
The proxy got a POST it couldn't match to an Inform (orphan), for example because the CPE opened a new connection mid-session without cookies, or the Inform session already ended. The request was still forwarded. See [How requests are grouped into sessions](proxy-mode.md#how-requests-are-grouped-into-sessions).

### `CONNECT ConnectionRefused`, `DNS`, `TIMEOUT …`, `TLS …`
The ACS leg failed; the CPE received `502` or `504`. The [error classes table](proxy-mode.md#upstream-error-classes) explains each one, and [walkthrough 3](proxy-mode.md#walkthrough-3-find-out-where-an-inform-dies) walks through finding where it fails.

### Auth failed (401 twice) through the proxy, but direct it works
- With Digest, check for the Digest path banner above: remove the path from the upstream URL and put it in the CPE's ACS URL.
- With Host header **Preserve**, some ACSs reject a host name they don't own; use **Upstream** (default).
- With **ManagedPerCpe**, the ACS's session cookie is kept by the proxy; that's fine, but if the ACS also expects the CPE's own cookies, use PassThrough.

### The ACS redirects (3xx) and the CPE goes around the proxy
The proxy doesn't rewrite `Location` and doesn't follow redirects. (It records a note on the exchange, but exchange notes aren't shown in the UI yet; the Response column shows the 3xx status and the Headers tab shows `Location`.) Point the upstream URL at the final address instead.

### `413` returned to the CPE / `BODYTOOLARGE`
A body was larger than `proxy.maxRequestBodyBytes` (10 MiB). Raise it in `settings.json` if you really need to.

### CustomCa won't load my PEM file
- Only the **first** certificate in a PEM file is used. Put the root (or the issuing) CA first, or use one certificate per file.
- A PEM file saved with a UTF-8 BOM is rejected. Save it as UTF-8 without BOM (or use DER).
- PFX files must have no password.

### My pins or CA path disappeared
**Restore defaults** + **Save** in the Settings window clears `proxy.tls` ([known issue](#restore-defaults-wipes-hand-edited-tls-settings)), and the app writes its in-memory settings on exit, so edits made to `settings.json` while the app runs are overwritten. Edit with the app closed; see [Restore defaults](settings-reference.md#restore-defaults).

## Simulator problems

### Start is greyed out or fails
Start uses the editor's current values (saved or not). They must pass validation, and the device key (OUI + serial) must not already be running. A hand-edited profile with `instances.count` other than 1 is rejected too. If this profile's CPE is already running, Start does nothing; stop it first to pick up edits.

### `✖ Upstream error` (CONNECT … / TIMEOUT …)
The red line shows the error class. The ACS URL can't be reached in time. Check the URL (scheme, host, port, path) in a browser; check that the ACS (or the proxy) is running. The connect timeout is at most 10 s.

### `🔒 Auth failed`
The ACS answered 401 to the Inform even with credentials, or no credentials were set. Check Username/Password and what auth scheme the ACS expects (Basic and Digest are both handled).

### The ACS set a value but the editor still shows the old one
The running CPE works on its own copy of the parameters. Editor changes (and the ACS's changes) don't flow between them while the CPE runs; the CPE's copy is discarded when it stops. See [Running CPEs](simulator-mode.md#running-cpes-and-parallel-sessions).

### 0 BOOTSTRAP is sent again and again
By design: BOOTSTRAP stays in the pending events until a session completes successfully, like a real device. Once a session ends `✔ Completed`, it's no longer sent (until the CPE is restarted).

### Retry keeps increasing
Every failed session increments **Retry** (and `RetryCount` in the next Inform). There's no automatic retry, so it only grows when you click Send Inform again. Restarting the CPE resets it to 0.

## Feed and memory

### Sessions disappear from the feed
The feed shows the newest **Feed max items** (2,000) rows per tab; older sessions are also evicted from memory by **Max sessions** and **Capture budget**. Raise them on the Settings window's Capture page. There's no export yet, so copy important bodies with **Copy request/response**.

### `dropped N` in the status bar
Under very heavy load the internal notification queue (10,000 entries) dropped some UI updates. Captured data isn't lost; the affected rows may lag until their next update.

### The Raw tab says `Captured 1 MiB of …`
The body was larger than **Max captured body**; only the first part was kept. It was still forwarded in full. Raise the limit (up to 100 MiB) on the Capture page; the proxy uses the new value on its next **Start**.

### Pretty doesn't format the XML
The body isn't well-formed XML, or it's more than 2 million characters. The text is shown unchanged.

## Known issues (fixes planned)

These are how the current version behaves. Fixes are planned (see [roadmap](roadmap.md#planned-fixes-for-known-issues)).

### The proxy says *ACS fault*, the simulator says *HTTP error* for the same session
An ACS that rejects an Inform with a SOAP Fault usually sends it with HTTP **500**. The proxy sees the Fault first and labels the session **⚠ ACS fault**. The simulator checks the HTTP status first, so it reports **⚠ HTTP error** unless the Fault came with a 2xx status. The fault code and string are in the Summary and Raw tabs either way. Planned fix: the simulator will check for the Fault first, like the proxy.

### Restore defaults wipes hand-edited TLS settings
**Restore defaults** followed by **Save** in the Settings window resets the whole `proxy` section, including `proxy.tls` (TLS mode, pins and CA path) and `proxy.maxRequestBodyBytes`, which the window doesn't show. Workaround: back up `settings.json` before restoring defaults, and copy the `tls` block back with the app closed. Planned fix: Restore defaults will keep these two values.

### CustomCa error points to Settings → TLS
With TLS mode CustomCa and no CA path, Start fails with *TLS mode CustomCa needs a CA certificate file (Settings → TLS).* There is no TLS page in the Settings window yet. Set `proxy.tls.customCaPath` in `settings.json` instead ([how](proxy-mode.md#configuring-pinnedthumbprint-or-customca-hand-edit)). Planned fix: the message will name `proxy.tls.customCaPath`.

### Exchange notes are not shown
The proxy records notes on single exchanges (auth challenge, cookie rewrites, 3xx `Location`, a Fault answering the Inform), but the detail pane shows only session-level notes. Use the Response column and the Headers tab instead. Planned fix: show the selected exchange's notes.

---

## FAQ

**Is AcsTool an ACS?**
No. It doesn't manage devices. It relays (proxy) and imitates CPEs (simulator), so you can watch and test a real ACS.

**Does the proxy change the SOAP?**
No. Bodies are forwarded byte for byte. Only HTTP headers are adjusted (hop-by-hop headers, `Host`, cookies; see [Other header handling](proxy-mode.md#other-header-handling)).

**Can the CPE talk HTTPS to the proxy?**
Not yet: the listener is plain HTTP. The proxy can talk HTTPS to the ACS.

**Do I need to give the proxy the ACS password?**
No. The CPE's `Authorization` header is relayed unchanged; Basic and Digest both work.

**Can I run the proxy and the simulator at the same time?**
Yes. Point a profile's ACS URL at the proxy (`http://127.0.0.1:7547/acs`) to see the same session in both tabs.

**How many simulated CPEs can run?**
As many profiles as you create, each with a unique serial; at most 32 sessions run at the same moment (`simulator.maxConcurrentSessions`). One profile = one CPE.

**Can the simulator send Informs periodically?**
Not yet. Use **Send Inform now** or the row's **Inform** button. See the [roadmap](roadmap.md).

**Where are my profiles?**
`%APPDATA%\AcsTool\profiles\`, one JSON file each. You can copy them to another PC, but re-enter the passwords there.

**Can I export a session?**
Not yet. Use **Copy request**/**Copy response** in the Raw tab, or screenshots.

**Is there a dark theme?**
Not yet; `ui.theme` in `settings.json` has no effect.

**Does it run on Linux or macOS?**
The app is Windows-only (WPF). The core libraries and tests build on Linux, which is what the Linux CI job does.

**Is it safe to expose the proxy on the network?**
It has no authentication of its own and captures everything, including Authorization headers. Use it on lab or trusted networks only, and stop it when you're done.
