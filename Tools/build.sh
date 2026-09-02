#!/usr/bin/env bash
# Headless Unity player builds for unity-map-renderer, driven by the `unity` CLI. Self-locating: run
# from anywhere inside the repo. `Tools/build.sh --help` documents the interface; this header is the
# maintainer's note on the division of labour.
#
# The CLI locates the editor for this project's ProjectVersion.txt and spawns it with
# -batchmode -nographics -quit -projectPath, so this script no longer hunts for the editor binary per
# OS. What it still owns: the this-project's-Editor-is-open guard, the target keyword -> BuildScript
# method mapping, and reading the verdict out of the log rather than trusting an exit code.
#
# `Assets/Editor/BuildScript.cs` stays: `unity build` REQUIRES --execute-method ("Unity has no
# built-in command-line build"), so the C# entry points are not removable — only the editor-discovery
# and flag-assembly boilerplate that used to live here was.
set -uo pipefail

usage() {
  cat <<'EOF'
Headless Unity player builds for unity-map-renderer, driven by the `unity` CLI.

USAGE
  Tools/build.sh <target> [--dev] [extra `unity build` flags...]

TARGET — the PLATFORM only; --dev selects the variant
  android    Builds/Android/UnityMapRenderer.apk      debug-signed APK, sideload/preview
  macos      Builds/macOS/UnityMapRenderer.app        unsigned .app (see CAVEATS)
  linux      Builds/Linux/UnityMapRenderer.x86_64     + _Data folder
  web        Builds/Web/UnityMapRenderer/              open with Tools/serve-web.sh (see WEB below)
  release    android + macos + linux                  the public release set
  desktop    macos + linux
  all        android + macos + linux

OPTIONS
  --dev      Build the DEVELOPMENT player, into Builds/<platform>-Development/ — a separate
             directory, so it can never overwrite the release artifact. ENABLE_PROFILER is defined,
             so the MapRenderer.* Profiler counters exist and are readable outside the Editor, and
             the player auto-connects to the Profiler. IL2CPP/Release like the shipping build: a
             Mono build's, or an IL2CPP/Debug build's, managed timings would not describe what
             ships. Available on every target.
  -h, --help This text. For the underlying CLI's own flags, see `unity build --help`.

EXAMPLES
  Tools/build.sh macos --dev              # the profiling player
  Tools/build.sh desktop --dev            # macOS + Linux, both development
  Tools/build.sh macos --dev --allow-install

WEB
  The player CANNOT be opened from the filesystem or served by a plain static server: Unity compresses
  with Brotli and its loader has no fallback decoder, so the server must send Content-Encoding. Use
  `Tools/serve-web.sh` (defaults to this build, port 8080).

  Two settings that used to need a web-specific exception, and no longer do — both were fixed by the
  Unity 6.6 upgrade, so web now builds on the same settings as every other target:
    Burst AOT on         the only route to a worker thread on web. Off through Unity 6000.5 / Burst
                         1.8.29, where entities + Burst trapped during static init; fixed in 6000.6 /
                         Burst 2.0. `UMR_WEB_BURST=off Tools/build.sh web` builds the other way.
    stripping High       same as every other target. It hung the player at 100% on Unity 6000.5, which
                         is why web was pinned to Minimal until 6.6; keeping Minimal cost 3.1 MB.

  Every web build prints a `burst:` line reporting what Burst was asked for against what it actually
  produced. generated-artifacts=0 on a requested=True build means the toggle never took — so nothing
  that player does is evidence about Burst, whatever the build said.

  Full reasoning, the measured table of configurations that do and do not start, and how to verify a
  build is genuinely what it claims: docs/web-target.md.

PASSTHROUGH
  Anything other than --dev is forwarded verbatim to every `unity build` invocation. These CLI flags
  are accepted there but do NOTHING here, silently — BuildScript.cs owns those decisions:
    --output-path          BuildScript composes Builds/<platform>/<name> itself (the Tools > Build
                           menu items need those paths too, so the shell can't be their source).
    --build-target-group   each BuildScript method hardcodes its BuildTargetGroup.
    --android-export-type  BuildAndroid() sets buildAppBundle = false; `aab` still yields an APK.
    --android-keystore-* / --android-symbol-type / --android-target-sdk-version
                           nothing in BuildScript reads them.
    --versioning-strategy / --build-version
                           unverified mechanism; if it stamps ProjectSettings that is a committed
                           file. Left at the CLI default (`none`) deliberately.

  Every build passes --allow-dirty-build: this repo is built from a working tree constantly, so the
  CLI's uncommitted-changes guard would block routine dev builds. A release artifact's provenance is
  therefore the human's check, not this script's.

SCENES
  Taken from Build Settings (EditorBuildSettings). For a PUBLIC build, enable the
  OpenStreetMapLiberty scene and DISABLE the internal MapDemo scene first — otherwise the build ships
  the dev scene with no on-screen attribution. BuildScript logs the scene list it builds.

CAVEATS
  - The macOS .app is UNSIGNED: on another Mac, right-click > Open or `xattr -cr <app>`. A real fix
    needs an Apple Developer cert + notarization. Neither blocks building.
  - An Android APK with an empty keystore is DEBUG-signed: fine to sideload, not publishable.
  - The first Android build is slow (build-target switch = full reimport + IL2CPP/NDK compile).
  - The CLI prints a `[Experiment] Fetch failed: ... ECONNREFUSED` stack trace when it cannot reach
    the network. That is benign telemetry noise, not a build error — read the BUILD OK / BUILD FAIL
    line for the verdict.

EXIT CODES
  0  every requested build succeeded
  2  setup error (not a repo / unknown target / no `unity` CLI)
  3  this project's Unity Editor is open (the project is locked)
  N  that many builds failed
EOF
}

# --help must work from anywhere, including outside the repo and without the CLI installed, so it is
# answered before any environment check. It also wins over passthrough: `build.sh macos --help` asks
# about THIS script, not `unity build`.
for arg in "$@"; do
  case "$arg" in -h|--help) usage; exit 0 ;; esac
