# AcsTool — ACS TR-069 Proxy & Inform Simulator

A Windows desktop tool (WPF, .NET 10) that sits between CPEs and an ACS as a capturing TR-069/CWMP proxy, and simulates CPEs sending Informs.
See `docs/PLAN.md` for the design and `docs/IMPLEMENTATION_NOTES.md` for status, deviations and known gaps.

## Projects
- `src/AcsTool.Core`: CWMP model, parser, builders, session store, settings (net10.0).
- `src/AcsTool.Proxy`: Kestrel-based capturing proxy (net10.0).
- `src/AcsTool.Simulator`: CPE profiles and Inform simulator (net10.0).
- `src/AcsTool.Presentation`: UI-free view models (net10.0).
- `src/AcsTool.App`: WPF shell (net10.0-windows). It compiles on Linux with `EnableWindowsTargeting`.
- `tests/*`: xUnit tests. `AcsTool.Testing` holds FakeAcs and the shared fakes.

## Build and test (Linux or Windows)
```bash
./build.sh                    # restore + Release build + tests (with coverage, excluding Perf)
./build.sh --no-coverage
dotnet build AcsTool.sln -c Release
dotnet test AcsTool.sln -c Release
```

## Running on Windows
Requires the .NET 10 SDK (10.0.403 or a newer 10.0.x, see `global.json`; x64) to build. The published exe is self-contained and does not need the SDK.
```powershell
dotnet build AcsTool.sln -c Release
dotnet test AcsTool.sln -c Release
.\build.ps1                                   # same as build.sh
dotnet run --project src\AcsTool.App -c Release
.\publish.ps1                                 # self-contained win-x64 -> out\win-x64\AcsTool.exe
.\out\win-x64\AcsTool.exe
Start-Process .\out\win-x64\AcsTool.exe -ArgumentList '--smoke-exit' -Wait -PassThru | % ExitCode   # 0 = OK
```
Files: settings and profiles are in `%APPDATA%\AcsTool`, logs in `%LOCALAPPDATA%\AcsTool\logs`. Upstream passwords are stored DPAPI-protected.
The first time the proxy listens on a non-loopback address, Windows Firewall may prompt. Allow it on private networks so that real CPEs can reach port 7547.

Quick check: start the proxy on 7547 with your ACS as the upstream, point a simulator profile at `http://127.0.0.1:7547/acs`, and click "Send Inform now". The full manual checklist is in `docs/IMPLEMENTATION_NOTES.md`.
