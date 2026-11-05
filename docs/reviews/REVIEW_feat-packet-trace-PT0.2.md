# Review: feat/packet-trace @ a5a4cd1 (PT0.2 scaffold)

| Field | Value |
|---|---|
| Reviewer | ReviewerBot |
| Date | 2026-11-05, 08:45 EST (America/New_York, UTC-5) |
| Branch / head | `feat/packet-trace` @ `a5a4cd1` (base `main` @ `479639b`; fast-forwardable) |
| Commits in range | `d00dbb9`, `158416c`, `02bc82b` (design docs, also on `feat/packet-trace-design`), `a5a4cd1` (PT0.2 code) |
| Mode | Review only. Built and tested in a private clone `/tmp/rv-pt`, `CI=true`, Perf excluded. No source changes, commits, merges or pushes. |
| Verdict | **OK TO MERGE.** 0 Blocker, 0 Major, 4 Minor, 6 Nit. All findings are non-blocking and can go in PT1.1 or a follow-up docs commit. |

## 1. CI

Run 42035784311, checked through `gh`: event `push`, branch `feat/packet-trace`, headSha `a5a4cd1ea622363c12af9b739f4006b31cdee70c`, **conclusion success**.
It started at 08:26 EST and finished at 08:28 EST (13:26:31Z to 13:28:20Z).

| Job | Result | Tests |
|---|---|---|
| linux (`build.sh`, with coverage) | success | 597 passed, 3 skipped, 0 failed (600). The skips are the 2 TesterCaMatrix tests and `Probe_RealBackend_NativeLibraryMissing_IsNotInstalled`, which is skipped **because the ubuntu runner has libpcap** (see m1). |
| windows (`build.ps1 -NoCoverage`, `publish.ps1`, `--smoke-exit`) | success | 598 passed, 2 skipped, 0 failed (600). The real no-native test **ran and passed** here (no Npcap). Publish and `--smoke-exit` passed with SharpPcap.dll and PacketDotNet.dll in the output. |

## 2. Local verification (/tmp clone, CI=true)

- `dotnet build -c Release` with `CI=true` (TreatWarningsAsErrors, AnalysisLevel 10.0-recommended): **0 warnings, 0 errors**, with no NU1510 or other NU15xx.
- Tests: **597 passed, 3 skipped, 0 failed**.
  - PacketCapture.Tests: 15/15 passed. The box has no libpcap, so the real `NativeLibraryMissing → NotInstalled` test **ran and passed on Linux**.
  - Presentation.Tests: 61 passed. Core: 322 passed and 3 skipped (2 TesterCaMatrix, plus the live DNS test, which skips in this sandbox).
- Coverage of `AcsTool.PacketCapture` (coverlet, from PacketCapture.Tests): **line 100 %, branch 84.6 %**. The missed branches are the `OperatingSystem.IsWindows()` arms, which Linux can't hit.
- `dotnet list package --vulnerable --include-transitive`: **no vulnerable packages** in any project. `--deprecated` reports only `xunit 2.9.3` (Legacy, use xunit.v3). That is the existing n3 follow-up from the net10 review, not new.
- Resolved packages for PacketCapture:
  - Top-level: SharpPcap 6.3.1, PacketDotNet 1.4.8, DI.Abstractions 10.0.14.
  - Transitive: Logging.Abstractions 10.0.14, from Core.
  - SharpPcap's `System.Memory 4.6.3` and `System.Text.Encoding.CodePages 9.0.5`, and PacketDotNet's `System.Runtime.CompilerServices.Unsafe 6.0.0`, are pruned on net10 and come from the framework. No NU1510.
- Pinning: both versions are set centrally in `Directory.Packages.props:8-9`, and PacketDotNet is referenced explicitly as design §4 requires. NuGet's minimum-version semantics resolve them exactly.
  - The repo has no lock file, which is the existing policy. This is fine.
- Licences, from the nuspecs: SharpPcap is `MIT` (repo commit bfedf297) and PacketDotNet is `MPL-2.0` (commit 690707ce…, which matches the notices file).

### 2.1 Lazy native loading / --smoke-exit (key requirement): verified