done

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repo" >&2; exit 2; }
TARGET="${1:-}"

if [ -z "$TARGET" ]; then
  # Brief on a mistake, full on request: an argument slip should not cost a screenful.
  echo "usage: Tools/build.sh <android|macos|linux|web|release|desktop|all> [--dev] [unity build flags...]" >&2
  echo "  --dev = the profileable DEVELOPMENT player, into Builds/<platform>-Development/" >&2
  echo "  run 'Tools/build.sh --help' for the full interface" >&2
  exit 2
fi
shift

# --dev is OURS (it picks the BuildScript entry point); everything else is `unity build` passthrough.
DEV=0
ARGS=()
for arg in "$@"; do
  case "$arg" in
    --dev) DEV=1 ;;
    *) ARGS+=("$arg") ;;
  esac
done
set -- ${ARGS+"${ARGS[@]}"}   # ${ARGS+...} so an empty array is safe under `set -u`

# The CLI's own install dir is not on PATH by default.
command -v unity >/dev/null 2>&1 || export PATH="$HOME/.unity/bin:$PATH"
if ! command -v unity >/dev/null 2>&1; then
  echo "the \`unity\` CLI was not found on PATH (nor in ~/.unity/bin)." >&2
  echo "This script builds through it; install it, or add its bin dir to PATH." >&2
  exit 2
fi

# Batch mode can't share the project with an open Editor — but only THIS project's Editor matters.
# (Verbatim from run-tests.sh: exact -projectPath equality, stale-lock clearing.)
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
  web)     PLATFORMS=(web) ;;
  release) PLATFORMS=(android macos linux) ;;
  desktop) PLATFORMS=(macos linux) ;;
  all)     PLATFORMS=(android macos linux) ;;
  *) echo "unknown target '$TARGET' — want android|macos|linux|web|release|desktop|all" >&2
     echo "(run 'Tools/build.sh --help' for the full interface)" >&2; exit 2 ;;
esac

# platform -> `unity build --target` value. These are BuildTarget ENUM names: the CLI validates the
# argument against that list and rejects Unity's own -buildTarget mnemonics (`OSXUniversal` ⇒
# "Invalid build target"). It then forwards the name verbatim as -buildTarget.
btarget_for() { case "$1" in
    android) echo Android ;;
    macos)   echo StandaloneOSX ;;
    linux)   echo StandaloneLinux64 ;;
    web)     echo WebGL ;;
  esac; }
