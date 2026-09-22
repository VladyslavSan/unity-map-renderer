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

- **A test file is named for its TOPIC, never for a production type. Where a topic needs more than one
  file (§4), each file is named for the sub-area that dominates it — not for a single subject and not for
  the class under test.**
  - The name still answers "what behaviour does this pin?", not "which class did I open?" — `FillSortKey`
    is a legitimate sub-area name inside `meshing`; `StyledFillTileBuilder` is not, because it names the
    production type instead of the behaviour. This is
    [`conventions-short.md` §"Names carry meaning"](conventions-short.md) applied to file names.
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
  - `Visual/` and `Structure/` are **kind** folders sitting in **topic** positions, filed by how they
    work rather than by what they cover. `Visual/` is its own assembly, `MapRenderer.Tests.Visual`, with
    its own `.asmdef` referencing only `MapRenderer.Tests.Shared` — it runs and can be skipped as a unit,
    separately from `Structure/`, which stays a folder inside `MapRenderer.Tests.EditMode`.

- **Behavioural** — calls production code and asserts the result. The default. Files by topic, §1.

- **Visual** — renders and reads pixels back, or needs a GPU context. Stays in `Visual/`
  (`MapRenderer.Tests.Visual`).
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

## 4. Size — pack by topic and lane, not by subject

- **Files are packed by TOPIC and LANE, filled up to 4,000 lines. SUBJECT is not a merge criterion.**
  Whether two tests share a file is a size question, bounded by topic and lane — not a subject question.
  - Two tests merge whenever they share (topic, lane) and the file they'd land in stays under the cap.
    They do not need to share a subject: one topic file can hold several unrelated subjects at once.
  - A file splits only when adding to it would cross 4,000 lines. Split however keeps both halves under
    the cap — not along a subject boundary.
  - Finding a specific test becomes a grep for its name rather than a walk to "the file that owns that
    class". That trade was made on purpose — see WHY below.

- **WHY a cap exists at all: not readability, a merge-conflict and load-time ceiling.** The maintainer
  accepts a long file — size is no longer a readability rule, it is the point past which one file becomes
  a merge-conflict magnet that everyone touching the topic collides on, and a slow thing to open and diff.
  4,000 lines is where that starts to bite; it is not a claim that a 4,000-line file is pleasant to read.

- **WHY 4,000 and not smaller: subject-pure grouping cannot reach a file count this suite can navigate.**
  Three floors, same 138,892 EditMode lines, assuming perfect packing:

    | Cap (lines) | Floor (files) |
    |---:|---:|
    | 1,200 (old, per-subject) | 115 |
    | 2,500 | 55 |
    | 4,000 | 34 |

  These are floors, not forecasts — the lane fence and the §4 stay-alone list push the real count above
  them. Topic+lane packing at 4,000 projects to roughly **63 EditMode files** before those protections
  force some singles back out, against **432 EditMode files today**. Subject-pure packing never gets
  close to any of these floors regardless of cap: its mean file is 321 lines, so it stalls around 373
  files whatever the cap says. The maintainer chose fewer, larger, topic-scoped files over many small
  subject-pure ones, accepting that a file holds unrelated subjects and that finding a test becomes a grep
  rather than opening the obvious file.

- **Worked example — the five `Fill*` files collapse to one.** `Tiles/` held `FillMeshGraphParityTests`,
  `FillMeshGraphSchedulingTests`, `FillMeshGraphGlobeParityTests`, `FillExtrusionGraphBuildTests` and
  `FillExtrusionMeshGraphSchedulingTests` — one topic, one lane, five files, 1,274 lines total. That was
  already five subjects under the old per-subject cap. Under the 4,000-line topic cap they fit in one file
  with room to spare — merge them.
  - A file too thin to hold a builder makes every test in it construct its fixtures inline. That is the
    mechanism behind a 2-field struct rewriting 48 construction sites in 23 test files — packing files
    fuller removes the incentive to skip the builder.

- **Five things stay in their own file whatever their size.** A small isolated file is the right design
  here, and none of them is evidence against the rule above.
  - A GPU or visual snapshot fixture (§2).
  - A fixture that only measures correctly in a full run (§2).
  - A Burst schedule probe. `Jobs/FillGraphBurstProbeTests` exercises the safety system's aliasing check at
    schedule time, which is sensitive to what else the process has scheduled.
  - A regression pin written for one specific defect. Keep its class name and its XML doc — the file
    identity is what records that the pin exists because of that defect.
  - A fixture whose `[SetUp]`/`[TearDown]` mutates PROCESS state — a shader global, `RenderSettings`,
    `QualitySettings`, an `Application` handler, `EditorSceneManager`. Six exist today. Packed beside an
    unrelated test, that test's pass would depend on run order — keep it alone regardless of topic or size.

### Merging files into a packed file

A packed file is reached by merging FILES, never CLASSES. Every fixture keeps its own class, so the set
of test fullnames (`Namespace.Class.Method`) is unchanged by the merge — that is what makes a packing
step verifiable rather than merely plausible, and it is why a merged file's namespace must not change
even when the file moves folder.

**Where two files cannot share a using set, they do not merge. Never edit a test to make a merge
possible** — not to qualify a type, not to rename a local, not to drop an import. Splitting costs one
file; the alternatives cost correctness, and a packed file is never so close to the cap that one more
file matters.

