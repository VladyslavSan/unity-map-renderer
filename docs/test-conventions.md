# Test conventions — topic, kind, lane, size

Where a new test goes, which runner it goes in, and what it may assume when it gets there. Four questions,
four decision procedures. The last section is the one to read if you only read one.

The code-style rules a test file follows are the same ones production code follows —
[`conventions-short.md`](conventions-short.md). This file adds only what is specific to tests.

Every count below (files, tests, lines) was measured at `259b5533` (2026-09-19) over 427 files that carry
at least one test, and is **indicative, not current** — the suite moves (UMR-176 is queued to move it a
lot). Re-measure before relying on a specific number; the file/folder/lane RULES are what does not drift.

---

## 1. Topic — what a file is about

- **A test file is named for the SUBJECT under test, never for a production type.**
  - The name answers "what behaviour does this pin?", not "which class did I open?". `FillSortKey`, not
    `StyledFillTileBuilder`. This is [`conventions-short.md` §"Names carry meaning"](conventions-short.md)
    applied to file names.
  - Mirroring a type name is why a rename touches dozens of files that assert nothing about the rename.

- **The topic is one word from the commit-scope vocabulary in
  [`commit-conventions.md`](commit-conventions.md).** One vocabulary, not two: the folder a test lives in
  and the scope of the commit that changes it are the same word.

  | Topic | Files | Tests | Lines | Where those files sit today |
  |---|---:|---:|---:|---|
  | `text` | 105 | 573 | 32,781 | `Text/`, `Text/Placement/`, `Text/Sprites/` |
  | `tile-pipeline` | 74 | 320 | 21,115 | `Tiles/`, `MapView/`, `DataSources/`, `Lifetime/`, `Concurrency/` |
  | `meshing` | 60 | 325 | 17,735 | `Meshing/`, `Jobs/`, `Globe/`, `Geometry/` |
  | `style` | 59 | 741 | 15,904 | `Style/`, `Expressions/`, `Filters/` |
  | `render-layers` | 19 | 113 | 3,861 | `Rendering/`, `Materials/` |
  | `decode` | 15 | 131 | 4,482 | `Mvt/`, `GeoJson/`, `Json/` |
  | `camera` | 14 | 190 | 4,804 | `Camera/` |
  | `projection` | 3 | 18 | 762 | `Projection/` |

- **Three facts this mapping exposes. Read them before you trust the table.**
  - `text` is the largest topic in the suite and is **not** in the `AGENTS.md` scope list. Treat that as a
    gap in the doc, not a gap in the vocabulary.
  - `shaders` and `backends` are live commit scopes with no folder. Their tests sit in `Visual/` and
    `Structure/` instead — which is §2.
  - `Jobs/` splits by topic: a fill or line job is `meshing`, a decode job is `decode`. The folder is not
    the topic; what the job builds is.

## 2. Kind — the second axis

- **Topic is not the only axis. A test also has a KIND, and two kinds do not merge with anything.**
  - `Visual/` (55 files, 27,223 lines) and `Structure/` (21 files, 5,935 lines) are **kind** folders
    sitting in **topic** positions. That is 76 of 427 files, 18% of the suite, filed by how they work
    rather than by what they cover.

- **Behavioural** — calls production code and asserts the result. The default. Files by topic, §1.

- **Visual** — renders and reads pixels back, or needs a GPU context. Stays in `Visual/`.
  - It carries a recorded cross-fixture hazard: `docs/lessons-learned.md` records
    `DevicePixelRatioSnapshotTests` reading a shader global left behind by an **earlier fixture in the same
    batch**. A shader global is process state, so these fixtures are not independent of each other.
  - Some pass only in a full run — `ShadowReceiveBisectTests` reds 16 of 19 under `-testFilter` and passes
    19 of 19 unfiltered. Do not put such a fixture beside one people filter to.

- **Fence** — asserts a SHAPE, not a behaviour: it reads production source text off disk, walks the
  production file tree, or reflects over production types **where the reflection IS the assertion**.
  Lives in `Structure/`.
  - **The predicate is narrower than "uses reflection".** `grep -rl 'using System.Reflection'
    Assets/Tests --include='*Tests.cs' | grep -v /Structure/` finds 12 files, and most of them reflect to
    *reach* a private member under test, not to assert its shape — `Text/SymbolParkedRedecodeTests.cs:251`
    (`WasParked`) gets a private field by reflection and returns `field.GetValue(pass)`; the field's VALUE
    drives a behavioural assertion elsewhere, the reflection itself asserts nothing. A file only fences
    when the `Assert` reads a `Type`/`MemberInfo`/`FieldInfo` SHAPE, not an instance value.
  - **Fences are not optional and they are not a lesser test.** They catch what behavioural tests cannot
    see. On 2026-09-19 a data-carrier shape change (public fields → `init`-only auto-properties, the shape
    `conventions-short.md` §"Data carriers" requires) reddened
    `HashedStructs_FieldCounts_MatchHasherCoverage` while every behavioural test stayed green: reflection
    saw `<Segment>k__BackingField` and stopped counting what it polices.
  - **Two live counter-examples — a fence buried in a behavioural file is invisible to whoever goes
    looking for the fences.**
    - `Text/Placement/SymbolTileBlockGoldenTests.cs` carries the `HashedStructs_FieldCounts_…` guard above.
    - `Camera/ViewMathTests.cs:803-809` (`Selector_TSeam_InterfaceCarriesNoAlgorithmKnob`) reflects
      `IVisibleTileSelector.SelectVisibleTiles` and asserts its parameter shape — a real signature fence,
      not access, sitting outside `Structure/`.
  - A source-text fence must match the **identifier**, not the syntax around it — see
    `docs/lessons-learned.md`.