# platform -> BuildScript entry point. --dev appends "Development" to the method name, which is the
# whole mechanism: BuildScript's method name alone decides platform, variant and output path, so there
# is no side-channel argument for a Unity-side flag parser to disagree with.
method_for()  { case "$1" in
    android) echo MapRenderer.Build.BuildScript.BuildAndroid ;;
    macos)   echo MapRenderer.Build.BuildScript.BuildMacOS ;;
    linux)   echo MapRenderer.Build.BuildScript.BuildLinux ;;
    web)     echo MapRenderer.Build.BuildScript.BuildWeb ;;
  esac; }

# Report whether the web player actually got Burst-compiled. BuildScript prints the setting it
# REQUESTED, which is not the same claim: Unity has been observed skipping Burst's build callback
# entirely and producing a Burst-less player from a Burst-on request. A requested=True/generated=0
# line is the signal to move Library/Bee aside and build again; a result read off a player in that
# state means nothing.
report_web_burst() {
  local log="$1" requested generated
  requested="$(grep -aoE 'EnableBurstCompilation=(True|False)' "$log" 2>/dev/null | tail -1 | cut -d= -f2)"
  # lib_burst_generated.wasm under Library/Bee, and NOT a bcl.exe invocation in the log: Burst 2.0
  # compiles in-process, so the bcl count this used to print is now a permanent 0 that reads as failure.
  generated=$(find "$ROOT/Library/Bee" -iname '*burst_generated*' 2>/dev/null | wc -l | tr -d ' ')
  echo "    burst: requested=${requested:-unknown} generated-artifacts=$generated" >&2
  if [ "$requested" = True ] && [ "$generated" -eq 0 ]; then
    echo "    WARNING: Burst was requested but produced nothing — this player is NOT Burst-compiled." >&2
    echo "             Delete Library/Bee and rebuild before reading anything into how it behaves." >&2
  fi
}

build_one() {
  local name="$1"
  shift  # drop the platform name; what remains in "$@" is the caller's `unity build` passthrough
  local btarget method log code variant
  btarget="$(btarget_for "$name")"
  method="$(method_for "$name")"
  # Release and development builds keep separate logs, mirroring their separate output dirs — reading
  # a release verdict out of a development run's log would be the same lie the -prev rotation prevents.
  variant=""
  if [ "$DEV" -eq 1 ]; then method="${method}Development"; variant="-dev"; fi
  log="$ROOT/Logs/build-$name$variant.log"

  # Move the previous log aside FIRST. If the editor never launches, Unity does not touch this path,
  # and the last run's `BUILD OK` would sit there reading as current (same hazard run-tests.sh
  # guards for with test-results.prev.xml). Its ABSENCE below is what proves nothing was produced.
  [ -f "$log" ] && mv -f "$log" "$ROOT/Logs/build-$name$variant-prev.log"

  echo ">>> Building $name${variant:+ (development)} (--target $btarget) ... log: $log" >&2
  # The CLI's own stdout/stderr is deliberately NOT swallowed: its failures (unknown target, editor
  # not installed, dirty-tree guard) are reported there and never reach --log-file.
  unity build "$ROOT" \
    --target "$btarget" \
    --execute-method "$method" \
    --log-file "$log" \
    --allow-dirty-build \
    --no-tail --no-banner --non-interactive \
    "$@"
  code=$?

  # BuildScript prints the sentinel; surface it plus any compile errors.
  grep -aE 'BUILD OK|BUILD FAIL' "$log" 2>/dev/null | tail -3
  grep -aE 'error CS' "$log" 2>/dev/null | sort -u | head

  # Success needs BOTH: a clean exit AND this run's log saying so. Unity's exit code alone has been
  # observed lying in this project (see run-tests.sh), and a missing log means no build happened.
  if [ "$code" -ne 0 ] || ! grep -qa 'BUILD OK' "$log" 2>/dev/null; then
    echo "$name FAILED (exit $code) — see $log" >&2
    return 1
  fi
  [ "$name" = web ] && report_web_burst "$log"
  echo "$name ok" >&2
  return 0
}

FAILED=0
for p in "${PLATFORMS[@]}"; do
  build_one "$p" "$@" || FAILED=$((FAILED + 1))
done

if [ "$FAILED" -eq 0 ]; then
  echo "All requested builds succeeded." >&2
  exit 0
fi
echo "$FAILED build(s) failed." >&2
exit "$FAILED"
