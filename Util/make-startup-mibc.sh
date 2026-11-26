#!/bin/sh
# Regenerates Build/startup.mibc, the profile crossgen2 uses for the composite ReadyToRun image
# of the R2R host (Ruby.Console.csproj, publish-r2r.sh) and of the release (package-release.sh),
# from a trace of the published R2R host starting up.
#
#   Util/make-startup-mibc.sh            # needs an R2R host (Util/publish-r2r.sh)
#
#   Util/make-startup-mibc.sh --check    # whether it still covers startup (exit 1 if not)
#
# The profile tells crossgen2 which methods startup runs, among them the generic
# instantiations it cannot see statically (FuncCallInstruction<...> and friends, made with
# MakeGenericType, and framework generics over value types such as Dictionary<int, ...>):
# with the framework in the same composite version bubble it precompiles them, so they are
# not JIT'd at startup. A profile older than the code only loses some of that; methods it
# names that no longer exist are ignored.
#
# Tools: dotnet-trace (`dotnet tool install -g dotnet-trace`) and dotnet-pgo, which is not on
# nuget.org but on the .NET team's public dotnet8 feed:
#   dotnet tool install -g dotnet-pgo --version 8.0.0-rc.2.23479.6 \
#     --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet8/nuget/v3/index.json
# or DOTNET_PGO=<path to dotnet-pgo.dll> (tools/net8.0/any/ in that package).
set -e
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/dotnet}"
# --check: the methods `ir -e 1` JITs at Tier0 on the R2R host, which the profile is compiled into (generic
# instantiations and framework methods; not the dynamic methods of rules and lambdas). ~40 with a profile made from
# the current code, ~150 with none; package-release.sh prints the same count for each archive.
if [ "$1" = --check ]; then
  log="$(mktemp)"
  IR_CONFIG=R2R IR_SERVER=0 DOTNET_JitStdOutFile="$log" DOTNET_JitDisasmSummary=1 "$IR_ROOT/ir.sh" -e 1
  n=$(grep "JIT compiled" "$log" | grep -v "dynamicClass" | grep -c "Tier0" || true)
  rm -f "$log"
  echo "ir -e 1 JITs $n methods at Tier0 (Build/startup.mibc of $(git -C "$IR_ROOT" log -1 --format=%cs -- Build/startup.mibc))"
  if [ "$n" -gt 60 ]; then
    echo "the startup profile looks stale: regenerate it (Util/make-startup-mibc.sh), then rebuild the R2R host"
    exit 1
  fi
  exit 0
fi
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# JIT, loader and R2R entry-point events: what dotnet-pgo needs to see every method startup runs
PROVIDERS='Microsoft-Windows-DotNETRuntime:0x1F000080018:5'
IR_CONFIG=R2R IR_SERVER=0 dotnet-trace collect --providers "$PROVIDERS" -o "$TMP/startup.nettrace" -- \
  "$IR_ROOT/ir.sh" -e 'require "set"; require "json"; JSON.parse(JSON.generate({"a" => [1, 2.5, nil]})); Set[1, 2].to_a' > /dev/null
if [ -n "$DOTNET_PGO" ]; then
  dotnet "$DOTNET_PGO" create-mibc -t "$TMP/startup.nettrace" -o "$IR_ROOT/Build/startup.mibc"
else
  dotnet-pgo create-mibc -t "$TMP/startup.nettrace" -o "$IR_ROOT/Build/startup.mibc"
fi
echo "wrote $IR_ROOT/Build/startup.mibc"
