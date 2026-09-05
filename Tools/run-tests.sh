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
# Before the Unity launch, this also runs the fast `Tools/core-tests` (dotnet, no Editor, no project
# lock) loop — see the "Fast loop" block below for why: it is part of the gate, not a separate
# convenience script, and this is what makes that true.
#
# Exit codes:
#   0 = compiled, results were written BY THIS RUN, and every test passed
#   1 = tests ran and something failed (or the run result is not "Passed")
#   2 = setup error (no repo / no editor binary)
#   3 = a live Unity process has this project open (Editor open, project locked)
#   4 = compilation failed (`error CS` in the log) — no tests ran
#   5 = Unity produced no results for this run (crashed/died before writing the XML)
#   6 = the Tools/core-tests fast loop failed — Unity was never launched
#   otherwise = Unity's own non-zero exit
#
# Do not trust Unity's exit code. It has been observed returning BOTH 0 and 1 for the same
# kind of compile failure, and when compilation fails it does not rewrite
# Logs/test-results.xml — so a naive reader sees the PREVIOUS run's green summary for code
# that never built. Two defences, neither relying on the exit code: any existing results are
# moved aside before launching (see RESULTS_PREV), so "results absent" is unambiguous; and the
# log is grepped for `error CS`. A VERDICT line is printed last, so `| tail` always shows it.
#
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
RESULTS_PREV="$ROOT/Logs/test-results.prev.xml"
LOG="$ROOT/Logs/test-run.log"

# THE staleness defence. Unity does not rewrite the results XML when compilation fails (and still
# exits 0), so an untouched file from an earlier run would be read as this run's result. Move it
# aside first: afterwards, the file existing means THIS run wrote it. The previous results stay
# available at test-results.prev.xml for comparison. Done BEFORE the fast loop below (not just
# before Unity) because exit 6 fires routinely during iteration, and a stale test-results.xml left
# in place on that path would look exactly like this run's — the same hazard the header describes.
[ -f "$RESULTS" ] && mv -f "$RESULTS" "$RESULTS_PREV"

# ── Fast loop: Tools/core-tests (dotnet, no Editor, no project lock) ──────────────────────────
# This is the loop AGENTS.md documents as authoritative for decode/geometry/earcut/assembler/
# projection-math work. It ran unwired for weeks and rotted silently, because nothing built it —
# this gate read fully green the whole time. Run it here, before the slow Unity launch, so a break
# fails in seconds instead of after a multi-minute batch import+compile, and so the gate can no
# longer go green while the fast project is broken.
#
# Warn-and-continue (not hard-fail) when `dotnet` is missing, so a maintainer without the .NET SDK
# can still run the Unity gate — but the skip must reach the final VERDICT line too, or a machine
# without dotnet reads a plain "compiled clean" with no sign the fast project was never built.
FAST_LOOP_NOTE=""
CORE_TESTS_LOG="$ROOT/Logs/core-tests.log"
if command -v dotnet >/dev/null 2>&1; then
  if ! dotnet test "$ROOT/Tools/core-tests" >"$CORE_TESTS_LOG" 2>&1; then
    echo "Tools/core-tests failed — last 40 lines of $CORE_TESTS_LOG:" >&2
    tail -n 40 "$CORE_TESTS_LOG" >&2
    echo "VERDICT: FAST LOOP FAILED — run 'dotnet test Tools/core-tests' to iterate; Unity was not launched." >&2
    exit 6
  fi
  # dotnet exits 0 on "no tests discovered" (a lost adapter reference, a bad runsettings, a stripped
  # <Compile> list) as readily as on a real pass — the same false-green this whole gate exists to
  # kill, one level up. Require the summary line to actually report a nonzero pass count.
  if ! grep -qE 'Passed:[[:space:]]*[1-9]' "$CORE_TESTS_LOG"; then
    tail -n 40 "$CORE_TESTS_LOG" >&2
    echo "VERDICT: FAST LOOP RAN NO TESTS — dotnet test exited 0 but discovered nothing. Treating as failure." >&2
    exit 6
  fi
