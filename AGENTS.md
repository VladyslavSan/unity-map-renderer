# unity-map-renderer — working notes for AI agents

Unity-native (DOTS/ECS, C#) MapLibre-style vector map renderer. Read `ARCHITECTURE.md` for the design
and decisions, `docs/coordinates-and-projections.md` for the math foundations, `docs/meshing-design.md`
§ "Mesh pipeline" for how MVT bytes become a mesh (the per-kind fill/line stage orderings + the build/consume tile loop;
line AA, lit shading, and the render-layer model are the later sections), `docs/conventions.md` for
generic coding conventions, `docs/gc-and-allocation-design.md` for why managed allocation dominates
frame timing (GC is stop-the-world) and how the hot paths avoid it, `README.md` for current status
(`docs/step-0.md` is historical only — pre-spike design, kept for record),
**`docs/web-target.md` before touching a web build** (the `Tools/build.sh web` recipe, the two
settings a web player must have or it silently never starts, what does and does not run off the main
thread there, and how to prove a build is the configuration you asked for), and
**`docs/lessons-learned.md` for hard-won engineering gotchas** (Unity/URP/HLSL + the headless test
workflow) — check it before debugging a shader/material/test-harness surprise.
`docs/job-scheduling-design.md` is the SSOT for how Burst work is chained (the job graph, the
`.Run()`/`.Schedule()` discriminator, and the disposal/cancellation invariant for in-flight `JobHandle`s);
it partly supersedes `docs/tile-pipeline-design.md`'s build seam — read "What this design owns"
for which parts.
Proprietary / all rights reserved.

`docs/*-design.md` state **the design and the principles that govern it** — the invariants a mechanism
must hold, the contracts between parts, and the reasoning that makes the current shape the right one
(including why a rejected alternative is wrong, where that still constrains the design). They are
**not** implementation histories. A stage sequence, a symptom report, a measured acceptance bar, a
changelog of resolved items, or a record of what landed when does **not** belong here: order and status
live in Jira, the acceptance detail lives with the work item or at the test's own site, and git holds
the history. The test is whether a sentence would still be worth reading by someone who has never heard
of the ticket that produced it. Write in the present tense about what *is*, not the past tense about
what happened. `docs/smooth-transitions-design.md` and `docs/depth-and-render-regimes-design.md` are the
model. A **limitation no test can observe** is design and stays.
`specs/*.md` are **normative**: they state the *contract* a mechanism must hold, and are
**authoritative** — where the code and a spec disagree, the code has a bug. Cite requirements by number in
reviews (e.g. *"violates SPEC-DPR R-9"*). See `specs/README.md` for the doc type and index.

## Project layout

**The product is `MapRenderer.Unity` + `MapRenderer.Jobs`, plus `MapRenderer.App` as the composition root
(`MapHost`, scene wiring, and the dev-facing surfaces built on it — camera control, menus, diagnostics);
`MapRenderer.Core` is legacy — not a destination for new code, and never a placement argument.** Renderer
logic still goes to `Unity`/`Jobs` only — `App` wires the product together, it does not host it. Put code
where it belongs architecturally, then test it wherever it lands. **New features are designed data-oriented
and native-first from the start** — the data
plane (anything per tile / feature / vertex / glyph / frame, or read inside a job) is born native; nativizing
later is not the plan. **Read `ARCHITECTURE.md` § "Module boundaries" before moving code between assemblies
or adding a type to Core** — it carries both rules, the rationale, and the three-workaround failure that
produced the first one.

- `Assets/Code/MapRenderer.Unity/` — **the product**: MonoBehaviours, mesh building, rendering glue.
- `Assets/Code/MapRenderer.Jobs/` — **the product**: Burst + Collections jobs; anything naturally blittable.
- `Assets/Code/MapRenderer.Core/` — **legacy, no new code**: engine-free tile math, geometry, earcut,
  style/expression evaluation, text shaping that predates the rule.
- `Assets/Code/MapRenderer.App/` — **the product (composition root)**: `MapHost` + scene wiring, plus
  the dev-facing surfaces built on it (camera control, menus, diagnostics/telemetry) — not a place for
  renderer/mesh-building logic.
- `Assets/Tests/MapRenderer.Tests.EditMode/` — headless EditMode tests.
- `Assets/Tests/MapRenderer.Tests.PlayMode/` — PlayMode tests (multi-frame/async behaviour EditMode can't
  exercise).
- `Assets/Tests/MapRenderer.Tests.Shared/` — shared test infra/fixtures used by both test runners.
- `Assets/Fixtures/` — committed test data (e.g. a sample MVT tile).
- Assemblies are split via `.asmdef`; Core does not depend on `MapRenderer.Unity`.

## Way of working

### Run tests yourself — don't ask the user to click in the Editor
Unity's Test Framework runs headless from the CLI in batch mode. Prefer this for every logic change;
it verifies **compilation and tests** without the GUI. Only fall back to asking the user to use the
in-Editor Test Runner if headless licensing is unavailable (see caveats).

**Recipe** — use the committed wrapper script (canonical command; run it from the repo root):
```bash
./Tools/run-tests.sh            # BOTH runners: EditMode, then PlayMode (default) — THE gate
./Tools/run-tests.sh EditMode   # EditMode only — the faster loop to iterate against
./Tools/run-tests.sh PlayMode   # PlayMode only
```
It is self-locating, drives `unity test` (which finds the Editor for this project's Unity version),
refuses to run if the Editor is open (exit 3), runs the tests, then prints the per-test results, any `error CS` lines, and a
**`VERDICT:` line last** — read that. **Run it in the background** (`run_in_background`): the first
batch launch does a full asset import + compile and can take minutes; you'll be notified on completion.

**A stage is done only against the unqualified command — both runners.** They run sequentially (one
Unity at a time; the project lock is exclusive) and the script stops at the first platform that fails,
so its exit code names exactly one. Each platform prints `VERDICT [EditMode]:` / `VERDICT [PlayMode]:`
and the combined `VERDICT:` is last. Each platform's XML is also kept as
`Logs/test-results-<platform>.xml` (and its log as `Logs/test-run-<platform>.log`), because the second
run overwrites `Logs/test-results.xml` — verify a new test name in the per-platform file. Iterating
with `EditMode` is fine; **declaring done on it is not** — PlayMode carries what EditMode cannot
exercise (multi-frame/async settle), and it sat red and unlooked-at for eight days (UMR-164) precisely
because the default skipped it.

The script's own exit code IS trustworthy — but only because it ignores the run's. Unity has been observed
returning both `0` and `1` for the same compile failure, and it does **not** rewrite
`Logs/test-results.xml` when compilation fails, so the previous run's green summary sits there looking
current. The script therefore moves any existing results to `Logs/test-results.prev.xml` before
launching (so the file existing proves *this* run wrote it), greps the log for `error CS`, and reads
`failed=`/`result=` out of the XML rather than trusting the process code:

| exit | meaning |
|---|---|
| `0` | compiled, results written by this run, every test passed |
| `1` | tests ran and something failed (or the run result isn't `Passed`) |
| `2` | setup error (not a repo, or the `unity` CLI is not on PATH) |
| `3` | this project's Editor is open — close it |
| `4` | compilation failed; **no tests ran** |
| `5` | no results produced for this run (crash), or an unfiltered run matched zero tests |
| `6` | the `Tools/core-tests` fast loop failed (or ran zero tests) — Unity was never launched |

Running both runners does not change that table: the code is the **first failing platform's**, and `0`
means every platform ran green.

> The script is allowlisted in `.claude/settings.json` (`Bash(./Tools/run-tests.sh:*)`) so it runs
> without a permission prompt. What it does, expanded, in case you need to invoke a step by hand:
> ```bash
> ROOT="$(git rev-parse --show-toplevel)"
> mkdir -p "$ROOT/Logs"
> # Move any existing results aside FIRST — Unity leaves them untouched when compilation fails, so
> # without this you cannot tell this run's results from the last one's.
> [ -f "$ROOT/Logs/test-results.xml" ] && mv -f "$ROOT/Logs/test-results.xml" "$ROOT/Logs/test-results.prev.xml"
> # Once per platform — EditMode then PlayMode, never concurrently (the project lock is exclusive),
> # moving the results aside again between the two.
> # `unity test` reads ProjectVersion.txt and locates the Editor itself. Always pass --mode: without
> # it the CLI runs "the editor's default platform". -logFile is forwarded to the Editor (the CLI has
> # no option of its own for it) because the `error CS` grep below has nothing else to read.
> unity test "$ROOT" --mode EditMode --output "$ROOT/Logs/test-results.xml" \
>   --no-banner --non-interactive -- -logFile "$ROOT/Logs/test-run.log"
> # Compile errors FIRST — they mean no tests ran, whatever the exit code says:
> grep -E 'error CS' "$ROOT/Logs/test-run.log" | sort -u
> # Then the results. An ABSENT file here means this run produced none — never read the .prev one as current.
> grep -oE '<test-run [^>]*result="[^"]*"[^>]*' "$ROOT/Logs/test-results.xml" | head -1
# Capture `fullname`, NOT `name`: sed is greedy, so `.*name="` runs forward to the LAST attribute
># ending in `name=` before `result=` — which is `classname`. Using `name` prints FIXTURE names and
># silently hides which TEST failed — a failure COUNT can reconcile while the failing NAMES do not.
> grep -oE '<test-case [^>]*' "$ROOT/Logs/test-results.xml" \
>   | sed -E 's/.*fullname="([^"]*)".*result="([^"]*)".*/\2  \1/' | grep -iE 'Passed|Failed'
> ```

### Fast Core tests (no Unity) — a faster loop for code that *already* lives in `MapRenderer.Core`
> Use it for the legacy code that is there; **never** a placement argument, and never a reason to put new
> code in Core — see `ARCHITECTURE.md` § "Module boundaries".

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
- **`./Tools/run-tests.sh` runs this project too, before launching Unity** — it is part of the gate, not
  a separate convenience script; a break here fails the script (exit `6`) without ever starting Unity.
  This project went unbuilt by anything for weeks in 2026-08 and rotted silently because the gate never
  exercised it — iterating here directly is still faster, but declaring a stage done no longer requires
  remembering to run it separately.

### Unity CLI caveats
- **The Editor must be closed.** Unity locks the project; batch mode can't run alongside an open Editor.
  Check before running with `unity editors running` — it enumerates from the process table plus each
  project's Pipeline lockfile, needs no package in the project it reports on, and is cross-platform.
  **Do not use `pgrep -x Unity`**, the old advice here: it does not exist on Git Bash / MSYS, where it
  fails OPEN — reporting no Editor while one holds the project. A bare `Temp/UnityLockfile` proves
  nothing either; it survives a killed batch run.
  If it's open, ask the user to quit Unity (Cmd+Q) — this is the one step you can't do for them.
- **Licensing.** Batch mode needs an activated license; the user should have opened the Editor via Unity
  Hub at least once so a license is cached. `Licensing::Module` handshake warnings in the log are
  usually non-fatal if a license is cached (compilation/tests still run).
- **`.meta` files.** Unity generates them on import — never hand-author them. New `.cs`/`.asmdef` files
  are picked up on the next Editor open or batch run.
- **`Logs/` is git-ignored** — safe to write test output there.

### Editing conventions
- **Placement is architectural, not test-driven** — `ARCHITECTURE.md` § "Module boundaries" is the rule.
- Logic that can be unit-tested **must** have EditMode tests wherever it lives; validate via the recipe above
  before declaring done.
- **Where that test GOES — topic, kind, lane, size — is `docs/test-conventions.md`.** It is an ordered
  decision tree, first match wins. Read it before adding a test FILE; a new file is correct only when the
  subject is new.
- Engine-only behavior (mesh build, rendering, camera) lives in `MapRenderer.Unity`; verify it visually
  in the Editor (a step the user runs).
- Vendored third-party code goes under `Assets/Code/ThirdParty/<name>/` with its license, and an entry in
  `THIRD-PARTY-NOTICES.txt`. Avoid copyleft (see `ARCHITECTURE.md` § "Licensing posture").

The code-style rules (math types, `System.Math` ban, `in` params, data carriers, builder naming,
test-code-bloat) live in **Coding conventions** below — don't restate them here.

### Multi-agent stage workflow (plan → develop ⇄ review)
A larger change lands one **stage** at a time via a three-role chain, each role a separate agent (models
chosen for the job — a strong reasoning model plans, a fast capable model develops, a strong model reviews).
Each role is a **tool-scoped subagent**: the planner reads and plans but writes no production code; the
developer implements and runs the gate; the reviewer reads the diff and edits nothing.
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
3. **Review.** A reviewer reads the working-tree diff and returns APPROVE or ranked actionable findings —
   stating an explicit verdict on each judgment call the change hinges on. Non-blocking findings are
   **recorded** (in the design doc, for the merge step), not necessarily fixed in-stage.
   **Reviewers do not launch the gate.** The dev's word is not trusted either — the *orchestrator* re-runs
   `./Tools/run-tests.sh` itself, once, on the frozen post-review tree, and that run is the authority. The
   reason is mechanical: Unity's project lock is exclusive, so a second batch run exits 3, and a review arm
   that RED-verifies by injecting a defect is *mutating the tree the gate is compiling* — a gate that races
   an arm reads clean and means nothing.
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
  open" is a false choice — and a brief that forbids leaving it open (rightly: a spec must not be committed
  while core design questions are open) has removed the only safe exit unless it supplies this one. Halting
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
