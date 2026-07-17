#!/usr/bin/env bash
# Headless Unity player builds for unity-map-renderer.
# Self-locating: run from anywhere inside the repo.
#
#   Tools/build.sh <target>
#
# target:
#   android   -> Builds/Android/UnityMapRenderer.apk        (debug-signed APK, sideload/preview)
#   macos     -> Builds/macOS/UnityMapRenderer.app          (unsigned .app — Gatekeeper caveat below)
#   linux     -> Builds/Linux/UnityMapRenderer.x86_64       (+ _Data folder)
#   release   -> android + macos + linux                    (the public release set)
#   desktop   -> macos + linux
#   all       -> android + macos + linux
#
# Scenes come from Build Settings (EditorBuildSettings). For a PUBLIC build, enable the
# OpenStreetMapLiberty scene and DISABLE the internal MapDemo scene first — otherwise the build
# ships the dev scene with no on-screen attribution. BuildScript logs the scene list it builds.
#
# Distribution caveats (neither blocks building):
#   - macOS .app is UNSIGNED: on another Mac, right-click ▸ Open or `xattr -cr <app>`; a real fix
#     needs an Apple Developer cert + notarization.
#   - Android APK with an empty keystore is DEBUG-signed: fine to sideload, not Play-Store-publishable.
#
# Exit codes: 0 = every requested build succeeded; 2 = setup error (no repo / bad target / no editor
# binary / module missing); 3 = this project's Editor is open (project locked); otherwise the number
# of builds that failed. Prints the BUILD OK / BUILD FAIL sentinel and any `error CS` lines per target.
# The first Android build is slow (build-target switch = full reimport + IL2CPP/NDK compile).
set -uo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repo" >&2; exit 2; }
VERSION="$(awk '/^m_EditorVersion:/ {print $2}' "$ROOT/ProjectSettings/ProjectVersion.txt" 2>/dev/null)"
TARGET="${1:-}"

if [ -z "$TARGET" ]; then
  echo "usage: Tools/build.sh <android|macos|linux|release|desktop|all>" >&2
  echo "  release = android + macos + linux (the public set)" >&2
  exit 2
fi

# Locate the Unity editor binary for this project's version (Unity Hub defaults) — same logic as run-tests.sh.
case "$(uname -s)" in
  Darwin)
    UNITY="/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
    [ -x "$UNITY" ] || UNITY="$HOME/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
    ;;
  Linux)
    UNITY="$HOME/Unity/Hub/Editor/$VERSION/Editor/Unity"
    ;;
  *) # Windows (Git Bash / MSYS)
    UNITY="/c/Program Files/Unity/Hub/Editor/$VERSION/Editor/Unity.exe"
    ;;
esac

if [ -z "$VERSION" ]; then echo "could not read m_EditorVersion from ProjectSettings/ProjectVersion.txt" >&2; exit 2; fi
if [ ! -x "$UNITY" ]; then echo "Unity editor for version $VERSION not found at: $UNITY" >&2; exit 2; fi

# Batch mode can't share the project with an open Editor — but only THIS project's Editor matters.
# (Verbatim from run-tests.sh: exact -projectPath equality, lowercase-compare, stale-lock clearing.)
this_project_editor_open() {
  local pid pp
  for pid in $(pgrep -x Unity 2>/dev/null); do
    pp="$(ps -ww -o command= -p "$pid" 2>/dev/null \
          | awk '{for(i=1;i<NF;i++) if(tolower($i)=="-projectpath"){print $(i+1);exit}}')"
    [ "$pp" = "$ROOT" ] && return 0
  done
  return 1
}
if this_project_editor_open; then
  echo "The Unity Editor for THIS project is open — close it before running batch builds." >&2
  echo "(A Unity editing a different clone is fine.)" >&2
  exit 3
fi
if [ -e "$ROOT/Temp/UnityLockfile" ]; then
  echo "Stale Unity lockfile present but this project's Editor isn't open — removing it and continuing." >&2
  rm -f "$ROOT/Temp/UnityLockfile"
fi

mkdir -p "$ROOT/Logs"

# Expand the target keyword into the concrete platform list.
case "$TARGET" in
  android) PLATFORMS=(android) ;;
  macos)   PLATFORMS=(macos) ;;
  linux)   PLATFORMS=(linux) ;;
  release) PLATFORMS=(android macos linux) ;;
  desktop) PLATFORMS=(macos linux) ;;
  all)     PLATFORMS=(android macos linux) ;;
  *) echo "unknown target '$TARGET' (want: android|macos|linux|release|desktop|all)" >&2; exit 2 ;;
esac

# platform -> Unity -buildTarget mnemonic + BuildScript method
btarget_for() { case "$1" in android) echo Android ;; macos) echo OSXUniversal ;; linux) echo Linux64 ;; esac; }
method_for()  { case "$1" in
    android) echo MapRenderer.Build.BuildScript.BuildAndroid ;;
    macos)   echo MapRenderer.Build.BuildScript.BuildMacOS ;;
    linux)   echo MapRenderer.Build.BuildScript.BuildLinux ;;
  esac; }

build_one() {
  local name="$1"
  local btarget method log code
  btarget="$(btarget_for "$name")"
  method="$(method_for "$name")"
  log="$ROOT/Logs/build-$name.log"
  echo ">>> Building $name (target=$btarget) ... log: $log" >&2
  "$UNITY" -batchmode -nographics -projectPath "$ROOT" \
    -buildTarget "$btarget" \
    -executeMethod "$method" \
    -logFile "$log"
  code=$?
  # BuildScript prints the sentinel; surface it plus any compile errors.
  grep -aE 'BUILD OK|BUILD FAIL' "$log" 2>/dev/null | tail -3
  grep -aE 'error CS' "$log" 2>/dev/null | sort -u | head
  echo "$name exit: $code" >&2
  return $code
}

FAILED=0
for p in "${PLATFORMS[@]}"; do
  build_one "$p" || FAILED=$((FAILED + 1))
done

if [ "$FAILED" -eq 0 ]; then
  echo "All requested builds succeeded." >&2
  exit 0
fi
echo "$FAILED build(s) failed." >&2
exit "$FAILED"
