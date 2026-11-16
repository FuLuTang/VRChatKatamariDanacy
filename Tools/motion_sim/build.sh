#!/usr/bin/env bash
# Build the motion_sim CLI.  The editor builds it on demand too
# (Tools/motion_sim/motion_cli.py); this script is for a human or an agent who wants the
# build in isolation, e.g. to see compiler output.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet="${MOTION_SIM_DOTNET:-${DSH_DOTNET:-}}"
if [[ -z "$dotnet" ]]; then
  if command -v dotnet >/dev/null 2>&1; then dotnet="$(command -v dotnet)"
  elif [[ -x "$HOME/.dotnet/dotnet" ]]; then dotnet="$HOME/.dotnet/dotnet"
  else
    echo "no dotnet found; run python3 Tools/csharp_check/check.py once, or set MOTION_SIM_DOTNET" >&2
    exit 2
  fi
fi
DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 "$dotnet" build "$here/motion_sim.csproj" -c Release