- **Reasoning:**
  - `SharpPcapBackend` has no static fields and no static constructor.
  - The public ctor only builds two delegates (`CountLiveDevices` and `() => Pcap.Version`), so it doesn't JIT or touch any SharpPcap type.
  - `Classify` (which references `PcapException`) and the lambdas only JIT when `Probe()` runs.
  - DI registration is `TryAddSingleton<ICaptureBackend, SharpPcapBackend>`: lazy, and constructed only when resolved.
  - Nothing on the startup path depends on `ICaptureBackend`. In `App.OnStartup`, `MainViewModel` and `MainWindow` don't take it, and the Generic Host doesn't ValidateOnBuild in Production. Even ValidateOnBuild wouldn't construct it.
  - SharpPcap's own cctor (`LibPcapSafeNativeMethods`) only runs on the first P/Invoke.
- **Run (scratch test in the private clone, since deleted):**
  - Steps: `AppHarness.Create()`, then resolve `MainViewModel`, `await InitializeAsync()`, and resolve `ICaptureBackend`. Then check `AppDomain.GetAssemblies()`.
  - Result: **neither SharpPcap nor PacketDotNet is loaded**. After `Probe()`, both are loaded (positive control) and the status is `NotInstalled`.
  - So the managed SharpPcap assembly isn't even loaded at startup, which means the native library can't be.
- **Windows CI** `--smoke-exit` on the published exe also passed (without Npcap installed).

### 2.2 Probe never throws: verified

- `Probe()` wraps the whole enumeration in `catch (Exception)` with a justified CA1031 suppression. `SafeVersion()` guards the version read separately.
- Uncatchable exceptions (StackOverflow, and OOM in practice) are out of scope.
- Exception mapping:
  - **DllNotFoundException**, direct or nested in a TypeInitialization, TargetInvocation or Aggregate exception, maps to NotInstalled. There are unit tests for both forms, plus the real run.
    - Real run on Linux without libpcap: `CaptureDeviceList.New()` throws a plain `DllNotFoundException` ("Unable to load shared library 'wpcap'") on **every** call, 3 out of 3. No `TypeInitializationException` gets cached.
    - On Linux, SharpPcap's cctor only registers a resolver.
    - On Windows, the cctor calls `pcap_init` but catches `TypeLoadException`, which is the base of DllNotFoundException. So the cctor succeeds without Npcap, and the failure isn't cached as a broken type either. Two probes in a row both returned NotInstalled.
  - **BadImageFormatException** (wrong-arch wpcap) maps to Error, with the type name in the message. It's tested.
  - **UnauthorizedAccessException**, and **PcapException** whose message mentions access, permission or privilege, map to AccessDenied. Other PcapExceptions map to Error. Tested by building `PcapException` through its internal ctor.
  - A version read that throws gives Available with Version=null. Tested.
- **Result type:** `CaptureBackendInfo(Status, Version?, Message, DeviceCount)` plus `IsAvailable` is sensible and matches design §5.2. `DeviceCount` is an additive extra.
- **Disposal:** `CaptureDeviceList.New()` only builds unopened `LibPcapLiveDevice` objects. `pcap_freealldevs` is called inside SharpPcap (`PcapInterface.GetAllPcapInterfaces`), and `Dispose()` on an unopened device is just `Close()`, which does nothing. So no native handle leaks. Disposing isn't required (see n3 for a cleaner call).

### 2.3 Architecture

- `AcsTool.Core` does **not** reference PacketCapture. PacketCapture → Core, and Presentation → PacketCapture. This matches design §5.1. PacketCapture doesn't use any Core types yet, which is fine; PT0.3+ will.
- No ASP.NET, WPF or MVVM in PacketCapture. No `#if`, and no separate TFM (D2 / §5.2).
- The `AddAcsToolPacketCapture()` naming and `TryAdd` style match `AddAcsToolProxy` and `AddAcsToolSimulator`.
- Scope: nothing from PT0.3/PT0.4 (no ExchangeEndpoints, no proxy wiring) and no UI.
  - `ICaptureBackend.Probe` and `SharpPcapBackend` are PT1.1 items brought forward. DevOrchestrator's brief explicitly includes them (see n1).
  - The interface shape is a strict subset of §5.2. `ListDevices` and `Open` are still to come.