## 3. Lane — which runner

Three lanes. Take the **first** one whose entry condition holds.

- **Fast lane — `Tools/core-tests`.** Entry condition: every type the file names comes from
  `MapRenderer.Core`, the BCL, `NUnit`, or `Unity.Mathematics`.
  - Runs in about 0.1 s through `dotnet test`, with no Editor and no project lock. 104 files are in it.
  - It compiles the real `MapRenderer.Core` sources plus the EditMode test file **verbatim** — one source,
    two runners. `Unity.Mathematics` is a shim under `Tools/core-tests/Shim/`.
  - `MapRenderer.Jobs`, `Unity.Collections` and `UnityEngine` all disqualify. `Unity.Collections` is the
    one people miss: `Filters/FeatureSelectorNativeFilterTests.cs` names no `UnityEngine` type and is still
    engine-bound.
  - **The `<Compile Include>` entry in `Tools/core-tests/core-tests.csproj` moves in the SAME commit as
    the file.** Nothing tells you otherwise: the file compiles and passes in EditMode either way.
    `Expressions/EvalArgBuffersReclamationTests.cs` is engine-free, its `Core` dependency is already in the
    fast lane, and it has no csproj entry — so it runs only in the slow lane, and nothing reports that.

- **PlayMode.** Entry condition: the behaviour needs **real frames** — a player loop, `yield return null`,
  an async settle, a `ThreadPool` completion that lands between frames.
  - 15 test files. It is the smallest lane and the one that carries what EditMode cannot exercise.
  - PlayMode sat red and unlooked-at for eight days because the default gate skipped it. Both runners now
    run; a stage is done against the unqualified `./Tools/run-tests.sh`, never against `EditMode` alone.

- **EditMode.** Everything else.

- **What a test may assume in each lane.**

  | | fast (`dotnet test`) | EditMode | PlayMode |
  |---|---|---|---|
  | frame loop | no | no | yes — `[UnityTest]` + `yield` |
  | `UnityEngine` types | no | yes | yes |
  | GC meter | `GC.GetAllocatedBytesForCurrentThread()` | `Is.Not.AllocatingGCMemory()` | `Is.Not.AllocatingGCMemory()` |
  | project lock | none — runs with the Editor open | exclusive | exclusive |

  - **The two GC meters are not interchangeable, and the wrong one is silently vacuous, not red.**
    `GC.GetAllocatedBytesForCurrentThread()` returns a constant `0` in the Unity Mono runner, so
    `Assert.AreEqual(0L, after - before)` reduces to `0 == 0` and passes whatever the code does.
    `Is.Not.AllocatingGCMemory()` is Recorder-based and is the only live meter in EditMode; it is not
    available in the fast lane.
  - **A fast-lane file runs in BOTH lanes, so an assertion can be live in one and vacuous in the other.**
    `Style/StylePropertyTests.cs` is the live case: its three zero-allocation teeth measure in
    `dotnet test` and assert nothing in the EditMode run. If a tooth needs a meter, keep the file in one
    lane.
  - **Nothing in the repo detects this: both lanes report green.** A vacuous assertion still passes, so
    the hazard has no red signal of its own — see UMR-182, which tracks fixing the instance above.

## 4. Size — split and merge

- **A file is split when it passes 1,200 lines, along the SUBJECT axis.** Not before.
  - The cap is the suite's own p99 (p50 is 234 lines, p90 is 599). Eight files reach it today, two of them
    in `Visual/`. So it forces no churn on anything already written. It exists to stop a merged topic file
    becoming unreadable.
  - A 300-line file is not a reason to start a second one.

- **Two files merge when they share the same (topic, subject, lane). Small is not a reason to merge.**
  - Identity decides the file; size only ever triggers a split. There is no "these are both short" rule.
  - The median file holds **4** tests and 222 of 427 files hold 4 or fewer, because files are split per
    test METHOD rather than per subject. `Tiles/` holds `FillMeshGraphParityTests`,
    `FillMeshGraphSchedulingTests`, `FillMeshGraphGlobeParityTests`, `FillExtrusionGraphBuildTests` and
    `FillExtrusionMeshGraphSchedulingTests` — one subject, one lane, five files.
  - A file too thin to hold a builder makes every test in it construct its fixtures inline. That is the
    mechanism behind a 2-field struct rewriting 48 construction sites in 23 test files.

