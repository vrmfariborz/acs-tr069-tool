# Self-contained Windows x64 publish of the WPF app (NFR-13). Run on Windows.
# Usage: ./publish.ps1 [-Output out/win-x64]
param([string]$Output = 'out/win-x64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish src/AcsTool.App/AcsTool.App.csproj -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "Published to $Output. Run $Output\AcsTool.exe"