Four collisions arise in practice. The first three are loud; the fourth is silent, and is the reason the
rule is absolute rather than a preference.

| Union of usings | Ambiguous symbol |
|---|---|
| `System` + `UnityEngine` | `Object` — the widest, and the one with the most conditions. It needs all of: both imports, a genuinely bare `Object.X` use site (not `UnityEngine.Object.X`), AND no `using Object = UnityEngine.Object;` alias. That alias immunises a whole file and a dozen already do it. Testing imports alone over-splits badly |
| `System` + `MapRenderer.Core.Expressions` | `ValueType` |
| `MapRenderer.Core.Expressions` + `UnityEngine` | `Color` (Core declares its own `Color` struct) |
| `MapRenderer.Core.Geo` + `UnityEngine.Rendering` | `CameraProperties` (both are real types) |
| `UnityEngine` + `MapRenderer.Core.Text` | `TextAnchor` |

**Every one of these is immunised by an explicit alias.** A file carrying
`using Object = UnityEngine.Object;` or `using CameraProperties = MapRenderer.Core.Geo.CameraProperties;`
cannot suffer that ambiguity, whatever else is merged into it — a dozen files in the suite already rely on
this. So the test for a collision is three-part, not one: **both names imported, a genuinely bare use
site, and no disambiguating alias.** Testing imports alone reports files that have been compiling green
for months.

A merge may INHERIT such an alias from one of its inputs through the using-union, and is then protected
for free. Synthesising an alias no input had is a different act and is not safe: if one input's bare
`Object` meant `UnityEngine.Object` and another's meant `System.Object`, the alias silently rebinds the
second. Splitting stays the default.

The list is not closed. Two of these were found by merging, not by inspection, after a plan had already
been written on the assumption the earlier ones were complete. **When a merge surfaces a new collision,
re-sweep the destinations already built** — they were assembled under a model now known to be incomplete.

**Aliases.** `UnityEngine.TestTools.Constraints.Is` derives from `NUnit.Framework.Is` and adds a single
member, `AllocatingGCMemory()`. It hides nothing, so a file that gains the alias behaves identically —
every inherited constraint resolves to the same NUnit implementation. The hazard runs the other way and
is loud: a file calling `Is.AllocatingGCMemory()` merged into a destination without the alias does not
compile. Keep alias-carriers together for that reason, not because a union could weaken an assertion.

**Dropping a using is the same hazard wearing a different hat.** A merge takes the UNION of its inputs'
usings. Removing `using UnityEngine;` to stop a `Color` ambiguity does not fail — it rebinds every bare
`Color` in the file to `MapRenderer.Core.Expressions.Color`, which can compile, run and pass while
exercising a different type than the author wrote. Pruning is not a cheaper split.

**A file whose fixture hooks touch process state stays alone.** Merging changes NUnit's execution order,
so a `[TearDown]` that writes a shader global, resets a scene, or clears a static scheduler will reach
fixtures it never reached before. This applies to all four hook attributes independently — a
TearDown-only file is the case a `SetUp`-shaped search misses.

**Tests that assert a delta against a process-wide counter are order-sensitive by construction.** A
baseline subtracts leakage that already exists; it cannot subtract async work from another fixture that
completes inside the window. Such a test failing after a merge is information about the suite, not a
merge defect to be silenced — and it is never fixed by editing the test.

**When a destination file is split off deliberately, its header records which symbol collided.** An
undocumented split reads as an oversight and gets merged back by the next round.

## 5. Where a new test goes

Ordered. First match wins. Read down until one fires.

1. **Does it assert a shape — source text off disk, the production file tree, or reflection where the
   reflection IS the assertion (§2)?** → a fence file in `Structure/`. `Structure/` is flat today (23
   files, no subfolders); grouping it by topic is UMR-176's move, not this one's — UMR-175 forbids moving
   files. Never inside a behavioural fixture.
2. **Does it render and read pixels back, or need a GPU context?** → `Visual/`
   (`MapRenderer.Tests.Visual`, its own assembly). Not merged with anything outside `Visual/`.
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

- **Then pick the file inside that folder: the one for your (topic, lane) that still has room under the
  4,000-line cap.** Subject does not decide the file — size does (§4).
  - **If the file you'd add to is already at the cap, split it** — divide however keeps both halves under
    4,000 lines, not along a subject boundary.
  - **If no file for this (topic, lane) exists yet, create one** — named for the topic, or its dominant
    sub-area (§1). A new file is correct when the topic is new here, or every existing file for it is
    already full. It is not correct just because you would rather not read the file that has room.
  - Shared fixtures and harnesses used by more than one runner go in `MapRenderer.Tests.Shared`, not in a
    production class. See [`conventions-short.md`](conventions-short.md) §"Test code must not bloat the
    production codebase".

## 6. What a test file may not do

- **No `Thread.Sleep` to wait for anything.** Settle by driving the thing: `yield return null` in PlayMode,
  an explicit drain or tick loop in EditMode. A sleeping thread does not advance the player loop, and the
  test then races the machine it runs on.
  - Two exceptions exist and both sleep a **worker** thread that is itself under test, not the test
    thread waiting for a result: a background release timer in
    `Tiles/ThrottleTests.cs` (the merged home of the former `TileManagerBackgroundRegistrationTests.cs`),
    and a `ThreadState` rendezvous with no event to park
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
