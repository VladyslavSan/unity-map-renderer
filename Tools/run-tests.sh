#!/usr/bin/env bash
# Headless Unity test runner for unity-map-renderer.
# Self-locating: run from anywhere inside the repo.
#
#   Tools/run-tests.sh [Both|EditMode|PlayMode] [testFilter]   (default: Both, no filter)
#
# Both — the DEFAULT, and the gate a stage is declared done against — runs EditMode and then
# PlayMode, one Unity at a time (the project lock is exclusive), and stops at the first platform
# that fails. It is the default because the other shape of this (a documented rule saying "also
# pass PlayMode") is a sentence a session can skip, and did: PlayMode went unrun by the workflow
# and main sat red there for eight days (UMR-164). Iterate with an explicit `EditMode` when you
# want the faster half; the unqualified command stays the whole gate.
#
# testFilter (optional) is passed straight to Unity's -testFilter (a regex over test
# full names), e.g. 'MapRenderer.Tests.Visual' runs just the snapshot suites. Startup
# (asset import + domain reload) still dominates; the filter only trims which tests run.
#
# Before the Unity launch, this also runs the fast `Tools/core-tests` (dotnet, no Editor, no project
# lock) loop — see the "Fast loop" block below for why: it is part of the gate, not a separate
# convenience script, and this is what makes that true.
#
# Exit codes (in Both mode: the code of the first platform that failed, else 0):
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
# Each platform prints its own `VERDICT [<platform>]:` line, and Both mode prints a combined
# `VERDICT:` line after them — so "read the last VERDICT line" stays the whole answer.
#
# A *stale* lockfile (present but no Unity process — e.g. a prior batch run was killed)
# is cleared automatically; only a live process makes this refuse.
set -uo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repo" >&2; exit 2; }
. "$ROOT/Tools/lib.sh"   # this_project_editor_open, shared with build.sh
VERSION="$(awk '/^m_EditorVersion:/ {print $2}' "$ROOT/ProjectSettings/ProjectVersion.txt" 2>/dev/null)"
FILTER="${2:-}"
case "${1:-Both}" in
  EditMode|PlayMode) PLATFORMS="${1}" ;;
  Both)              PLATFORMS="EditMode PlayMode" ;;
  *) echo "unknown test platform '${1}' — expected Both, EditMode or PlayMode" >&2; exit 2 ;;
esac

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
# this_project_editor_open lives in Tools/lib.sh — build.sh needs the identical check, and the two
# copies were previously kept in sync by hand.
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

