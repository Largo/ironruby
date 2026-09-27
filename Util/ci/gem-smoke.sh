#!/bin/bash
# Installs a small pure-Ruby gem and its dependency from rubygems.org into an empty
# GEM_HOME with IronRuby's own gem command, requires it, and then does the same
# through Bundler (a Gemfile, `bundle install`, `require "bundler/setup"`).  The
# downloads go over IronRuby's TLS, the unpacking through its zlib and tar reader, and
# the gem executables' wrappers through its File and path code - on Windows all of
# that is otherwise untested.
#
#   Util/ci/gem-smoke.sh
set -eu
IR="./ir.sh"
IGEM="./igem.sh"
if [ "${RUNNER_OS:-}" = Windows ] || [ "${OS:-}" = Windows_NT ]; then
  IR="cmd //c ir.cmd"
  IGEM="cmd //c igem.cmd"
fi
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
# On Windows these reach ir.exe as native paths.
native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

export GEM_HOME="$(native "$WORK/gemhome")"
export GEM_PATH="$GEM_HOME"
export IRONRUBY_NO_HOST_GEMS=1

check() { # expected actual what
  if [ "$1" = "$2" ]; then echo "ok   $3: $2"; else echo "FAILED: $3: expected [$1], got [$2]"; exit 1; fi
}

# pastel depends on tty-color: both come down, and are resolved as a pair.
$IGEM install pastel --no-document
ls "$WORK/gemhome/gems"
check "pastel tty-color" "$(ls "$WORK/gemhome/gems" | sed 's/-[0-9.]*$//' | sort | xargs)" "installed gems"

check '"\e[31mok\e[0m" tty-color' \
  "$($IR -e 'require "pastel"; print Pastel.new(enabled: true).red("ok").inspect, " ", Gem.loaded_specs.keys.grep(/tty/).join(",")' | tr -d '\r')" \
  "require from GEM_HOME"

$IGEM list pastel | tr -d '\r'
check "true" "$($IR -e 'print Gem::Specification.find_by_name("pastel").full_gem_path.start_with?(ENV["GEM_HOME"].tr("\\", "/"))' | tr -d '\r')" \
  "spec under GEM_HOME"

# Bundler: resolve, install into a path of its own, and load through bundler/setup.
mkdir -p "$WORK/app"
cat > "$WORK/app/Gemfile" <<'RUBY'
source "https://rubygems.org"
gem "pastel"
RUBY
export BUNDLE_GEMFILE="$(native "$WORK/app/Gemfile")"
export BUNDLE_PATH="$(native "$WORK/bundle")"
$IR -S bundle install
grep -q "pastel" "$WORK/app/Gemfile.lock" || { echo "FAILED: no Gemfile.lock entry"; exit 1; }
check '"\e[32mbundled\e[0m"' \
  "$($IR -e 'require "bundler/setup"; require "pastel"; print Pastel.new(enabled: true).green("bundled").inspect' | tr -d '\r')" \
  "require through bundler/setup"
echo "gem smoke test passed"