- **Four things stay in their own file whatever their size.** A small isolated file is the right design
  here, and none of them is evidence against the rule above.
  - A GPU or visual snapshot fixture (§2).
  - A fixture that only measures correctly in a full run (§2).
  - A Burst schedule probe. `Jobs/FillGraphBurstProbeTests` exercises the safety system's aliasing check at
    schedule time, which is sensitive to what else the process has scheduled.
  - A regression pin written for one specific defect. Keep its class name and its XML doc — the file
    identity is what records that the pin exists because of that defect.

## 5. Where a new test goes

Ordered. First match wins. Read down until one fires.

1. **Does it assert a shape — source text off disk, the production file tree, or reflection where the
   reflection IS the assertion (§2)?** → a fence file in `Structure/`. `Structure/` is flat today (23
   files, no subfolders); grouping it by topic is UMR-176's move, not this one's — UMR-175 forbids moving
   files. Never inside a behavioural fixture.
2. **Does it render and read pixels back, or need a GPU context?** → `Visual/`. Not merged with anything
   outside `Visual/`.
3. **Does it need real frames to settle?** → `MapRenderer.Tests.PlayMode/<Topic>/`.
4. **Is every type it names from `Core`, the BCL, NUnit or `Unity.Mathematics`?** →
   `MapRenderer.Tests.EditMode/<Topic>/`, **and add the `<Compile Include>` to
   `Tools/core-tests/core-tests.csproj` in the same commit.**
5. **Otherwise** → `MapRenderer.Tests.EditMode/<Topic>/`.

- **`<Topic>/` is one folder only for `camera` and `projection` — the folder name already matches the
  topic and nothing else claims it. The other six topics (§1) span more than one existing folder;
  UMR-175 does not move files, so the destination for a NEW file is the one below, not an average of
  where old ones already sit.**

  | Topic | Destination | Exception → its folder |
  |---|---|---|
  | `tile-pipeline` | `Tiles/` | concurrency primitive → `Concurrency/`; tile eviction/retention → `Lifetime/`; data-source loading → `DataSources/`; `MapView` selector/view-state → `MapView/` |
  | `meshing` | `Meshing/` | Burst schedule/determinism mechanics, not what the job computes (§4's schedule-probe case) → `Jobs/`; globe-specific subdivision → `Globe/`; pure geometry math with no job involved → `Geometry/` |
  | `style` | `Style/` | expression evaluation → `Expressions/`; filter compilation → `Filters/` |
  | `text` | `Text/` | placement/collision → `Text/Placement/`; sprite atlas → `Text/Sprites/` |
  | `decode` | the format under test names it: `Mvt/`, `GeoJson/`, or `Json/` | — |
  | `render-layers` | `Rendering/` | material assembly/config → `Materials/` |

- **Then pick the file inside that folder: the one whose (topic, subject, lane) your test shares.**
  - If that file is over 1,200 lines, split it along the subject axis and take the half you belong in.
  - **If no such file exists, create one** — named for the subject, not the production type. A new file is
    correct when the subject is new. It is not correct when a file for your subject already exists and you
    would rather not read it.
  - Shared fixtures and harnesses used by more than one runner go in `MapRenderer.Tests.Shared`, not in a
    production class. See [`conventions-short.md`](conventions-short.md) §"Test code must not bloat the
    production codebase".

## 6. What a test file may not do

- **No `Thread.Sleep` to wait for anything.** Settle by driving the thing: `yield return null` in PlayMode,
  an explicit drain or tick loop in EditMode. A sleeping thread does not advance the player loop, and the
  test then races the machine it runs on.
  - Two exceptions exist and both sleep a **worker** thread that is itself under test, not the test
    thread waiting for a result: a background release timer in
    `Tiles/TileManagerBackgroundRegistrationTests.cs`, and a `ThreadState` rendezvous with no event to park
    on in `Text/SymbolParkedRedecodeTests.cs`. Both carry their reason at the call site. If you need a
    third, write down which thread sleeps and why no event exists.

- **Never re-bake a snapshot or a golden to go green.** A moved snapshot means behaviour changed. Find out
  which, then decide. Re-baking deletes the only record that it moved.

- **No stage or ticket id in a test NAME** — see [`conventions-short.md`](conventions-short.md)
  §"Test file/class/method names must not carry a stage identifier".

- **A regression test is RED-verified.** Confirm it fails against the un-fixed code before you confirm it
  passes against the fixed code. A test that never failed pins nothing.

- **No production member that exists only for a test** — see [`conventions-short.md`](conventions-short.md)
  §"Test code must not bloat the production codebase".
