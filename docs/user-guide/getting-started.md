# Getting started

This page takes you from a fresh Windows PC to a running AcsTool and a first test session.

## 1. Requirements

| What | Why | Notes |
|---|---|---|
| 64-bit Windows (x64) | The app is WPF (`net10.0-windows`) and is published for `win-x64`. | It does not run on Linux or macOS. Linux can build and test the code, but not show the UI. |
| **.NET 10 SDK**, version **10.0.403 or a newer 10.0.x** | Needed to build, test and publish. `global.json` requires `10.0.403` with `rollForward: latestFeature`, so any newer 10.0 SDK works, but an 11.x SDK is not picked. | Download: <https://dotnet.microsoft.com/download/dotnet/10.0>. |
| .NET 10 runtime | Not needed for the published exe. | `publish.ps1` makes a **self-contained** build that bundles the .NET, Windows Desktop and ASP.NET Core 10 runtimes. You only need the runtime if you start the app with `dotnet run`. |
| Git | To clone and update the repository. | |
| PowerShell | To run `build.ps1` and `publish.ps1`. | Windows PowerShell or PowerShell 7 both work. |

Check the SDK in a **new** PowerShell window after installing it:

```powershell
dotnet --version     # must print 10.0.403 or a newer 10.0.x
```

If it prints an 8.x or 9.x version, or says no SDK matches `global.json`, install the .NET 10 SDK and open a new window.

## 2. Get the code

```powershell
git clone https://github.com/vrmfariborz/acs-tr069-tool.git
cd acs-tr069-tool
```

Already cloned? Update with `git pull` in the project folder.

## 3. Build and test

```powershell
Set-ExecutionPolicy -Scope Process Bypass   # only for this PowerShell window
.\build.ps1
```

> **ExecutionPolicy tip.** Windows blocks unsigned scripts by default ("running scripts is disabled on this system"). `Set-ExecutionPolicy -Scope Process Bypass` lifts that only for the current window and changes nothing permanently.

`build.ps1` does three things, and stops at the first failure:

1. `dotnet restore AcsTool.sln`
2. `dotnet build AcsTool.sln -c Release`
3. `dotnet test` for all test projects, skipping performance tests (`Category=Perf`) and collecting code coverage into `TestResults\`.

Options: `.\build.ps1 -NoCoverage` (faster) and `.\build.ps1 -IncludePerf` (also runs the parser performance test).

A healthy run ends with every test project reporting `Failed: 0`. A few tests are reported as **Skipped** on purpose: two known-issue repros for custom CA files (see [roadmap.md](roadmap.md)) and, on machines where every DNS name resolves, one DNS test.

## 4. Publish the app

```powershell
.\publish.ps1
```

This runs `dotnet publish src/AcsTool.App/AcsTool.App.csproj -c Release -r win-x64 --self-contained true` and writes the app to **`out\win-x64\`**. Use `.\publish.ps1 -Output D:\Tools\AcsTool` to choose another folder.

The output folder is portable: you can copy it to another Windows x64 PC that has no .NET installed.

## 5. Run it

```powershell
.\out\win-x64\AcsTool.exe
```

Other ways to start it:

| Command | Use |
|---|---|
| `dotnet run --project src\AcsTool.App -c Release` | Run from source without publishing (needs the SDK). |
| `Start-Process .\out\win-x64\AcsTool.exe -ArgumentList '--smoke-exit' -Wait -PassThru \| % ExitCode` | Smoke test: starts the app without showing the window, loads settings and profiles, shuts down cleanly and exits. `0` means OK, `1` means startup failed. Like every normal exit, it also writes `settings.json`. CI uses this. |

`--smoke-exit` is the only command-line option the app understands.

## 6. First run: what you will see

On the very first start there is no settings file and no simulator profile yet, so:

- The window opens at 1280 × 820 with two tabs, **Proxy** (selected) and **Simulator**, a **⚙ Settings** button at the top right, and a status bar at the bottom.
- A blue info banner says: *"Windows Firewall may prompt; allow Private networks so modems can reach the proxy."* It appears on every start; click **dismiss** to hide it.
- The proxy is **stopped**. Its fields show the defaults: listen on `0.0.0.0`, port `7547`, upstream `http://localhost:7557`, TLS `Strict`, Cookies `PassThrough`.
- The Simulator tab has an empty **Profiles** list. Create one with **+ From TR-098** or **+ From TR-181**.
- The status bar reads `Proxy: Stopped · active 0 · sessions 0 · capture 0.0 MB / 256 MB · dropped 0`.

Settings, profiles and logs are created under your Windows user profile (see [settings-reference.md](settings-reference.md#where-files-live)):

| Folder | Content |
|---|---|
| `%APPDATA%\AcsTool\settings.json` | App settings (written when you save in the Settings window, and when the app closes) |
| `%APPDATA%\AcsTool\profiles\*.json` | One file per simulator profile |
| `%LOCALAPPDATA%\AcsTool\logs\acstool-YYYYMMDD.log` | Daily rolling log |

## 7. A five-minute end-to-end test

This uses both modes on one PC: a simulated CPE sends an Inform through the proxy to your ACS.

1. **Proxy tab.** In **→ Upstream**, enter your dev ACS URL, for example `http://acs-dev.lab:7547`. Leave the path off unless your ACS needs it (see [proxy-mode.md](proxy-mode.md#paths-and-the-acs-url)).
2. Click **▶ Start** (or press **F5**). The state text changes to `● Running on http://<your-ip>:7547`. If Windows Firewall asks, allow **Private networks**.
3. **Simulator tab.** Click **+ From TR-098**. A profile called `● New TR-098 profile` appears (the dot means unsaved). Its ACS URL is already `http://127.0.0.1:7547/acs`, which is the proxy.
4. If your ACS needs CPE credentials, fill **User** and **Password**.
5. Click **Save**, then **✉ Send Inform now**.
6. Watch the result:
   - Simulator tab: the **Running CPEs** row goes `InSession` → `Idle`, and the feed shows a session with outcome **✔ Completed**.
   - Proxy tab: the feed shows the same session as seen by the proxy, also **✔ Completed** (within about 3 seconds; see [proxy-mode.md](proxy-mode.md#how-a-session-ends)).
7. Click a session and open the **Raw** tab to see the Inform and InformResponse XML. Tick **Pretty** to format it.

Next: point a real modem at the proxy with [proxy-mode.md](proxy-mode.md#walkthrough-1-put-a-lab-modem-behind-the-proxy).

## Updating later

```powershell
git pull
.\build.ps1
.\publish.ps1
```

Close the running app before publishing to the same folder, otherwise Windows keeps `AcsTool.exe` locked. Settings, profiles and logs are outside the app folder, so they survive updates.
