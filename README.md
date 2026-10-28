# AcsTool — ACS TR-069 Proxy & Inform Simulator

See `docs/PLAN.md` for the design and `docs/IMPLEMENTATION_NOTES.md` for deviations and status.

## Build (Linux or Windows)

```bash
./build.sh            # restore + build all (WPF App compiles only on Linux) + tests with coverage
./build.sh --no-coverage
```
```powershell
./build.ps1           # same on Windows / PowerShell
./publish.ps1         # self-contained win-x64 publish to out/win-x64
```
