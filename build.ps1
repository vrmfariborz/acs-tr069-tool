# Restore, build everything and run all tests (Windows or Linux PowerShell).
# Usage: ./build.ps1 [-NoCoverage] [-IncludePerf]
param([switch]$NoCoverage, [switch]$IncludePerf)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
# CI must run the unroutable-address connect test; the skip switch is for local sandboxes only (BUG-03 follow-up).
if ($env:CI -eq 'true' -and $env:ACSTOOL_SKIP_NET_TIMEOUT) {
    Write-Error 'ACSTOOL_SKIP_NET_TIMEOUT must not be set in CI (it would skip Connect_UnroutableAddress_ConnectTimedOut)'
    exit 1
}
$testArgs = @('test', 'AcsTool.sln', '-c', 'Release', '--no-build', '--results-directory', 'TestResults')
if (-not $IncludePerf) { $testArgs += @('--filter', 'Category!=Perf') }
if (-not $NoCoverage) { $testArgs += @('--collect:XPlat Code Coverage') }
dotnet restore AcsTool.sln; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build AcsTool.sln -c Release --no-restore; if ($LASTEXITCODE) { exit $LASTEXITCODE }
& dotnet @testArgs; exit $LASTEXITCODE