run_unity() { # $1 = platform, $2 = testResults path, $3 = logFile path
  "$UNITY" -runTests -batchmode -projectPath "$ROOT" \
    -testPlatform "$1" \
    ${FILTER:+-testFilter "$FILTER"} \
    -testResults "$2" \
    -logFile "$3"
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
# Per-filter stamp. A filtered run warms only its own subset, so it may not claim the FULL set is
# warm — but it may claim ITS OWN subset is, which is what lets repeated filtered iteration skip the
# warm-up instead of paying it forever. Unfiltered runs keep the unsuffixed stamp.
WARM_STAMP="$ROOT/Library/.umr-shader-warm-stamp${FILTER:+-$(printf '%s' "$FILTER" | cksum | cut -d' ' -f1)}"
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
  # Once per invocation, not per platform: Library/ShaderCache is shared, so warming under the first
  # platform leaves the second one warm too.
  run_unity "${PLATFORMS%% *}" "$ROOT/Logs/test-results.warmup.xml" "$ROOT/Logs/test-run.warmup.log"
  echo "Warm-up pass complete (shader variants cached) — running the real test pass." >&2
fi

# ── One platform: run it, print its results, return ITS exit code ─────────────────────────────
# Same contract as the whole script had when it ran one platform — the codes below are the
# documented ones. Both mode calls this twice and stops at the first nonzero.
run_platform() { # $1 = EditMode|PlayMode
  local platform="$1"

  # The staleness defence again, per platform: the FIRST platform's results must not be readable as
  # the second's when the second fails to compile (the header's hazard, one level up). The
  # per-platform copies are removed for the same reason — this platform's copy existing afterwards
  # must prove THIS run wrote it, not that some earlier invocation did.
  [ -f "$RESULTS" ] && mv -f "$RESULTS" "$RESULTS_PREV"
  rm -f "$ROOT/Logs/test-results-$platform.xml" "$ROOT/Logs/test-run-$platform.log"

  run_unity "$platform" "$RESULTS" "$LOG"
  local code=$?

  # Keep a per-platform copy: in Both mode the PlayMode run overwrites $RESULTS, and "verify the new
  # test names appear in the XML" must stay answerable for BOTH runs after the script exits.
  [ -f "$RESULTS" ] && cp -f "$RESULTS" "$ROOT/Logs/test-results-$platform.xml"
  [ -f "$LOG" ]     && cp -f "$LOG"     "$ROOT/Logs/test-run-$platform.log"

  echo "exit: $code  ($platform)"
  if [ -f "$RESULTS" ]; then
    grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$RESULTS" | head -1
    grep -oE '<test-case [^>]*' "$RESULTS" \
      | sed -E 's/.*fullname="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
  fi

  local compile_errors
  compile_errors="$(grep -E 'error CS' "$LOG" 2>/dev/null | sort -u)"
  [ -n "$compile_errors" ] && printf '%s\n' "$compile_errors"

  # ── Verdict — printed LAST so `| tail` always shows it, and independent of Unity's exit code ──
  # Ordered by what makes the rest of the output meaningless: a compile failure means no tests ran,
  # and a missing XML means nothing can be concluded at all.
  if [ -n "$compile_errors" ]; then
    echo "VERDICT [$platform]: COMPILE ERROR — no tests ran. (Unity's own exit was $code; it is not" >&2
    echo "  reliable here — 0 and 1 have both been observed for the same kind of failure, which is why" >&2
    echo "  this greps the log.)$FAST_LOOP_NOTE" >&2
    return 4
  fi

  if [ ! -f "$RESULTS" ]; then
    echo "VERDICT [$platform]: NO RESULTS — Unity wrote no test-results.xml for this run (crash, or it" >&2
    echo "  died before writing). Nothing can be concluded; see $LOG. Previous results, if any:" >&2
    echo "  $RESULTS_PREV$FAST_LOOP_NOTE" >&2
    return 5
  fi

  local run_tag run_result run_total run_passed run_failed
  run_tag="$(grep -oE '<test-run [^>]*' "$RESULTS" | head -1)"
  run_attr() { printf '%s' "$run_tag" | grep -oE "(^| )$1=\"[^\"]*\"" | head -1 | sed -E 's/.*="([^"]*)"/\1/'; }
  run_result="$(run_attr result)"
  run_total="$(run_attr total)"
  run_passed="$(run_attr passed)"
  run_failed="$(run_attr failed)"

  if [ "${run_failed:-0}" != "0" ] || [ "$run_result" != "Passed" ]; then
    echo "VERDICT [$platform]: TESTS FAILED — result=$run_result total=$run_total passed=$run_passed failed=$run_failed$FAST_LOOP_NOTE" >&2
    return 1
  fi

  # A filtered run legitimately matches nothing; an unfiltered one that ran zero tests is a broken setup
  # dressed up as success, which is the same trap as the stale XML.
  if [ -z "$FILTER" ] && [ "${run_total:-0}" = "0" ]; then
    echo "VERDICT [$platform]: NO TESTS RAN — the results XML reports total=0 with no -testFilter. Treating as failure.$FAST_LOOP_NOTE" >&2
    return 5
  fi

  if [ "$code" != "0" ]; then
    echo "VERDICT [$platform]: all $run_total tests passed, but Unity exited $code — investigate $LOG.$FAST_LOOP_NOTE" >&2
    return "$code"
  fi

  echo "VERDICT [$platform]: PASS — $run_passed/$run_total tests passed, compiled clean, results written by this run.$FAST_LOOP_NOTE"
  return 0
}

# ── Drive the platforms, one Unity at a time (the project lock is exclusive) ──────────────────
# Stop at the first failure: a red EditMode cannot be "done" either way, and stopping keeps the
# exit code naming exactly one platform.
RAN=""
for platform in $PLATFORMS; do
  run_platform "$platform"
  CODE=$?
  RAN="${RAN:+$RAN, }$platform"
  [ "$CODE" != "0" ] && break
done

# Stamp the cache as warm-for-the-current-shaders, so the next run skips the warm-up unless a shader
# changes. The stamp is per-filter (see WARM_STAMP above): an unfiltered run claims the whole set,
# a filtered run claims only its own subset. Before this was per-filter, a filtered run never
# stamped at all, so every filtered iteration after a shader edit paid a full warm-up forever —
# which made the fast path slower than the slow one.
if [ -d "$SHADER_CACHE" ] && [ -n "$(ls -A "$SHADER_CACHE" 2>/dev/null)" ]; then
  touch "$WARM_STAMP"
fi

# In Both mode the per-platform verdicts are no longer the last line, so restate the combined one.
if [ "$PLATFORMS" != "${PLATFORMS% *}" ]; then
  if [ "$CODE" = "0" ]; then
    echo "VERDICT: PASS — both runners green ($RAN)."
  else
    echo "VERDICT: FAILED — ran $RAN; the last one failed (exit $CODE). See its VERDICT line above." >&2
  fi
fi
exit "$CODE"
