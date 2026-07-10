#!/usr/bin/env bash
# Headless Unity test runner for unity-map-renderer.
# Self-locating: run from anywhere inside the repo.
#
#   Tools/run-tests.sh [EditMode|PlayMode] [testFilter]   (default: EditMode, no filter)
#
# testFilter (optional) is passed straight to Unity's -testFilter (a regex over test
# full names), e.g. 'MapRenderer.Tests.Visual' runs just the snapshot suites. Startup
# (asset import + domain reload) still dominates; the filter only trims which tests run.
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
FILTER="${2:-}"

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

# Batch mode can't share the project with an open Editor — but only THIS project's Editor matters.
# Refuse iff a LIVE Unity process has this exact project open (-projectPath == $ROOT); a Unity editing
# a *different* clone (e.g. unity-map-renderer-test) is fine and must not block us. Exact-equality is
# deliberate: a substring match would wrongly fire on a sibling like "${ROOT}-test". The lowercase
# compare handles the Editor's `-projectpath` vs batch mode's `-projectPath`. A lockfile with no such
# process is STALE (a prior batch run was killed and left it behind); clear it rather than refuse forever.
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
  echo "The Unity Editor for THIS project is open — close it before running batch tests." >&2
  echo "(A Unity editing a different clone is fine.)" >&2
  exit 3
fi
if [ -e "$ROOT/Temp/UnityLockfile" ]; then
  echo "Stale Unity lockfile present but this project's Editor isn't open — removing it and continuing." >&2
  rm -f "$ROOT/Temp/UnityLockfile"
fi

mkdir -p "$ROOT/Logs"
RESULTS="$ROOT/Logs/test-results.xml"
LOG="$ROOT/Logs/test-run.log"

run_unity() { # $1 = testResults path, $2 = logFile path
  "$UNITY" -runTests -batchmode -projectPath "$ROOT" \
    -testPlatform "$PLATFORM" \
    ${FILTER:+-testFilter "$FILTER"} \
    -testResults "$1" \
    -logFile "$2"
}

# Cold- OR stale-shader-cache warm-up pass.
# In batchmode, an un-cached shader variant compiles ASYNChronously and lands AFTER the first render
# that needs it, so the measuring render is wrong (blank fills; a _NORMALMAP keyword toggle that
# silently no-ops) — not real regressions. This bites two ways:
#   • cold  cache — a fresh clone / wiped Library has no compiled variants at all; and
#   • stale cache — you EDITED a shader, so the cache is non-empty but the changed shader's variants
#                   are out of date and recompile (async) on first use.
# A throwaway warm-up pass compiles+PERSISTS the current variants to Library/ShaderCache so the real
# pass (a fresh process) reads them warm. A stamp file records the last FULL warmed run; we warm again
# whenever the cache is empty, never warmed, or any .shader/.hlsl is newer than the stamp. Warm,
# unchanged runs (the common case) pay nothing. Opt out with UMR_SKIP_SHADER_WARMUP=1. See
# docs/lessons-learned.md.
SHADER_CACHE="$ROOT/Library/ShaderCache"
WARM_STAMP="$ROOT/Library/.umr-shader-warm-stamp"
shaders_need_warmup() {
  [ -d "$SHADER_CACHE" ] || return 0                          # cache absent  => cold
  [ -z "$(ls -A "$SHADER_CACHE" 2>/dev/null)" ] && return 0   # cache empty   => cold
  [ -f "$WARM_STAMP" ] || return 0                            # never warmed  => warm up
  # A shader source edited since the last warmed run => its variants are stale.
  [ -n "$(find "$ROOT/Assets" -type f \( -name '*.shader' -o -name '*.hlsl' \) \
            -newer "$WARM_STAMP" -print 2>/dev/null | head -1)" ] && return 0
  return 1
}
if [ -z "${UMR_SKIP_SHADER_WARMUP:-}" ] && shaders_need_warmup; then
  echo "Cold/stale shader cache — running a throwaway warm-up pass first so GPU-snapshot variants" >&2
  echo "are compiled before the measuring run (set UMR_SKIP_SHADER_WARMUP=1 to skip)." >&2
  run_unity "$ROOT/Logs/test-results.warmup.xml" "$ROOT/Logs/test-run.warmup.log"
  echo "Warm-up pass complete (shader variants cached) — running the real test pass." >&2
fi

run_unity "$RESULTS" "$LOG"
CODE=$?

# Stamp the cache as warm-for-the-current-shaders, so the next run skips the warm-up unless a shader
# changes. Only a FULL (unfiltered) run exercises every variant, so only it may claim the whole set is
# warm; a filtered run warms just its subset and must not stamp the full set as current.
if [ -z "$FILTER" ] && [ -d "$SHADER_CACHE" ] && [ -n "$(ls -A "$SHADER_CACHE" 2>/dev/null)" ]; then
  touch "$WARM_STAMP"
fi

echo "exit: $CODE"
if [ -f "$RESULTS" ]; then
  grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$RESULTS" | head -1
  grep -oE '<test-case [^>]*' "$RESULTS" \
    | sed -E 's/.*name="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
fi
grep -E 'error CS' "$LOG" 2>/dev/null | sort -u
exit $CODE