### 2.4 Publish impact (win-x64 self-contained, via `publish.ps1` on Linux, compared with main 479639b)

- New files: `AcsTool.PacketCapture.dll` (10.5 KB) plus its `.pdb`, `SharpPcap.dll` (94.5 KB) and `PacketDotNet.dll` (289.5 KB). That's 4 files and about **+410 KiB** (175.36 MB → 175.78 MB).
- **No native binaries** and no `runtimes/` RID-specific assets. Neither package ships any; Npcap stays user-installed (D3).
- This meets the PT0.2 acceptance criterion "publish output contains SharpPcap.dll and PacketDotNet.dll".
- Known and accepted: `THIRD-PARTY-NOTICES.md` isn't in the publish output yet.

## 3. Findings

### Blocker
None.

### Major
None.

### Minor

**m1. The Linux CI runner has libpcap, which contradicts the design and task assumptions.**
- Where: `docs/PACKET_TRACE_DESIGN.md:78`, `docs/PACKET_TRACE_TASKS.md:128`, `:295`, `:376`, and the test comment at `tests/AcsTool.PacketCapture.Tests/ProbeTests.cs:32`.
- What happens:
  - On ubuntu-latest, `Probe_RealBackend_NativeLibraryMissing_IsNotInstalled` is skipped with the message "Npcap/libpcap is installed here".
  - `Probe_RealBackend_NeverThrows…` therefore exercises the *Available* path against the real libpcap.
  - The real NotInstalled path is covered only by the Windows runner and by local runs.
- This is fine as coverage (both paths are now exercised in CI). But PT1.1's acceptance "On Linux CI without libpcap: Probe() returns NotInstalled" can't be met as written, and future live tests could start running on Linux CI unexpectedly.
- Fix:
  - Reword design §3 row "CI", TASKS PT1.1 acceptance and PT1-A1/§376 to: "Linux runner has libpcap (Available path, non-root); Windows runner has no Npcap (NotInstalled path)".
  - Update the comment at ProbeTests.cs:32 to match.
  - Keep live capture gated on `ACSTOOL_PCAP_LIVE=1`, as planned.

**m2. The composition test doesn't prove the native-free startup it's named for.**
- Where: `tests/AcsTool.Presentation.Tests/SmokeTests.cs:22-30`.
- `AddAcsToolPresentation_RegistersPacketCaptureBackend_WithoutProbing` only asserts the registered type, so a future eager `Probe()` in a VM ctor or `InitializeAsync` would still pass.
- Fix: assert that SharpPcap isn't loaded after building the shell. I verified this pattern in the scratch run:
  ```csharp
  await using var app = AppHarness.Create();
  await app.Services.GetRequiredService<MainViewModel>().InitializeAsync();
  _ = app.Services.GetRequiredService<ICaptureBackend>();
  Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "SharpPcap");
  ```
  - Presentation.Tests currently has no test that probes, so the check is stable in that assembly's process.
  - Add a comment that Presentation tests must never call `Probe()` on the real backend. Alternatively, the test could record `AppDomain.AssemblyLoad` events during the build-and-resolve.

**m3. THIRD-PARTY-NOTICES abbreviates SharpPcap's copyright line.**
- Where: `THIRD-PARTY-NOTICES.md:15`.
- The file says "Copyright (c) Tamir Gal, Chris Morgan and others." The MIT condition requires the notice itself to be included.
- Fix: copy it exactly from upstream `LICENSES/MIT.txt` (v6.3.1): `Copyright (c) Tamir Gal, Chris Morgan, Ayoub Kaanich, Noah Potash, Phillip Lemon, Evan Plaice`.

**m4. `Available` with 0 devices isn't distinguished.**
- Where: `src/AcsTool.PacketCapture/Backend/SharpPcapBackend.cs:33-36`.
- Design §5.4 calls out "Npcap installed but no devices" (driver broken after a Windows feature update, or BFE stopped) as its own message. The probe currently says "Npcap is available (0 capture device(s))." and `IsAvailable` is true.
- Fix (can wait for PT1.1): when `count == 0`, keep `Available` (or add a status) with a message like "Npcap is installed but no capture devices were found. The driver may need repairing (see troubleshooting)." Add a test with `() => 0`.

