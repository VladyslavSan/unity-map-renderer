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

### Driving the parity loop — read `docs/loop-operations.md` first
If you are launching/monitoring `.claude/workflows/parity-loop.js` runs (the autonomous
manager→planner→developer↔reviewer loop), the operator playbook is **`docs/loop-operations.md`** — read it.
The rules that each cost a multi-hour failure to learn: **one run at a time** (stop prior runs); the **Unity
Editor must be closed** (else every headless validation silently exits 3); **monitor every run** with the
stall+bloat watchdog and **advance only on workflow-completion**, not on a HEAD-move; **when you hand-commit a
stage, mark it `done` in `docs/feature-parity.md`** or the manager re-runs it as an unmet dependency; commit
your own infra/backlog edits as a separate `chore` so they don't ride into the stage commit. Model policy is
sonnet-first, opus only on genuine failure. (In-loop agent lessons live in `docs/lessons.md`.)

### Run tests yourself — don't ask the user to click in the Editor
Unity's Test Framework runs headless from the CLI in batch mode. Prefer this for every logic change;
it verifies **compilation and tests** without the GUI. Only fall back to asking the user to use the
in-Editor Test Runner if headless licensing is unavailable (see caveats).

**Recipe** — use the committed wrapper script (canonical command; run it from the repo root):
```bash
./Tools/run-tests.sh            # EditMode (default)
./Tools/run-tests.sh PlayMode   # PlayMode
```
It is self-locating, finds the Editor binary for this project's Unity version, refuses to run if the
Editor is open (exit 3), runs the tests, then prints the result summary and any `error CS` lines. Exit
`0` = compiled AND all tests passed; non-zero = compile error or test failure (don't trust the code
alone — read the printed summary). **Run it in the background** (`run_in_background`): the first batch
launch does a full asset import + compile and can take minutes; you'll be notified on completion.

> The script is allowlisted in `.claude/settings.json` (`Bash(./Tools/run-tests.sh:*)`) so it runs
> without a permission prompt. What it does, expanded, in case you need to invoke a step by hand:
> ```bash
> ROOT="$(git rev-parse --show-toplevel)"
> VERSION="$(awk '/^m_EditorVersion:/ {print $2}' "$ROOT/ProjectSettings/ProjectVersion.txt")"
> # Editor binary — Unity Hub default install locations (macOS):
> UNITY="/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
> [ -x "$UNITY" ] || UNITY="$HOME/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
> # (Linux: .../Editor/$VERSION/Editor/Unity ; Windows: .../Editor/$VERSION/Editor/Unity.exe)
> mkdir -p "$ROOT/Logs"
> "$UNITY" -runTests -batchmode -projectPath "$ROOT" -testPlatform EditMode \
>   -testResults "$ROOT/Logs/test-results.xml" -logFile "$ROOT/Logs/test-run.log"
> # results (don't trust exit code alone):
> grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$ROOT/Logs/test-results.xml" | head -1
> grep -oE '<test-case [^>]*' "$ROOT/Logs/test-results.xml" \
>   | sed -E 's/.*name="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
> grep -E 'error CS' "$ROOT/Logs/test-run.log" | sort -u   # compile errors prevent tests running at all
> ```

### Fast Core tests (no Unity) — prefer these for `MapRenderer.Core` logic
`MapRenderer.Core` is plain C# (its only Unity dependency is `Unity.Mathematics.double2`, which a 2-field
shim replaces). `Tools/core-tests/` is a `dotnet test` project that compiles the **real** Core `.cs` files
plus the **engine-free EditMode test files verbatim** (single source of truth — they run in both runners).
It executes in **~0.1s, with no Editor and no project lock** (works while Unity is open).
```bash
dotnet test "$(git rev-parse --show-toplevel)/Tools/core-tests"
```
- Use this as the **default** loop for decode / geometry / earcut / assembler / projection-math changes —
  it's seconds, not minutes, and won't trip long-run watchdogs.
- It can also iterate root-cause experiments (it's how the disjoint-hole assembler bug was found): edit
  `Tools/core-tests/CorePipelineTests.cs` to probe the real pipeline over the fixture.
- **Still run the Unity EditMode recipe (above) before declaring a stage done** — it's the source of truth
  for engine-integration tests that the fast project can't cover: `MeshBuilder`/`UnityEngine.Mesh`,
  `NativeArray`/Burst jobs, MonoBehaviours, and Burst-compilation correctness.

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
