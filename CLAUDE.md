# unity-map-renderer — working notes for AI agents

Unity-native (DOTS/ECS, C#) MapLibre-style vector map renderer. Read `ARCHITECTURE.md` for the design
and decisions, `docs/coordinates-and-projections.md` for the math foundations, and `docs/step-0.md` for
the current milestone. Proprietary / all rights reserved.

## Project layout
- `Assets/MapRenderer.Core/` — managed library: MVT decode, Web-Mercator/tile math, geometry, earcut.
- `Assets/MapRenderer.Jobs/` — Burst + Collections jobs (coordinate transforms, etc.).
- `Assets/MapRenderer.Unity/` — MonoBehaviours, mesh building, rendering glue.
- `Assets/MapRenderer.Tests.EditMode/` — headless EditMode tests.
- `Assets/Fixtures/` — committed test data (e.g. a sample MVT tile).
- Assemblies are split via `.asmdef`; keep Core free of `MapRenderer.Unity` dependencies.

## Way of working

### Run tests yourself — don't ask the user to click in the Editor
Unity's Test Framework runs headless from the CLI in batch mode. Prefer this for every logic change;
it verifies **compilation and tests** without the GUI. Only fall back to asking the user to use the
in-Editor Test Runner if headless licensing is unavailable (see caveats).

**Recipe** (self-discovering; run from anywhere inside the repo):
```bash
ROOT="$(git rev-parse --show-toplevel)"
VERSION="$(awk '/^m_EditorVersion:/ {print $2}' "$ROOT/ProjectSettings/ProjectVersion.txt")"

# Editor binary — Unity Hub default install locations (macOS):
UNITY="/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
[ -x "$UNITY" ] || UNITY="$HOME/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
# (Linux: .../Editor/$VERSION/Editor/Unity ; Windows: .../Editor/$VERSION/Editor/Unity.exe)

mkdir -p "$ROOT/Logs"
"$UNITY" -runTests -batchmode -projectPath "$ROOT" \
  -testPlatform EditMode \
  -testResults "$ROOT/Logs/test-results.xml" \
  -logFile "$ROOT/Logs/test-run.log"
echo "exit: $?"   # 0 = compiled AND all tests passed; non-zero = compile error or test failure
```
- Use `-testPlatform PlayMode` for play-mode tests.
- **Run it in the background** (`run_in_background`): the first batch launch does a full asset import +
  compile and can take minutes. You'll be notified on completion.

**Read the results** (don't trust the exit code alone):
```bash
ROOT="$(git rev-parse --show-toplevel)"
# overall + per-test:
grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$ROOT/Logs/test-results.xml" | head -1
grep -oE '<test-case [^>]*' "$ROOT/Logs/test-results.xml" \
  | sed -E 's/.*name="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
# compile errors (these prevent tests from running at all):
grep -E 'error CS' "$ROOT/Logs/test-run.log" | sort -u
```

### Unity CLI caveats
- **The Editor must be closed.** Unity locks the project; batch mode can't run alongside an open Editor.
  Check before running: `[ -e "$ROOT/Temp/UnityLockfile" ]` (present ⇒ likely open), or `pgrep -x Unity`.
  If it's open, ask the user to quit Unity (Cmd+Q) — this is the one step you can't do for them.
- **Licensing.** Batch mode needs an activated license; the user should have opened the Editor via Unity
  Hub at least once so a license is cached. `Licensing::Module` handshake warnings in the log are
  usually non-fatal if a license is cached (compilation/tests still run).
- **`.meta` files.** Unity generates them on import — never hand-author them. New `.cs`/`.asmdef` files
  are picked up on the next Editor open or batch run.
- **`Logs/` is git-ignored** — safe to write test output there.

### Editing conventions
- Logic that can be unit-tested (decode, math, geometry, earcut) lives in `Core` and **must** have
  EditMode tests; validate via the recipe above before declaring done.
- Engine-only behavior (mesh build, rendering, camera) lives in `MapRenderer.Unity`; verify it visually
  in the Editor (a step the user runs).
- Vendored third-party code goes under `Assets/ThirdParty/<name>/` with its license, and an entry in
  `THIRD-PARTY-NOTICES.txt`. Avoid copyleft (see `ARCHITECTURE.md` §4).
