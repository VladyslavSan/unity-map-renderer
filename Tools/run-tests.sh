#!/usr/bin/env bash
# Headless Unity test runner for unity-map-renderer.
# Self-locating: run from anywhere inside the repo.
#
#   Tools/run-tests.sh [EditMode|PlayMode]   (default: EditMode)
#
# Exit codes: 0 = compiled AND all tests passed; 2 = setup error (no repo / no
# editor binary); 3 = a live Unity process is running (Editor open, project locked);
# otherwise Unity's own non-zero exit (compile error or test failure). Prints a result
# summary and any `error CS` lines regardless of exit code, so don't trust the code alone.
# A *stale* lockfile (present but no Unity process — e.g. a prior batch run was killed)
# is cleared automatically; only a live process makes this refuse.
set -uo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repo" >&2; exit 2; }
VERSION="$(awk '/^m_EditorVersion:/ {print $2}' "$ROOT/ProjectSettings/ProjectVersion.txt" 2>/dev/null)"
PLATFORM="${1:-EditMode}"

# Locate the Unity editor binary for this project's version (Unity Hub defaults).
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

# Batch mode can't share the project with an open Editor. A LIVE Unity process means the Editor is
# genuinely open — refuse. A lockfile with NO running process is STALE (a prior batch run was killed
# and left it behind); clear it and proceed rather than refusing forever.
if pgrep -x Unity >/dev/null 2>&1; then
  echo "A Unity process is running (Editor open) — close it before running batch tests." >&2
  exit 3
fi
if [ -e "$ROOT/Temp/UnityLockfile" ]; then
  echo "Stale Unity lockfile present but no Unity process running — removing it and continuing." >&2
  rm -f "$ROOT/Temp/UnityLockfile"
fi

mkdir -p "$ROOT/Logs"
RESULTS="$ROOT/Logs/test-results.xml"
LOG="$ROOT/Logs/test-run.log"

"$UNITY" -runTests -batchmode -projectPath "$ROOT" \
  -testPlatform "$PLATFORM" \
  -testResults "$RESULTS" \
  -logFile "$LOG"
CODE=$?

echo "exit: $CODE"
if [ -f "$RESULTS" ]; then
  grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$RESULTS" | head -1
  grep -oE '<test-case [^>]*' "$RESULTS" \
    | sed -E 's/.*name="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
fi
grep -E 'error CS' "$LOG" 2>/dev/null | sort -u
exit $CODE
