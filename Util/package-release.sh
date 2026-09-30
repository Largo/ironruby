#!/usr/bin/env bash
# Builds a binary release of IronRuby for one platform:
#
#   Util/package-release.sh linux-x64            # -> dist/ironruby-<version>-linux-x64.tar.gz
#   Util/package-release.sh win-x64 out          # -> out/ironruby-<version>-win-x64.zip
#   IR_VERSION=4.0.0-preview1 Util/package-release.sh osx-arm64
#
# The archive keeps the source tree's layout - the launchers at the top, the build in
# Src/Console/bin/Release/<tfm>, the standard library in Src/StdLib - so ir.sh/ir.cmd,
# igem, irb and irubyc work from the unpacked directory exactly as they do in a
# checkout (ir.sh picks the Release build when there is no Debug one).
#
# The build is self-contained, so it runs on a machine with no .NET installed, and
# ReadyToRun, so IronRuby's own assemblies start as native code (see publish-r2r.sh).
# irubyc still needs the .NET SDK, as it does from a checkout.
#
# It needs what a normal build needs: ../dlr with Build/dlr-patches applied, and
# ../prism/build holding the libprism for this platform. The RID must be this
# machine's: the archive carries this machine's libprism.
set -euo pipefail

RID=${1:?usage: Util/package-release.sh RID [OUTDIR]}
IR_ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$(mkdir -p "${2:-dist}" && cd "${2:-dist}" && pwd)
: "${IR_TFM:=net8.0}"
VERSION=${IR_VERSION:-$(sed -n 's/.*DisplayVersion = "\(.*\)";.*/\1/p' "$IR_ROOT/Src/Ruby/CurrentVersion.cs")}
NAME="ironruby-$VERSION-$RID"
STAGE="$OUT/$NAME"

rm -rf "$STAGE"
mkdir -p "$STAGE/Src"

dotnet publish "$IR_ROOT/Src/Console/Ruby.Console.csproj" \
  -c Release -r "$RID" --self-contained true -f "$IR_TFM" \
  -p:IronRubyTargetFrameworks="$IR_TFM" -p:PublishReadyToRun=true \
  -o "$STAGE/Src/Console/bin/Release/$IR_TFM"
rm -f "$STAGE/Src/Console/bin/Release/$IR_TFM"/*.pdb

case "$RID" in
  win-*) launchers=(ir.cmd irb.cmd igem.cmd iirb.cmd irubyc.cmd) ;;
  *)     launchers=(ir.sh irb.sh igem.sh iirb.sh irubyc.sh) ;;
esac
for f in "${launchers[@]}" README.md; do
  cp "$IR_ROOT/$f" "$STAGE/"
done
cp -R "$IR_ROOT/Src/StdLib" "$STAGE/Src/"
rm -f "$STAGE/Src/StdLib/StdLib.rbproj"
cp -R "$IR_ROOT/Src/Public" "$STAGE/License"

# A quick check that the unpacked tree starts, before it is archived.
case "$RID" in
  win-*) (cd "$STAGE" && cmd //c ir.cmd -e 'puts RUBY_DESCRIPTION') ;;
  *)     "$STAGE/ir.sh" -e 'puts RUBY_DESCRIPTION' ;;
esac

cd "$OUT"
case "$RID" in
  win-*)
    rm -f "$NAME.zip"
    if command -v 7z >/dev/null; then 7z a -tzip -bd "$NAME.zip" "$NAME" >/dev/null
    else zip -qr "$NAME.zip" "$NAME"; fi
    ARCHIVE="$NAME.zip" ;;
  *)
    tar -czf "$NAME.tar.gz" "$NAME"
    ARCHIVE="$NAME.tar.gz" ;;
esac
echo "packaged: $OUT/$ARCHIVE"
