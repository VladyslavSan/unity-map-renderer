#!/usr/bin/env bash
# Package built players into single-file, SYMBOL-FREE archives for distribution (e.g. GitHub Releases).
# Self-locating: run from anywhere inside the repo.
#
#   Tools/package.sh [android|macos|linux|release|desktop|all] [version]   (default target: all)
#
# Reads built players from Builds/<Platform>/ and writes archives to Builds/dist/:
#   android -> UnityMapRenderer-android-<ver>.apk     (the APK, as-is — already one file)
#   linux   -> UnityMapRenderer-linux-<ver>.tar.gz    (exe + _Data + UnityPlayer.so + libs)
#   macos   -> UnityMapRenderer-macos-<ver>.zip       (the .app bundle, via ditto)
#
# WHY THIS EXISTS: Unity drops "_BackUpThisFolder_ButDontShipItWithYourGame" (IL2CPP C++ source +
# symbols) and "_BurstDebugInformation_DoNotShip" next to the player. Sweeping the whole Builds/<P>
# folder into an archive would LEAK that source/symbols. This script excludes them by construction and
# then re-opens each archive to ASSERT no such entry slipped in (hard-fails if one did).
#
# Publish the results as GitHub Release assets (`gh release create <tag> Builds/dist/*`) — do NOT commit
# binaries into the repo (/Builds/ is git-ignored on purpose).
#
# version defaults to PlayerSettings bundleVersion from ProjectSettings.asset.
# Exit: 0 = every requested platform packaged + verified clean; else the number that failed/were missing.
set -uo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repo" >&2; exit 2; }
. "$ROOT/Tools/lib.sh"   # PRODUCT + build_artifact: where BuildScript actually writes
TARGET="${1:-all}"
VERSION="${2:-$(awk '/^  bundleVersion:/ {print $2; exit}' "$ROOT/ProjectSettings/ProjectSettings.asset" 2>/dev/null)}"
[ -n "${VERSION:-}" ] || VERSION="0.0.0"

BUILDS="$ROOT/$OUTPUT_ROOT"
DIST="$BUILDS/dist"
mkdir -p "$DIST"

# Regex of forbidden entry markers — used both to exclude and to verify.
LEAK_RE='_BackUpThisFolder_ButDontShipItWithYourGame|_BurstDebugInformation_DoNotShip'

case "$TARGET" in
  android) PLATFORMS=(android) ;;
  macos)   PLATFORMS=(macos) ;;
  linux)   PLATFORMS=(linux) ;;
  release) PLATFORMS=(android macos linux) ;;
  desktop) PLATFORMS=(macos linux) ;;
  all)     PLATFORMS=(android macos linux) ;;
  *) echo "unknown target '$TARGET' (want: android|macos|linux|release|desktop|all)" >&2; exit 2 ;;
esac

# Assert an archive listing contains no leak markers. $1 = archive, $2 = listing command output.
assert_clean() { # $1 = label, rest = listing lines on stdin
  if grep -qiE "$LEAK_RE"; then
    echo "LEAK: $1 contains debug-symbol/backup entries — refusing to ship it" >&2
    return 1
  fi
  return 0
}

package_android() {
  local apk="$ROOT/$(build_artifact android)"
  [ -f "$apk" ] || { echo "android: no APK at $apk — build it first (Tools/build.sh android)" >&2; return 1; }
  local out="$DIST/$PRODUCT-android-$VERSION.apk"
  cp -f "$apk" "$out" || return 1
  # An APK is a single self-contained file; the sibling _BurstDebugInformation folder is never inside it.
  echo "  android -> $out"
}

package_linux() {
  local dir="$ROOT/$(build_dir linux)"
  [ -d "$dir" ] || { echo "linux: no build at $dir — build it first (Tools/build.sh linux)" >&2; return 1; }
  local out="$DIST/$PRODUCT-linux-$VERSION.tar.gz"
  local stage="$DIST/.stage-linux/$PRODUCT-linux-$VERSION"
  rm -rf "$DIST/.stage-linux"; mkdir -p "$stage"
  # Copy the payload, dropping the scratch dirs + macOS cruft.
  rsync -a \
    --exclude='*_BackUpThisFolder_ButDontShipItWithYourGame*' \
    --exclude='*_BurstDebugInformation_DoNotShip*' \
    --exclude='.DS_Store' \
    "$dir"/ "$stage"/ || { rm -rf "$DIST/.stage-linux"; return 1; }
  tar -C "$DIST/.stage-linux" -czf "$out" "$PRODUCT-linux-$VERSION" || { rm -rf "$DIST/.stage-linux"; return 1; }
  rm -rf "$DIST/.stage-linux"
  tar tzf "$out" | assert_clean "$out" || { rm -f "$out"; return 1; }
  echo "  linux   -> $out"
}

package_macos() {
  local app="$ROOT/$(build_artifact macos)"
  [ -d "$app" ] || { echo "macos: no .app at $app — build it first (Tools/build.sh macos)" >&2; return 1; }
  local out="$DIST/$PRODUCT-macos-$VERSION.zip"
  # Archive ONLY the .app bundle (the _DoNotShip/_BackUp dirs are siblings in Builds/macOS, never inside
  # the bundle). ditto preserves the bundle layout + symlinks; plain `zip` can corrupt a .app.
  rm -f "$out"
  if command -v ditto >/dev/null 2>&1; then
    ditto -c -k --keepParent "$app" "$out" || return 1
  else
    ( cd "$ROOT/$(build_dir macos)" && zip -qry "$out" "$PRODUCT.app" ) || return 1  # non-mac fallback
  fi
  if command -v unzip >/dev/null 2>&1; then
    unzip -Z1 "$out" | assert_clean "$out" || { rm -f "$out"; return 1; }
  fi
  echo "  macos   -> $out"
}

echo "Packaging $TARGET (version $VERSION) -> $DIST" >&2
FAILED=0
for p in "${PLATFORMS[@]}"; do
  "package_$p" || FAILED=$((FAILED + 1))
done

if [ "$FAILED" -eq 0 ]; then
  echo "Done. Archives (symbol-free) in: $DIST" >&2
  echo "Publish with: gh release create v$VERSION $DIST/*" >&2
  exit 0
fi
echo "$FAILED platform(s) failed or were missing." >&2
exit "$FAILED"
