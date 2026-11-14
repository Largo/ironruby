#!/bin/bash
# Makes a worktree an experiment can be built in without disturbing this checkout's build,
# which stays the baseline every measurement is taken against.
#
#   Util/perf/worktree.sh NAME      # ../wt-NAME on branch perf/NAME, DLR copy ../dlr-NAME
#   Util/perf/worktree.sh -d NAME   # remove both (the branch stays)
#
# The worktree is a sibling of this checkout so that ../prism still resolves, gets the two
# native libraries the session hook built here (they are not tracked), and a DLR copy of
# its own: worktrees sharing one DLR checkout share its obj/ and fail with bogus CS0246
# when they build at the same time. Prints the build command for the new tree.
set -e -o pipefail      # an incomplete tar of the DLR must stop the script, not be extracted and built
ROOT="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
UP="$(dirname "$ROOT")"
DLR="${DLR_SOURCE_DIR:-$UP/dlr/src/core}"; DLR="${DLR%/}"; DLR_TOP="${DLR%/src/core}"
if [ "$1" = "-d" ]; then
  git -C "$ROOT" worktree remove --force "$UP/wt-$2"; rm -rf "$UP/dlr-$2"; exit 0
fi
N="$1"; [ -n "$N" ] || { echo "usage: $0 [-d] NAME" >&2; exit 1; }
git -C "$ROOT" worktree add -q -b "perf/$N" "$UP/wt-$N" HEAD
mkdir -p "$UP/dlr-$N"
tar -C "$DLR_TOP" --exclude=obj --exclude=bin --exclude=.git -cf - . | tar -C "$UP/dlr-$N" -xf -
for d in Src/Libraries/Markly/native/build Src/Libraries/Rbs/native/build; do
  [ -d "$ROOT/$d" ] && mkdir -p "$UP/wt-$N/$d" && cp -r "$ROOT/$d/." "$UP/wt-$N/$d/"
done
echo "tree:  $UP/wt-$N   (branch perf/$N)"
echo "build: cd $UP/wt-$N && dotnet build Src/Console/Ruby.Console.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 -p:DlrSourceDir=$UP/dlr-$N/src/core/ -p:CheckDlrPatches=false -v q | tail -3"
echo "run:   IR_CONFIG=Release $UP/wt-$N/ir.sh script.rb"