### Nit

**n1. The scope note is out of date.**
- `docs/PACKET_TRACE_TASKS.md:58` says PT0.2's `AddAcsToolPacketCapture()` is "empty for now". `ICaptureBackend`, `CaptureBackendInfo`/`Status` and `SharpPcapBackend.Probe()` (PT1.1 at `:117-121`) landed here.
- That was sanctioned by the brief. Update PT0.2 and PT1.1 to say Probe landed in PT0.2.
- PT0.1's findings and `tests/fixtures/pcap/` ("merged with PT0.2") aren't in yet. `spike/pt0-npcap` exists on the remote at 34811c1; track it as pending.

**n2. Wrong design section references.**
- `SharpPcapBackend.cs:8` and `ServiceCollectionExtensions.cs:11` cite "design §7", which is the memory budget. Lazy loading and detection are §5.2 / §5.4. Change them to `§5.2/§5.4`.

**n3. `CaptureDeviceList.New()` enumerates up to 4 times and fills a process-wide static.**
- Where: `SharpPcapBackend.cs:85`.
- In SharpPcap 6.3.1, the private `CaptureDeviceList` ctor first uses `LibPcapLiveDeviceList.Instance` (static, cached) and calls `Refresh`. Then `New()` builds another list and calls `Refresh` again.
- The comment at `:83-84` ("not cached inside a static") holds for failures, but on success the static Instance is populated anyway. That's harmless, but it's extra `pcap_findalldevs` calls.
- Fix: `return SharpPcap.LibPcap.LibPcapLiveDeviceList.New().Count;` gives one fresh enumeration with no static. PT1.1's `ListDevices` can use the same call.

**n4. The test's native-library candidates differ from SharpPcap's resolver.**
- Where: `ProbeTests.cs:124`.
- SharpPcap tries `libpcap.so`, `libpcap.so.0`, `libpcap.so.0.8` and `libpcap.so.1`. The test omits `libpcap.so.0`, so on a system with only that file the test wouldn't skip, and it would fail (Available ≠ NotInstalled).
- Fix: add `"libpcap.so.0"`. The extra `"wpcap"` on Linux is harmless.

**n5. BadImageFormatException gets a generic message.**
- Where: `SharpPcapBackend.cs:63-64`.
- A wrong-architecture or corrupt `wpcap.dll` maps to the generic Error, "…could not be used (BadImageFormatException)…".
- Consider a friendlier hint ("Npcap's wpcap.dll could not be loaded (wrong architecture or damaged install); reinstall Npcap") when the PT1.15 banners are written. Optional.

**n6. The notices intro overstates its coverage.**
- Where: `THIRD-PARTY-NOTICES.md:3-4`.
- It says Microsoft packages are MIT and aren't listed, but Serilog (Apache-2.0) and CommunityToolkit.Mvvm (MIT, .NET Foundation) aren't covered either.
- Either scope the intro to the packet-capture dependencies for now, or add them when the file is copied into publish output (the known nit).

## 4. Not verifiable on Linux (Windows checklist / PT0.1 spike)

- The Available path against a real **Npcap** on Windows: device count, `Pcap.Version` text, and the `SetDllDirectory(System32\Npcap)` resolution.
- **Admin-only Npcap with a non-admin user:** whether `pcap_findalldevs` fails (and with what PcapException text, which the "access/permission/privilege" heuristic must match) or succeeds and the failure only appears at `Open` (expected per §5.4, which would make it a PT1.1 concern).
- **Installing Npcap while the app runs**, with the next probe picking it up. On Linux, failures were confirmed not to be cached. On Windows, the cctor's process-wide `SetDllDirectory` and the non-UTF-8 encoding fallback after a failed `pcap_init` weren't exercised.
- `--smoke-exit` and startup **with Npcap installed** (CI only covers "not installed"). Reasoning and the assembly-load check show nothing is touched at startup, so this is low risk.
- Wrong-architecture `wpcap.dll` and legacy WinPcap in System32.