else
  echo "dotnet not found on PATH — skipping the Tools/core-tests fast loop (Unity gate still runs)." >&2
  FAST_LOOP_NOTE=" (fast loop SKIPPED: dotnet not on PATH)"
fi

# Preserve the interactive Editor's open-scene selection across the batch run. Batch-mode Unity opens
# with no scene and rewrites Library/LastSceneManagerSetup.txt to `sceneSetups: []`, so the developer's
# NEXT interactive open lands on an empty scene and has to hunt for MapDemo again. The file is only read
# at Editor launch, so snapshotting it now and restoring it on exit (any exit — hence the trap) makes the
# batch run transparent to the open-scene state. No-op when Library is fresh (file absent).
SCENE_SETUP="$ROOT/Library/LastSceneManagerSetup.txt"
SCENE_SETUP_BAK="$ROOT/Logs/LastSceneManagerSetup.bak"
if [ -f "$SCENE_SETUP" ]; then cp -f "$SCENE_SETUP" "$SCENE_SETUP_BAK"; else rm -f "$SCENE_SETUP_BAK"; fi
restore_scene_setup() { [ -f "$SCENE_SETUP_BAK" ] && cp -f "$SCENE_SETUP_BAK" "$SCENE_SETUP"; }
trap restore_scene_setup EXIT

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
    | sed -E 's/.*fullname="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
fi

COMPILE_ERRORS="$(grep -E 'error CS' "$LOG" 2>/dev/null | sort -u)"
[ -n "$COMPILE_ERRORS" ] && printf '%s\n' "$COMPILE_ERRORS"

# ── Verdict — printed LAST so `| tail` always shows it, and independent of Unity's exit code ──
# Ordered by what makes the rest of the output meaningless: a compile failure means no tests ran,
# and a missing XML means nothing can be concluded at all.
if [ -n "$COMPILE_ERRORS" ]; then
  echo "VERDICT: COMPILE ERROR — no tests ran. (Unity's own exit was $CODE; it is not reliable here —" >&2
  echo "  0 and 1 have both been observed for the same kind of failure, which is why this greps the log.)$FAST_LOOP_NOTE" >&2
  exit 4
fi

if [ ! -f "$RESULTS" ]; then
  echo "VERDICT: NO RESULTS — Unity wrote no test-results.xml for this run (crash, or it died before" >&2
  echo "  writing). Nothing can be concluded; see $LOG. Previous run's results, if any: $RESULTS_PREV$FAST_LOOP_NOTE" >&2
  exit 5
fi

RUN_TAG="$(grep -oE '<test-run [^>]*' "$RESULTS" | head -1)"
run_attr() { printf '%s' "$RUN_TAG" | grep -oE "(^| )$1=\"[^\"]*\"" | head -1 | sed -E 's/.*="([^"]*)"/\1/'; }
RUN_RESULT="$(run_attr result)"
RUN_TOTAL="$(run_attr total)"
RUN_PASSED="$(run_attr passed)"
RUN_FAILED="$(run_attr failed)"

if [ "${RUN_FAILED:-0}" != "0" ] || [ "$RUN_RESULT" != "Passed" ]; then
  echo "VERDICT: TESTS FAILED — result=$RUN_RESULT total=$RUN_TOTAL passed=$RUN_PASSED failed=$RUN_FAILED$FAST_LOOP_NOTE" >&2
  exit 1
fi

# A filtered run legitimately matches nothing; an unfiltered one that ran zero tests is a broken setup
# dressed up as success, which is the same trap as the stale XML.
if [ -z "$FILTER" ] && [ "${RUN_TOTAL:-0}" = "0" ]; then
  echo "VERDICT: NO TESTS RAN — the results XML reports total=0 with no -testFilter. Treating as failure.$FAST_LOOP_NOTE" >&2
  exit 5
fi

if [ "$CODE" != "0" ]; then
  echo "VERDICT: all $RUN_TOTAL tests passed, but Unity exited $CODE — investigate $LOG.$FAST_LOOP_NOTE" >&2
  exit "$CODE"
fi

echo "VERDICT: PASS — $RUN_PASSED/$RUN_TOTAL tests passed, compiled clean, results written by this run.$FAST_LOOP_NOTE"
exit 0
