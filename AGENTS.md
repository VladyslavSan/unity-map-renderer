# unity-map-renderer — working notes for AI agents

Unity-native (DOTS/ECS, C#) MapLibre-style vector map renderer. Read `ARCHITECTURE.md` for the design
and decisions, `docs/coordinates-and-projections.md` for the math foundations, `docs/meshing-design.md`
§1 for how MVT bytes become a mesh (the per-kind fill/line stage orderings + the build/consume tile loop;
line AA, lit shading, and the render-layer model are the later sections), `docs/conventions.md` for
generic coding conventions, `docs/gc-and-allocation-design.md` for why managed allocation dominates
frame timing (GC is stop-the-world) and how the hot paths avoid it, `docs/step-0.md` for the current
milestone, and
**`docs/lessons-learned.md` for hard-won engineering gotchas** (Unity/URP/HLSL + the headless test
workflow) — check it before debugging a shader/material/test-harness surprise.
Proprietary / all rights reserved.

## Project layout

**The product is `MapRenderer.Unity` + `MapRenderer.Jobs`; `MapRenderer.Core` is a convenience, not a goal —
and never a placement argument.** Put code where it belongs architecturally, then test it wherever it lands.
**Read `ARCHITECTURE.md` §2 "Module boundaries" before moving code between assemblies or adding a type to
Core** — it carries the rule, the rationale, and the three-workaround failure that produced it.

- `Assets/Code/MapRenderer.Unity/` — **the product**: MonoBehaviours, mesh building, rendering glue.
- `Assets/Code/MapRenderer.Jobs/` — **the product**: Burst + Collections jobs; anything naturally blittable.
- `Assets/Code/MapRenderer.Core/` — naturally engine-free code: tile math, geometry, earcut, style/expression
  evaluation, text shaping.
- `Assets/Code/MapRenderer.Tests.EditMode/` — headless EditMode tests.
- `Assets/Fixtures/` — committed test data (e.g. a sample MVT tile).
- Assemblies are split via `.asmdef`; Core does not depend on `MapRenderer.Unity`.

## Way of working

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
Editor is open (exit 3), runs the tests, then prints the per-test results, any `error CS` lines, and a
**`VERDICT:` line last** — read that. **Run it in the background** (`run_in_background`): the first
batch launch does a full asset import + compile and can take minutes; you'll be notified on completion.

The script's own exit code IS trustworthy — but only because it ignores Unity's. Unity has been observed
returning both `0` and `1` for the same compile failure, and it does **not** rewrite
`Logs/test-results.xml` when compilation fails, so the previous run's green summary sits there looking
current. The script therefore moves any existing results to `Logs/test-results.prev.xml` before
launching (so the file existing proves *this* run wrote it), greps the log for `error CS`, and reads
`failed=`/`result=` out of the XML rather than trusting the process code:

| exit | meaning |
|---|---|
| `0` | compiled, results written by this run, every test passed |
| `1` | tests ran and something failed (or the run result isn't `Passed`) |
| `2` | setup error (not a repo / no editor binary) |
| `3` | this project's Editor is open — close it |
| `4` | compilation failed; **no tests ran** |
| `5` | no results produced for this run (crash), or an unfiltered run matched zero tests |

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
> # Move any existing results aside FIRST — Unity leaves them untouched when compilation fails, so
> # without this you cannot tell this run's results from the last one's.
> [ -f "$ROOT/Logs/test-results.xml" ] && mv -f "$ROOT/Logs/test-results.xml" "$ROOT/Logs/test-results.prev.xml"
> "$UNITY" -runTests -batchmode -projectPath "$ROOT" -testPlatform EditMode \
>   -testResults "$ROOT/Logs/test-results.xml" -logFile "$ROOT/Logs/test-run.log"
> # Compile errors FIRST — they mean no tests ran, whatever the exit code says:
> grep -E 'error CS' "$ROOT/Logs/test-run.log" | sort -u
> # Then the results. An ABSENT file here means this run produced none — never read the .prev one as current.
> grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$ROOT/Logs/test-results.xml" | head -1
# Capture `fullname`, NOT `name`: sed is greedy, so `.*name="` runs forward to the LAST attribute
># ending in `name=` before `result=` — which is `classname`. Using `name` prints FIXTURE names and
># silently hides which TEST failed (see `red-verify-counts-must-reconcile-with-names`).
> grep -oE '<test-case [^>]*' "$ROOT/Logs/test-results.xml" \
>   | sed -E 's/.*fullname="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
> ```

### Fast Core tests (no Unity) — a faster loop for code that *already* lives in `MapRenderer.Core`
> A convenience, **never** a placement argument — see `ARCHITECTURE.md` §2 "Module boundaries".

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
- **Placement is architectural, not test-driven** — `ARCHITECTURE.md` §2 "Module boundaries" is the rule.
- Logic that can be unit-tested **must** have EditMode tests wherever it lives; validate via the recipe above
  before declaring done.
- Engine-only behavior (mesh build, rendering, camera) lives in `MapRenderer.Unity`; verify it visually
  in the Editor (a step the user runs).
- Vendored third-party code goes under `Assets/Code/ThirdParty/<name>/` with its license, and an entry in
  `THIRD-PARTY-NOTICES.txt`. Avoid copyleft (see `ARCHITECTURE.md` §4).

The code-style rules (math types, `System.Math` ban, `in` params, data carriers, builder naming,
test-code-bloat) live in **Coding conventions** below — don't restate them here.

### Multi-agent stage workflow (plan → develop ⇄ review)
A larger change lands one **stage** at a time via a three-role chain, each role a separate agent (models
chosen for the job — a strong reasoning model plans, a fast capable model develops, a strong model reviews).
Each role is a **tool-scoped subagent**: the planner reads and plans but writes no production code; the
developer implements and runs the gate; the reviewer reads the diff and re-runs the gate but edits nothing.
This is a lightweight, human-in-the-loop chain — there is **no board, no stage files, no autonomous
supervisor**; the design docs below are the only tracking.
1. **Plan.** A planner writes a **file-level implementation plan** grounded in the epic's SSOT design doc —
   an *ordered edit list* citing `file:symbol` (re-verified against source), with the stated **invariant**
   (e.g. "behaviour-preserving ⇒ byte-identical snapshots"), **acceptance teeth** (falsifiable — a shallow
   impl can't pass), and an **explicit "deferred" scope fence** so the developer can't over-reach. The plan
   is transient build scaffolding, not a repo doc — `docs/` holds only durable design. No production code.
2. **Develop.** A developer executes the plan in order, honoring its compile checkpoints, and iterates
   `./Tools/run-tests.sh` to green (Editor **closed**; verify NEW test names appear in the results XML — the
   batch-Burst stale-XML hazard; **never re-bake a snapshot to go green** — a diff means behaviour changed).
   Regression tests for a fixed bug are **RED-verified** (confirm they fail against the un-fixed code, then
   pass). Does **not** commit.
3. **Review.** A reviewer reads the working-tree diff, **independently re-runs the gate** (doesn't trust the
   dev's word), and returns APPROVE or ranked actionable findings — stating an explicit verdict on each
   judgment call the change hinges on. Non-blocking findings are **recorded** (in the design doc, for the
   merge step), not necessarily fixed in-stage.
Then **commit one stage per revertible commit** on the feature branch (never fold stages; the merge step
decides what to squash). The design doc is the running SSOT — decisions, the stage sequence, and open
findings live there. The orchestrator (main session) hands each role its brief, relays results, and gates
the commit; it does not do the role work itself.

**Two rules every role brief must carry** — a subagent only knows what its brief points it at, and both of
these were learned the expensive way:

- **Clean-room: implement from the open specs and from black-box observation of rendered output. Do NOT
  read, cite, quote, or paraphrase another renderer's source** — not to derive a design, and not to
  *confirm* one you already derived. `ARCHITECTURE.md` § "Clean-room hygiene" is the rule; the design docs
  state it as a fact about this repo ("no MapLibre source has been read"), which reads as provenance rather
  than instruction, so a role that has only the design docs will not see a prohibition. A planner once
  settled a decision by citing MapLibre's `symbol_layout.ts` — including a verbatim source comment — and
  instructed the developer to paste that comment into `docs/`. The decision was independently derivable and
  survived unchanged; the citation was pure contamination risk. Caught before any developer ran, but the
  failure is asymmetric: a bad design is reverted, foreign source text committed to a proprietary repo is
  not.
- **Escalation beats invention. If a decision genuinely needs the maintainer, STOP, say so, and write down
  the options with their consequences.** A role has no channel to the human, so "decide it or leave it
  open" is a false choice — and a brief that forbids leaving it open (rightly, per
  `refine-lock-core-design-questions`) has removed the only safe exit unless it supplies this one. Halting
  with a stated fork is a successful outcome, not a failure to deliver.

### Commit conventions — read `docs/commit-conventions.md`
`type(scope): subject` ([Conventional Commits](https://www.conventionalcommits.org)). **The scope is a
code-area tag, never a stage id** — `feat(meshing): …`, not `feat(S89 D2): …` (a scope must be
legible without looking up a stage). Types: `feat` / `fix` / `refactor` / `perf` / `test` / `docs` /
`chore`. Scope vocabulary (pick from — don't invent ad-hoc): `meshing`, `tile-pipeline`,
`render-layers`, `style`, `decode`, `backends`, `camera`, `shaders`, `projection`, `build`, `backlog`,
`docs`.
Subject: imperative, lower-case, no period. A stage-linked commit records it in a **body trailer**
(`Stage: S89 (render-layer unification)`), not the subject — above the mandated identity/session trailers.

## Coding conventions

The short summary index is imported below; each entry links to its full section in `docs/conventions.md`
(the canonical human-facing reference — read it when a rule is ambiguous or you need the *why*).

@docs/conventions-short.md
