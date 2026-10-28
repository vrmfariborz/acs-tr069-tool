#!/usr/bin/env bash
# Restore, build everything (incl. the WPF App, compile-only on Linux) and run all tests.
# Usage: ./build.sh [--no-coverage] [--include-perf]
set -euo pipefail
cd "$(dirname "$0")"
if [ -d "$HOME/.dotnet" ] && ! command -v dotnet >/dev/null 2>&1; then
  export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COVERAGE=(--collect:"XPlat Code Coverage")
FILTER=(--filter "Category!=Perf")
for a in "$@"; do
  case "$a" in
    --no-coverage) COVERAGE=() ;;
    --include-perf) FILTER=() ;;
  esac
done
dotnet restore AcsTool.sln
dotnet build AcsTool.sln -c Release --no-restore
dotnet test AcsTool.sln -c Release --no-build "${FILTER[@]}" "${COVERAGE[@]}" --results-directory TestResults
