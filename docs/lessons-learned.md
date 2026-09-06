# Lessons learned — engineering gotchas

Generic, hard-won knowledge discovered while building this project: Unity/URP/HLSL/DOTS and the headless
test workflow. These are **engineering** gotchas — knowledge about the code and tooling, not process. Add
a lesson here when it (a) cost real debugging time, (b) is
not obvious from the code, and (c) will recur. Keep each entry tight and actionable.

## Shaders & HLSL

- **Editing a shared `.hlsl` CBUFFER/include can run STALE shader variants on the first headless batch
  run.** The GPU reads properties at the *old* CBUFFER offsets → garbage colors/lighting, while EditMode
  compiles clean. Tell-tale: a GPU-snapshot test fails in a way that's causally unrelated to your change
  (e.g. a normal-map effect goes to `diff=0` after a *color* edit). **Re-run `./Tools/run-tests.sh` once
  before treating a GPU-snapshot failure as a real regression** — a second clean batch run recompiles.
  Harder reset: delete `Library/ShaderCache`. (Seen: S58 `_MapColor` collapse — `BrgBackendSnapshotTests`
  + `LitFill_NormalMap` both failed on run 1, both passed on run 2.)

- **SRP Batcher requires the `UnityPerMaterial` CBUFFER to be byte-identical across every pass of a
  shader.** Define it once in a shared include (`Fill_LitInput.hlsl` / `Line_LitInput.hlsl`); never add/remove
  members in per-pass code. Removing or adding a CBUFFER member shifts every later member's offset, and the
  change must be mirrored in **three** places that travel together: the CBUFFER, the
  `UNITY_DOTS_INSTANCING` block (+ sampled statics + `#define`s), and any BRG SoA packing
  (`BrgTileRenderer` `Pfx_*` offsets + `FloatsPerInstance` + `MetaCount`). Miss one → wrong-offset reads.

- **NEVER name an internal shader property after a MapLibre style term — the styler binds `line-X → _X`
  and silently overwrites it.** `MaterialFactory`/`ZoomStyleApplier.BindFloat` maps each style paint/layout
  property onto a shader property by stripping the prefix (`line-width → _Width`, `line-opacity → _Opacity`,
  `line-blur → _Blur`). An internal render param that reuses such a name gets clobbered with the style value
  at material-build time — no error, just wrong visuals. **The bug (2026-06-28):** the line antialiasing
  edge-width knob was named `_Blur`, collided with `line-blur` (spec default **0**), so `MaterialFactory`
  set `_Blur = 0` on every rendered material → **AA was off the whole time** on all backends. Took a
  frame-debugger capture (`_Blur = 0` despite the inspector showing 4) to find. Fix: the AA knob became
  `_AaEdgeWidth` (internal, default 1, never style-bound), `_Blur` now honestly means `line-blur`, and the
  shader uses `(_AaEdgeWidth + _Blur)`. **Rule:** before naming a `_X` property, grep `PropertyNames.cs` /
  `PaintProperties.cs`; reserve style-derived names for real bindings, and give internal params clearly
  non-style names (cf. `_WidthIsPixels`, `_MetersPerPixel`). Keep the two kinds in **separate, labeled
  groups** in the CBUFFER / Properties block so the boundary is visible. *(Update 2026-07-06: line edge AA and
  `_AaEdgeWidth` were removed — lines rendered a hard edge. **Superseded 2026-07-30:** AA is back as a strict
  one-pixel straddle, `docs/line-antialiasing-design.md`. `_AaEdgeWidth` did **not** come back and must not —
  the ramp width is a compile-time constant precisely because a bindable AA width is how the removed model
  went wrong. The naming rule above is unaffected and still binding.)*

- **A fade *inside* the styled band cannot render a crisp *cased* line — it's a compositing problem, not a
  coverage one, and MSAA doesn't fix it. A fade that stays *at* the edge is fine.** A cased road is two
  stacked transparent draws (casing under, fill over; `Queue=Transparent`, `ZWrite Off`). A shader-space
  alpha fade that eats **inward** from the fill's edge ramps to transparent over the casing and **bleeds the
  casing colour through** → the fill↔casing boundary blurs. MSAA only cleans geometry coverage while the two
  layers still *blend*, so it gives the same mush at higher cost — measured here to **16× with no meaningful
  gain**, which also rules out alpha-to-coverage (it needs MSAA to do anything).
  *(Corrected 2026-07-30 — an earlier revision of this entry was too strong and is why AA stayed removed
  longer than it needed to.)* It claimed "inset / outset / straddle all bleed" and stated a hard trilemma
  {solid core, unchanged apparent width, antialiased} — pick two. **A strict ±0.5 px straddle breaks both
  claims:** with the ramp centred on the styled edge, half in and half out, everywhere the fill is meant to
  be opaque has `a == 1`, and `dst = src·a + dst·(1−a)` cannot show anything through at `a == 1`. The
  trilemma is escaped by buying the third property with **geometry** — a fixed 0.5 px pad on the ribbon,
  added *before* the miter multiply — rather than with the fade. That is what ships now, behind
  `_EdgeAntialiasing`. The architectural **single-pass cased line** remains the better long-term answer for
  same-layer *junction* under-coverage, but it is no longer required for crispness. Full write-up:
  **`docs/line-antialiasing-design.md`**; mechanism notes in
  `Assets/Code/MapRenderer.Unity/Shaders/Map/Line/README.md`.
  *(Second correction, A6.0 — this entry still described only ONE of the removed AA's two defects; see the
  next bullet for the other.)*

- **`fwidth` is the WRONG length for an AA ramp — it is Manhattan, and it makes antialiasing quality depend
  on a line's screen direction.** `fwidth(x) = abs(ddx(x)) + abs(ddy(x))`, the L1 length of the screen-space
  gradient; the true length is L2, and the ratio is `|cos θ| + |sin θ| ∈ [1, √2]`. A ramp divided by it is
  1.00 px on an axis-aligned edge and **1.41 px on a 45° diagonal**, which also costs the diagonal 0.41 px of
  ink (the profile integral is `W + 1 − c`). Measured on a styled 12 px line: horizontal **12.003 px**,
  diagonal **11.586 px** — dead on the √2 prediction. Use `length(float2(ddx(x), ddy(x)))` wherever "one
  device pixel" must mean the same thing in every direction. This was the **second, never-diagnosed** defect
  of the AA removed in `0b910c7`: the write-up blamed the compositing bleed alone, so when the rebuild reused
  the same expression the defect shipped again and was caught only in A6.0. Two lessons for the price of one
  — a horizontal-fixture tooth cannot see it (`fwidth` is exact when one derivative is zero), and **"we
  already know why that failed" deserves re-checking when the fix reuses the failed code.**

## Rendering loop & camera

- **Under camera-relative rendering a pure PAN never moves the camera — so tile/label frame-coherence is a
  snapshot-PHASE problem, not a camera-matrix one.** The camera orbits the scene origin (the look-at sits at
  world origin every frame), so a pan only changes each frame's `SceneOriginRender` (the floating-origin
  rebase); the `worldToCameraMatrix`/`projectionMatrix` are unchanged. A ~1-frame "labels lag the map during
  pan" was NOT stale matrices — forcing `ResetWorldToCameraMatrix()`/`ResetProjectionMatrix()`, pinning
  `Camera.worldToCameraMatrix`, and re-projecting at `RenderPipelineManager.beginCameraRendering` ALL did
  nothing. The cause: tiles were rebased in the `Update` phase while the camera commit + label placement ran
  in `LateUpdate`, two different phases racing the input `Controller.Update` (sibling MonoBehaviour `Update`
  order is unspecified in Unity). Fix: run the whole per-frame pipeline — commit camera → move tiles → place
  labels — in ONE pass off a SINGLE `CurrentProperties` snapshot, driven from `LateUpdate`. Unity runs every
  `LateUpdate` after every `Update`, so it always sees this frame's input with no `DefaultExecutionOrder`
  fragility, and tiles+labels can't diverge. (2026-07-07, S20.)

- **An UNMARKED hot spot looks exactly like an environment artifact — check that the span is marked at all
  before blaming the Editor.** While profiling the symbol-label epic, a "camera moving costs +5 ms" delta
  appeared that was **absent from the main loop's marker tree entirely**: the main `PlayerLoop` was
  byte-for-byte the same still vs moving, while a *second* frametime grew. That was first written up here as
  Editor-side repaint (Inspector/Scene-view redraw scales with mouse activity, i.e. with panning), which is a
  tempting fit because repaint and real work are **confounded by the same input** — moving the mouse both pans
  the camera and drives repaint.

  **It was wrong.** The delta was real, unmarked main-thread work: the all-or-nothing mirror rebuild, which
  runs on virtually every frame under motion because the memo is structurally dead there. Bursting it closed
  the gap (13 ms vs 19 ms still-vs-moving became ~identical) — an Editor repaint artifact would not have been
  fixed by a Burst job. It was invisible in the marker tree because **the hot spot had no marker yet**;
  `Symbol.BatchBuild` was added afterwards, precisely to surface it.

  So the defence is the inverse of what this entry used to say. "The sum of my markers didn't move, so the cost
  isn't mine" is only sound if the suspect code is *instrumented* — otherwise absence from the tree is evidence
  of a missing marker, not of innocence. Mark the span, re-profile, and only then reach for the environment.
  The second defence stands unchanged: instrument a counter only your code can move (the epic's
  mirror-rebuild-rate counter — repaint cannot bump it) and check it tracks the delta.

  Two things from the original entry survive: an Editor Play-mode capture really can show two `PlayerLoop`s and
  two render loops per frame, so take perf verdicts from a Development standalone build — as a **capture
  caveat**, not as an explanation for any particular delta. And a telemetry surface that inflates the thing it
  measures is worse than no telemetry: the live Inspector panel writing public fields every frame was itself
  forcing repaint, which is why the panel is now off by default. See `docs/telemetry-design.md` §1.3 and
  `docs/symbol-label-perf-design.md` §10.4. (2026-07-25, corrected 2026-07-27.)

## DOTS / Entities Graphics

- **GC is stop-the-world, so an allocation's cost surfaces as a phantom CPU spike in an unrelated
  marker.** A collection freezes every thread and the profiler charges the frozen time to whichever marker
  is on the stack — so a heavy cost that *wanders between markers frame to frame* and *always co-occurs with
  a GC-Alloc spike* is GC, not that marker. Off-thread allocations freeze the main thread too. The full
  narrative, the current allocation state, and the allocation-hunting discipline are in
  [`gc-and-allocation-design.md`](gc-and-allocation-design.md); the ladder rule is in
  [`conventions.md`](conventions.md); the GC-meter caveats are under **Test workflow** below.

- **An Entities-Graphics entity renders NOTHING in a headless EditMode test until you tick its system
  groups manually.** EG submits draws from `EntitiesGraphicsSystem`, which runs in the player-loop
  presentation group — and that loop does not tick in EditMode (`camera.Render()` alone won't drive it).
  The manual-BRG backend works headless because it calls `BatchRendererGroup` directly; EG does not. The
  driving sequence that works: `DefaultWorldInitialization.Initialize(name, editorWorld:false)` → set
  `World.DefaultGameObjectInjectionWorld` → tick `InitializationSystemGroup` / `SimulationSystemGroup` /
  `PresentationSystemGroup` (EG lives in Presentation; uploads instance data + registers the BRG batch) →
  *then* `camera.Render()` (SRP culling invokes EG's `OnPerformCulling`, which emits the draws). At
  runtime the player loop does this for you. (Seen: S53a spike.)

- **Disable Entities' automatic default-world bootstrap** with the `UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP`
  scripting define (Player settings) in a project that only uses ECS for one optional subsystem. Two
  reasons: (1) correct architecture — create the world on demand when the ECS path is selected, so the
  non-ECS shipping path pays nothing; (2) the auto-created **editor** world's systems tick on editor
  update and perturb fragile zero-tolerance `Is.Not.AllocatingGCMemory()` tests (a stray allocation lands
  in the measurement window). The define is compile-time and cascades to the editor-world gate;
  `ICustomBootstrap` is runtime-only and does NOT stop the editor world. Create worlds manually via
  `DefaultWorldInitialization.Initialize` thereafter. (Seen: S53a — adding Entities flipped a no-GC test
  until the define was set; verified 0 automatic worlds created afterward.)

- **An Entities-Graphics World draws into EVERY camera until it is disposed.** EG registers a
  `BatchRendererGroup` owned by `EntitiesGraphicsSystem`; while the World lives, its entities render in
  any `camera.Render()` — including later, unrelated tests. A leaked Entities World therefore bleeds
  geometry into other tests (it broke a "blank render must be blank" assertion). Dispose the World on
  teardown (`World.Dispose()` runs the EG system's `OnDestroy`, unregistering the BRG). In EditMode tests,
  `OnDestroy` does NOT fire on `Object.DestroyImmediate` — call the explicit teardown
  (`MapView.Teardown()`) before destroying the GameObject. (Seen: S53b.)

- **`Is.Not.AllocatingGCMemory()` tests are flaky under a heavier domain.** They assert *exactly* zero
  allocation, so any stray allocation from added assemblies / background activity fails them — under the
  full suite, not in isolation, and the failing set rotates run-to-run. Before treating one as a
  regression, re-run it **in isolation**; a logic regression fails deterministically and in isolation too.
  (Seen: S53a — installing the Entities packages made 1–4 such tests flake per full run; all passed in
  isolation.)

- **A render backend that draws on instance/entity creation must POSITION it at creation — not next
  frame.** `MapView.Tick` recomputes transforms (`InstancedRebuild`) *before* it consumes newly-built
  tiles, so an entity created with `LocalToWorld.identity` renders at the world origin for one frame until
  the next Rebuild moves it — a visible blink during zoom. The GameObject backend never showed this (it
  sets the container transform at creation) and BRG never showed it (it defers all drawing to Rebuild, so
  a new instance is simply absent for a frame, never misplaced). Only the Entities backend, whose entity
  is immediately live with render components + generous bounds, flashed. Fix: cache the last scene origin
  in `Rebuild` and place each entity at its correct position the instant it is created. (Seen: S53b
  follow-up.)

- **A "generous" FIXED `RenderBounds` box is NOT a safe never-cull hack — it must ENCLOSE the mesh, and a
  fixed box centred at the origin fails at low zoom.** EG frustum-culls each entity by
  `WorldRenderBounds = RenderBounds × LocalToWorld`; `RenderBounds` is an *entity-local* AABB. The Entities
  backend stamped every tile `{ Center=0, Extents=1e6 }` "so culling never drops a tile." But render units
  are **ECEF metres** (`SphericalProjection.MetersPerUnit == 1`), and a tile mesh is origin-relative with
  one corner at local `(0,0,0)` reaching *out* to its far corner — so at low zoom the mesh is far bigger
  than the box **and off-centre from it**: Mercator z3 spans ≈5e6 m, z4 ≈2.5e6 m; the globe at z0–1 reaches
  the earth radius R≈6.4e6 m. The `1e6` box then hugs the origin corner, and EG culls the **whole tile** the
  instant that corner leaves the frustum — tiles vanish in the **Game** view while the **Scene** view (a
  wider camera + different frustum) still shows them, and **screen-space labels** (`1e9` bound, a separate
  path) fill the gaps, which misleads you into thinking the geometry is present-but-hidden. The bug switches
  off above the zoom where a tile shrinks below `1e6` m (Mercator ≈z6), which is the diagnostic tell. **Fix:
  stamp `RenderBounds` from the mesh's own tight `mesh.bounds`** (the fill/line builders already compute a
  correctly-centred AABB in the same origin-relative frame as the vertices, so it maps correctly under the
  same `LocalToWorld`; a rotation only inflates the world AABB — conservative). Keep the generous box only as
  a fallback for a *degenerate* (zero-size) mesh. **General rule: never fake bounds with a constant — a bound
  that doesn't enclose its geometry is a latent cull bug that only shows at some scales.** (BRG was immune:
  it does minimal culling — emits all live items, no per-instance frustum test — behind a `1e8` batch bound;
  GameObject `MeshRenderer` reads `mesh.bounds` itself. So this was Entities-only.) Verify with an
  enclosure test at the `AddTileLayer` seam (a >1e6-span mesh's `RenderBounds` must contain every vertex),
  not a snapshot — culling drops zero-pixel geometry, so on-screen tiles stay pixel-identical.
  (Seen: 2026-07-17 — tiles culled in Game view at globe z0–1 / Mercator z3–4, maintainer-confirmed fixed.)

- **`Child` is `ICleanupBufferElementData`, so destroying a transform-parent leaves a cleanup-zombie.**
  `ParentSystem` adds a `Child` buffer to any entity that becomes a parent; because it is a *cleanup*
  buffer, `EntityManager.DestroyEntity` on that parent does not finalize it — the entity lingers (and
  `EntityManager.Exists` still returns true) until a later `ParentSystem` tick removes the component. If
  you maintain your own handle/parent bookkeeping, treat your own dictionary as the source of truth and
  remove the entry on destroy, so the transient `Exists` staleness never leaks into your public lifecycle
  API. Destroy children before the parent (we ref-count layers per tile root and destroy the root only
  when its last layer goes), so no live child is ever orphaned. (Seen: S53b follow-up — per-tile root
  entities.)

- **`if (x.IsCreated) x.Dispose()` — mostly bloat, but NOT always. CORRECTED 2026-09-03.**
  ~~The claim below that `NativeArray`'s `Dispose()` early-returns on `!IsCreated` is **FALSE**.~~ Raw
  `NativeArray<T>.Dispose()` invalidates the safety handle and throws on a second call; only `NativeList<T>`
  and structs with their own `if (!IsCreated) return;` no-op. Acting on the flat version of this rule, a
  sweep of all 57 guards reddened **106 tests**. See `conventions.md` §"`if (x.IsCreated) x.Dispose();` —
  know whether it is redundant or load-bearing" for the discriminator. Original text, premise included:
  `NativeArray`/`NativeList`/etc. `.Dispose()` already early-returns on `!IsCreated` (and on a default,
  never-allocated value). So `if (x.IsCreated) x.Dispose();` adds a redundant check that does *nothing* —
  just write `x.Dispose();`. Same for grow-realloc: `oldBuf.Dispose(); oldBuf = new NativeArray(...)` needs
  no guard on the first (default) pass. A single struct-level idempotency flag (`if (!IsCreated) return;`
  guarding a whole `Dispose()` body) is fine; per-field `IsCreated` guards are the smell.

- **Don't hand-grow a `NativeArray` with dispose-realloc + a tracked capacity — use `NativeList`.** The
  pattern `if (n > cap) { buf.Dispose(); buf = new NativeArray<T>(n, …); cap = n; }` reimplements, by hand,
  exactly what `NativeList<T>.Resize(n, …)` does (grow-only backing buffer, set length). Declare a
  `NativeList<T>`, `Resize(n, NativeArrayOptions.UninitializedMemory)` per use, pass `.AsArray()` at Burst
  job boundaries (jobs take `NativeArray`), and `Dispose()` once. Deletes the cap variables and the
  realloc blocks. (Seen: `StyledLineTileBuilder` — 7 scratch buffers + 4 hand-tracked caps → 7 `NativeList`s.)

- **A job's internal scratch must be a LOCAL inside `Execute`, never an instance `NativeArray` field.** The
  job-safety system validates *every* `NativeContainer` field at schedule time; a field you only allocate
  inside `Execute` is `default` at schedule and throws `InvalidOperationException: The … <field> has not been
  assigned or constructed. All containers must be valid when scheduling a job.` — even under `.Run()`, even
  with Burst. It compiles clean, so it only shows at runtime (cost a full gate cycle in S100's `RibbonJob`).
  Keep scratch as `var x = new NativeArray<T>(…, Allocator.Temp)` locals and pass values (not the arrays) into
  helper methods — the pattern `RibbonJob`/`RibbonJob` follow. Only INPUT/OUTPUT containers
  (assigned before scheduling) belong as job fields.

### A RED injection that removes an edge may also remove the node — then it proves reachability, not the dependency

When RED-verifying a job graph by deleting a dependency edge, check whether that edge was the node's **only**
link to the terminal handle. If it was, the injection orphans the node too, and the violation the safety
system surfaces is an unreachability consequence — at deallocate time, naming whichever container is next
touched synchronously — rather than the conflict you aimed at. The RED goes red, so the tooth looks verified;
it verified a weaker claim.

*2026-09-02, the fill graph:* two nodes wrote a shared counts array with no edge between them — a real bug.
Dropping that edge threw, but named `RingAssemblyJob` and `FeatureGeometryType`, a bystander. Isolating the
injection — remove the edge under test, keep the node reachable through the terminal, and route the one
dispose that also depended on it — produced the intended message at the second job's `.Schedule()`, naming
both jobs and the shared container. **An edge injection must preserve node reachability, or it is an
orphaning injection wearing an edge injection's name.** Two of that stage's ten REDs were affected; both were
re-run isolated.

**The same signature can also defeat a positive control, and then the honest answer is "inconclusive".**
Stage 2's curved arm relies on the geodetic/project nodes being genuinely dead — `GlobeFillSubdivideJob`
takes only tile verts, indices and feature index, and projects internally, so it has no world-position input
to read. That claim was established by **reading the job's field list**. The attempted positive control —
re-scheduling those nodes anyway, correctly threaded into the terminal handle, expecting nothing to red —
instead threw a job-safety `InvalidOperationException` naming a *different, unrelated* upstream job on each
run (`RingAssemblyJob`, `RingSelectJob`, …): the bystander signature above. Root cause was not chased; the
suspicion is scheduling-order sensitivity in deferred-length `IJobParallelFor.Schedule(NativeList, …)`, not a
defect in the branch under test.

Two things worth keeping from that. First, **an inconclusive control is evidence for neither side** — record
it as inconclusive rather than letting "it threw" read as a refutation or "we tried" read as verification.
Second, **when a control cannot be made to work, a structural reading can still settle the claim**: the field
list either contains the input or it does not. Say which instrument answered the question. If you are
re-deriving this deadness claim later, read the fields — do not retry that control unmodified.

### A RED proves that SOME assertion in the test bites — not the one you had in mind

A RED going red is where scrutiny normally stops, which is exactly why an inert assertion survives there. If
a test asserts inside a loop or an `if`, the injection may redden it through a *different* assertion while
the one the test is named for never executes.

*2026-09-02, the fill graph:* a test named `…_BoundariesAndSumsMatchSubdividedArrays` had its boundary
assertion behind `if (ci > 0)`, and the fixture produced exactly one chunk — so that branch never ran in the
green case **or** the red one. The RED reddened via the sum assertions and read as verification of the whole
test. This was the second inert-assertion case in one epic; the first was predicted inert and honestly
recorded, and the difference was only that someone looked.

**How to apply:** when an assertion sits inside a guard, assert the **guard is satisfiable** as a
precondition — `Assert.Greater(chunks.Length, 1)`, `Assert.IsTrue(anySplitFired)` — which converts a silently
skipped branch into a loud one. And when RED-verifying, confirm the *specific* assertion fired, not merely
that the test failed: read the failure message and check it is the one you aimed at. Related:
[[red-verify-counts-must-reconcile-with-names]].

### A process-wide dispose-balance counter makes RED results order-dependent

A leak counter implemented as a `static` (`FillGraphOutput.DebugLiveCount`, `MeshDataPayload.
DebugLiveAllocCount`, `TileBuildGraph.DebugLiveCount`) is **process-wide**, and an EditMode batch run is one
process. So a leak deliberately injected in one test poisons the baseline for **every** dispose-balance test
in the same run, and a single injected defect surfaces as two or more failures.

*Found during a RED set, 2026-09-02.* Two tests failed; it read as two defects until the shared static was
noticed.

**How to apply:** assert a **delta** around the operation under test, never an absolute "counter is zero" —
capture the count before, act, capture after, compare. An absolute assertion is correct only for the first
such test to run in a batch, and test order is not guaranteed. When a RED produces more dispose-balance
failures than defects injected, suspect the shared static before suspecting a second bug. This applies to any
counter a test both reads and perturbs, not just these three.

### An exception unwinding through a `using` whose `Dispose()` also throws is silently replaced

If a test triggers a job-safety violation and the enclosing `using` scope disposes a container that is still
registered to a live job, the disposal throws *during unwinding* and the CLR reports **that** exception. The
original — the one naming your actual defect — is discarded. You get a confident, precise, and entirely
misleading diagnosis pointing at cleanup.

*2026-09-02:* this masked the same write-write violation twice, through two different scopes — first the
test's own `try` opening after the calls it needed to guard, then one level further out at the corpus loop's
`using var mvtTile`. Fixing the inner one did not reveal the message, because the outer one took over. What
worked was a throwaway diagnostic with **no `using` and no cleanup at all**, which let the first exception
propagate untouched. When a safety-violation message names a container you did not expect, suspect the
cleanup path before you re-derive the design: put the failing call in a scope that disposes nothing.

### A test gap written down as a rationale becomes a requirement

When you document *why* a code path exists, check that the reason is a design constraint and not an
observation about the tests. Prose outlives the situation it described, and the next reader takes it as
intent.

*2026-09-02:* `ProjectionDispatch.Schedule`'s XML doc justified keeping its `case null` branch with "every
existing test leaves `LayerInput.Projection` unset, so a missed null case would silently lose the whole
corpus." That sentence is true, and it is a statement about a **coverage gap** — production never passes
null (`StyledFillTileBuilder.cs:428` passes `projection ?? DefaultProjection`). Written as a rationale, it
reads as a requirement, and it made the gap look deliberate to two independent review arms: both accepted a
tooth that reached the dispatch only through the branch production never takes.

**How to apply:** a comment of the form "the tests do X, therefore the code must do Y" is a defect report,
not a design note. Fix the tests and delete the sentence, or state plainly that the branch exists for a
production case and name it. Related: [[tooth-vacuous-or-overclaiming]].

### A job's container fields are validated whether or not `Execute()` reads them — nested structs included

Unity's schedule-time container validation walks **every** container field on a job struct, and recurses into
nested structs. A field left at literal `default` fails at `Schedule` even if the job's body never touches it.
There is no "unused field" exemption, and `[ReadOnly]` does not grant one.

*Stage 1 (2026-09-02):* an additive optional `NativeArray<int>` on an existing job broke all its callers at
runtime while compiling clean — a `NativeArray` job field left `default` fails validation regardless of
`[ReadOnly]`, and even through `.Run()`, which still goes via `JobsUtility.Schedule`. The fix was to make the
discriminator a **value type** (a `bool`), which has no such constraint.

*The scratch-struct refactor (same day):* grouping 19 scratch columns into one struct passed as a single job
field surfaced the same rule one level deeper — a hand-built instance in a test left one unused column
`default` and the gate failed with `EarcutBatchJob.Scratch.PerPolyFeatureIdx has not been assigned or
constructed`. So a grouped scratch struct is **all-or-nothing**: every container in it must be constructed by
every caller, including callers whose job ignores most of them.

**How to apply:** give a grouped scratch struct a single `Allocate()` that constructs every field, and build
it that way everywhere — hand-construction is what reintroduces the hazard. When adding an optional field to
an existing job, a value type is safe additive and a container is not; the failure is invisible until a gate
run, because it compiles clean.

### A job writing several `Mesh.MeshData` streams takes the whole `MeshData`, not one field per stream

Every stream view of one `Mesh.MeshData` — each `GetVertexData<T>(stream)` and `GetIndexData<T>()` — is
tracked by the safety system under **one shared handle**. So two *writable* views of the same `MeshData`
cannot be two job fields: scheduling throws *"Stream0 is the same … as Stream1, two containers may not be the
same (aliasing)"*. The working shape is the one Unity documents for `MeshData` in a job — pass the
`Mesh.MeshData` itself as a single field and resolve every view **inside `Execute()`**, where the job holds
exclusive access to the whole struct and extracting several views from within its own execution needs no
cross-view aliasing check. The caller still sizes it (`SetVertexBufferParams`/`SetIndexBufferParams`) on the
main thread beforehand.

*Stage 2 (2026-09-02).* The first framing of this was **wrong and is corrected here**: the failure looked
like a *call-order* bug — declaring the index buffer before taking the vertex views — because reordering the
calls changed which pair collided first. Call order is not the constraint; the number of writable views held
as job fields is. Reordering only moves the collision.

**The transferable part is why the stage's own probe missed it.** A probe had already established that a
scheduled Burst job can write a `MeshData`-derived `NativeArray` and read it back after `Complete()` — but it
calibrated a **single** stream, and the defect only exists with two or more. A probe that answers "can a job
touch this at all?" does not answer "can a job touch *several of these*?", and the second question is the one
the real code asks. When a probe clears an API, check that its shape matches the shape production will use —
count included. See also *blind spots don't transfer between instruments*.

### "What production passes" is not one value — name the configuration

An audit that records what production passes to a behaviour-selecting field will generalise from **the
shipped scene** to **production** if nobody stops it. Those are different, and the difference silently
inverts a finding.

*2026-09-02 → corrected 2026-09-03.* An audit recorded `LayerInput.Projection` as "production passes a real
projection; the shipped scene is `UseGlobe: 1`", and twelve tests leaving it unset were filed as driving an
arm production never takes. But `MapHost` passes `UseGlobe ? new SphericalProjection() : null` — **null is
the production value for the planar case**, and `ProjectionDispatch`'s `case null` maps it to
`WebMercatorProjection`, matching the seam arm's `?? DefaultProjection`. Those tests were exercising a real
production path: the wrong *shipped configuration*, which is still worth fixing, but not a phantom arm. The
overstated form was repeated in briefs for a full working session before the code was re-read.

The sibling row in the same audit — `LayerInput.Clip`, where `FromInspectorUnits` disables only on a negative
value and the config default is `0.0` — was correct, which is why the table as a whole read as trustworthy.

**How to apply:** when recording what production passes, **name the configuration** ("globe passes
`SphericalProjection`; planar passes `null`"), never just "production". If a field's production value depends
on a launch-time or per-scene switch, the audit row has as many entries as the switch has values. And a
default encoded in two places — here `?? DefaultProjection` in one arm and `case null` in another — is worth
collapsing to one, because it is exactly what lets two arms disagree without any test noticing. Related:
[[unset-field-drives-the-wrong-arm]], [[test-quality-audit-catalogued]].

### A source-text fence must match the IDENTIFIER, not the syntax around it

A structure test that greps source for a forbidden construct is only as good as its pattern. Matching a
*syntactic* form — `"[NativeDisableContainerSafetyRestriction]"` with its brackets — passes anything spelled
differently: the fully-qualified `[Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction]`,
an aliased `using`, or attribute syntax with arguments. The fence reads green while the construct is present.

*2026-09-02.* Caught only because the developer's first RED attempt used the qualified form, the test stayed
green, and they asked why instead of switching to the form that worked. Both spellings compile; only one was
fenced.

**How to apply:** match the **bare identifier** after stripping comments (the strip is what makes it safe) —
an identifier like `NativeDisableContainerSafetyRestriction` cannot appear in compiling C# except as that
attribute, however it is qualified. It is simpler than the bracketed pattern *and* strictly more complete.
Two further rules for any file-scanning test: **enumerate the directory** rather than hand-listing files, or
the fence covers whatever existed the day it was written and silently misses the next addition; and **assert
it found files to scan**, or a moved or renamed directory satisfies every "zero occurrences" claim
vacuously. Related: [[tooth-vacuous-or-overclaiming]].

### A tool that matches nothing usually exits 0 — success and no-op are indistinguishable

A bulk edit that silently changed nothing reports the same exit code as one that worked. Check the *effect*,
never the exit status.

*2026-09-02:* a rename across nine files used BSD `sed` with `\b` word boundaries. macOS `sed` does not
support `\b`, so every substitution matched nothing — and `sed` exited **0** on all nine. `perl -i -pe` does
support it. Caught only because the developer looked at the files afterwards.

This is one instance of a shape that has bitten this project repeatedly, and it is worth recognising as a
class:

| the tool | how it looks like success | what actually proves it |
|---|---|---|
| BSD `sed`/`grep` with an unsupported escape | exit 0, no matches | `git diff` after the edit |
| Burst failing to compile a job | falls back to managed IL, tests still pass | `Library/Bee` artifacts, or a log grep for compile errors |
| the Unity test gate crashing | leaves the *previous* run's results XML in place, looking current | the results file's own timestamp, and new test names present by name |
| a chunk/branch assertion inside `if (…)` | the test passes | a precondition asserting the guard is satisfiable |

**The rule:** for any step whose failure mode is "did nothing", the check must observe the change, not the
command. `git diff` for edits, artifacts on disk for builds, named results for test runs.

### A design doc that *names* its own tooth is the easiest place for a missing tooth to hide

A rule stated as "a structure test greps for X" or "the Editor's check catches Y" reads as settled to every
later reader — reviewer, planner and developer alike. Nobody re-checks a claim that sounds like a report of
existing coverage, so the gap survives exactly as long as the sentence does. This is worse than a rule with
no tooth: the prose actively suppresses the question.

*Epic A (2026-09-02) — four instances, none found by reading the doc:*

- §7 rule 2 said "a structure test greps for both attributes and allows exactly the earcut batch job's
  fields." No such test existed. Found only because a developer needed one of those attributes and asked
  whether it was allowed.
- §7's first qualification said the schedule-time write-write check "is contingent on the JobsDebugger
  toggle… a gate whose debugger is off proves nothing here." Nothing read the toggle — so every
  dependency-edge RED in the epic rested on an unobserved environment flag. (`JobsUtility.JobDebuggerEnabled`
  is readable *and* settable, so this one is a tooth, not a log grep — unlike the Burst-compile hazard next
  to it, which genuinely cannot be seen from inside a test.)
- Stage 1's tooth list kept a chunk-plan tooth after `ChunkPlanJob` was deleted — a tooth pointing at nothing.
- Stages 2–3 kept teeth asserting "the layer produces ≥3 meshes" after chunking was retracted — teeth
  nothing can satisfy, which read as coverage while being unsatisfiable.

The last two share a cause with the first two: a decision was amended in one section, and the sections that
depended on it were not swept. An amendment banner at the top of a section does not reach a reader who lands
in the middle of it.

**How to apply:** when a doc sentence asserts that something is *checked*, name the check — file and test
name — or write it as an obligation ("stage N must add…"), never as a report. When a decision is retracted,
grep the whole doc for what depended on it in the same edit; the banner is not the sweep. And when a tooth's
subject is deleted, delete the tooth in that commit — a tooth whose target no longer exists is
indistinguishable from coverage until someone tries to run it. Related:
[[recorded-limitation-needs-an-observing-tooth]], [[tooth-vacuous-or-overclaiming]].

### `AllTilesSettled()` is TRUE before the first tick — a settle loop that checks first never runs

`TileManager.AllTilesSettled()` returns `_desired.Count == 0 && every loaded tile Built`. `_desired` is
populated by the cover recompute inside `LateUpdate()`, so **before the first tick both collections are empty
and the predicate reads true**. A drive shaped `while (!view.AllTilesSettled()) { view.LateUpdate(); … }`
therefore exits on its first evaluation, never pumps, and never requests a tile.

*2026-09-03.* Cost about half an hour on a new tooth. Its diagnostic read
`guard=0 loaded=0 settled=True drawItems=0` — every number pointing at a drive that never started, while the
assertion that actually fired complained about a mesh count. The failure looked like the production fix under
test was broken.

This is **not** silent vacuity in the existing suite: `ThrottleTests`' equivalent loop is protected by outcome
assertions (`sawPartialTileFrame`, `totalMeshes > totalTiles`) that cannot pass on an empty drive. The cost is
**diagnosability** — a fixture that never starts is indistinguishable from a fix that does not work.

**How to apply:** pump before you test the predicate (`do { LateUpdate(); } while (!settled)`, or a `for` loop
whose body pumps first), and assert a **drive precondition** before any outcome assertion — that a tile was
loaded, or a kick observed. `TileManagerBackgroundRegistrationTests`' step-progression tooth has the right
shape: `Assert.GreaterOrEqual(kickTick, 0, "drive precondition: …")`. A test's drive is a guard like any
other, and the rule for guards applies: assert it was satisfiable.

## Test workflow

- **`dotnet test Tools/core-tests` is STRUCTURALLY blind to every Unity-only file — a green run there is not
  evidence the tree compiles.** The fast project references no `UnityEngine`, and it compiles only the files
  registered in `Tools/core-tests/core-tests.csproj`. So it cannot see, by construction:
  - **Unity namespace collisions.** `MapRenderer.Core.Text.TextAnchor` vs `UnityEngine.TextAnchor` compiled
    clean in the fast runner and failed the Unity gate with `CS0104`. Fixed with a `using` alias.
  - **A missing `using` for a Unity type.** Adding a `NativeArray` return to a test file with no
    `using Unity.Collections;` — fast runner reported **1282 passed**, Unity gate exited **4**, compile
    error, no tests ran.
  - **Any file not in the csproj at all.** `#if UNITY_EDITOR` fixtures under `Visual/` are never compiled by
    it, so a whole test suite can be broken and invisible.

  Three instances during the pitch-alignment epic, from three different directions. Use the fast loop for
  what `AGENTS.md` says — decode/geometry/earcut/projection math — and treat the Unity gate as the only
  instrument that answers "does this tree compile". **Never report a fast-runner pass as a compile check.**


- **Measuring per-frame GC allocation: only NUnit's `Is.Not.AllocatingGCMemory` is trustworthy here; the
  two obvious `System.GC` counters both lie on this Unity Mono runtime.** Verified during the S53b
  re-measurement (2026-06-23):
  - `GC.GetTotalMemory(forceFullCollection: false)` measures *net heap delta*, not allocation traffic — it
    is GC-timing-dependent and swings wildly: the same Entities `Rebuild` loop reported **0** and **~409
    bytes/frame** on back-to-back runs of identical code. (This is the source of the now-retracted "409
    B/frame" Entities figure.) `forceFullCollection: true` only reports *retained* memory, hiding the
    transient churn you are usually hunting.
  - `GC.GetAllocatedBytesForCurrentThread()` returns a **constant 0** on this build — a self-check
    allocating a known 80 KB registered 0 bytes. Any "0 allocations" from it is a broken-instrument
    artifact, not a real zero. **Always canary an allocation API before trusting a 0 from it.**
  - Use `UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory()` (the same instrument the BRG
    zero-alloc tooth uses) — it samples the `GC.Alloc` profiler recorder, so it sees transient churn and is
    immune to GC timing. Caveat: it reports a *count of allocation calls*, not a byte total (and its "But
    was:" actual prints blank here), so it answers "allocates: yes/no", not "how many bytes". For a byte
    figure use `Unity.Profiling.ProfilerRecorder` ("GC Allocated In Frame"). Also: a **single** Tick can be
    alloc-free while a **run of N** Ticks trips the recorder — EG allocates intermittently, so measure over
    many frames or you will under-report. (Beware the `Is` name collision: alias
    `using Is = UnityEngine.TestTools.Constraints.Is;` + `using NIs = NUnit.Framework.Is;`, or instantiate
    `new AllocatingGCMemoryConstraint()` and wrap in `NUnit.Framework.Constraints.NotConstraint`.)

- **A headless camera→RenderTexture readback is vertically MIRRORED vs the on-screen render.** The shipping
  on-screen path renders through URP's intermediate RT and blits to the backbuffer (that blit flips Y); a
  direct `camera.targetTexture` → `ReadPixels` snapshot lacks that blit, so its rows are the vertical mirror
  of what ships on screen. `_ProjectionParams.x` is `-1` in BOTH paths, so a shader CANNOT branch on it to
  self-correct (a symbol shader that lands glyphs upright on screen renders them upside-down in the readback).
  For snapshot tests: on-screen is the ground truth (eyeball it live), and any test asserting absolute
  vertical orientation/position from the readback must un-mirror the buffer first (flip rows), or assert only
  flip-invariant axes (horizontal). `SymbolAtlasOrientationSnapshotTests` un-mirrors then checks glyph
  uprightness + horizontal centering; absolute vertical position is a verified-live eyeball item, not
  machine-checked. (2026-07-07, S20.)

- **Unity batch `-runTests` does not reliably generate/persist `.meta` for new or renamed files.** A new
  `.cs`/`.asmdef` may run once without a committed `.meta`. Force generation with a dedicated
  `-batchmode -quit` import, or delete+recreate the file. Never hand-author a `.meta`.

- **A COLD `Library/ShaderCache` makes GPU-snapshot tests fail deterministically — it is NOT a real flake.**
  In batchmode, an un-cached shader variant compiles *asynchronously* and lands AFTER the first render that
  needs it, so the measuring render draws the wrong thing: fills come back blank (base variant not ready) and
  a `_NORMALMAP` keyword toggle silently no-ops (its variant isn't compiled), so a lit render looks identical
  with and without the map. `allowAsyncCompilation = false` in a `[SetUpFixture]` does **not** fix it (that
  flag is inert in batchmode — verified). Reproduce reliably by wiping the cache
  (`find Library/ShaderCache -mindepth 1 -delete`) then running: `S55ThrottleTests.Tooth_e` (blank coverage)
  and `LitFillSnapshotTests.LitFill_NormalMap_ChangesShading` (flat-vs-normal diff = 0.0) fail every run cold,
  and pass every run warm. This was the whole of the 2026-07-09 "flaky snapshot tests" scare — a per-commit
  verify that reimported the `Library` each commit kept re-cooling the cache; there is no test defect.
  The same trap fires when you **edit a shader**: the cache is non-empty but the changed shader's variants are
  stale and recompile (async) on first use — a full cache is not a *current* one. (A content edit changes the
  variant's hash, so it is effectively a cold/missing variant for that hash.)
  **Fix (in `Tools/run-tests.sh`):** when the shader cache is cold OR stale, run one throwaway warm-up pass
  first — its renders compile the variants and PERSIST them to `Library/ShaderCache`, so the real pass (a fresh
  process) reads them warm and renders correctly. "Stale" is detected with a stamp file
  (`Library/.umr-shader-warm-stamp`, touched only after a full unfiltered run): warm up if the cache is empty,
  never warmed, or any `.shader`/`.hlsl` is newer than the stamp. Warm, unchanged runs — the common case — pay
  nothing; opt out with `UMR_SKIP_SHADER_WARMUP=1`. (Rejected alternatives: `ShaderVariantCollection.WarmUp()`
  — canonical but needs a hand-maintained collection of every shader×keyword; the async flag — inert.
  2026-07-10.)

- **The EDITOR does not reliably rebuild variants when only an included `.hlsl` changes — and batch mode does.**
  Batch-mode tests compile from source every run, so `./Tools/run-tests.sh` can be **green on a change the
  running Editor is not executing**. The two disagreeing is not a contradiction to explain away; it means the
  Editor is stale. Symptom: a shader edit — even a hard `color.rgb = magenta` — has *no visible effect* in the
  Game view. Touching the `.shader` file is **not** sufficient on its own; what worked reliably was **quit
  Unity → delete `Library/ShaderCache` → reopen**. (2026-08-01, S114: cost most of an evening. Three
  successive "the fix doesn't work" reports were all made against stale variants, and each one sent the
  investigation to a different, innocent subsystem.)
  **Method, not just the fix:** every in-scene shader diagnostic must be **self-verifying** — pair the thing
  you are testing with an unmistakable signal that proves the build is live (a colour the previous build could
  not produce), and never reuse a colour between revisions. Two diagnostics in a row that both rendered green
  made "all green" unreadable. And when a maintainer reports "still happening", the FIRST move is to confirm
  the code under test is the code running, before touching the maths again.

- **A straight-road fixture cannot see a corner defect — and "my fixture disagrees with the scene" means the
  fixture is wrong, not the scene.** S114's teeth all render one straight road, so they measured the
  extrusion's convexity beautifully and were blind to the reported symptom, which turned out to involve
  **miter/bisector vertices at a turn under grazing incidence**. A hard-coded constant-width override
  (`lateralUnits = 8.0`) settled in one look what six rounds of derivation could not: the width was exactly
  16 px, so the extrusion path was never the cause. Reach for the crudest override that removes all doubt
  before refining a model. (2026-08-01.)

- **A shader GLOBAL is PROCESS state, so a render fixture that forgets to push one reads another fixture's
  camera.** `DevicePixelRatioSnapshotTests` built its materials through `MaterialFactory` +
  `ZoomStyleApplier` and never through the seam that pushed `_MapFrameMetersPerDevicePixel`, so at zoom 8 it
  rendered against `MetersPerPixel(5.0)` left behind by an earlier fixture in the same batch — a ratio of
  exactly **8.000**, and a styled 16 px road that measured 128 px. **Read the number before theorising about
  it:** 8.000 is 2³, three whole zoom levels, which a projection mismatch cannot produce (globe-vs-Mercator
  at latitude 30 would have been 1.1547 — and that fixture is Mercator anyway). A clean dyadic ratio between
  two readings of one quantity means *stale state*, not *wrong maths*. The structural fix is to push such a
  constant from the object that owns the quantity — here `MapCamera.SyncToCamera` — so a path that builds the
  object cannot forget it; a fixture written next month is not visible to any structural test. (2026-08-02,
  S116.)

- **Decoupling two expressions that shared a wrong number exposes the defect it was cancelling — that is the
  fix working, not a regression to undo.** `widthWorld` and `aaPadWorld` both multiplied
  `MapPixelsToWorld(centerWS, unitDir_WS)`. At a **round-cap pivot** `unitDir_WS` is deliberately zero, the
  probe steps zero metres, `refPx` is 0 and the `max(refPx, 0.1)` clamp returns 51× the true scale — but
  `hairlineScale = widthWorld / max(widthWorld, 2·(2·aaPadWorld))` is a *ratio*, so the blow-up divided out
  exactly and nothing ever read wrong. Giving the width a frame constant left the pad holding the bad number
  alone, and the cap's ink collapsed from 0.75 px to 0.229. The temptation is to re-couple them and go green;
  that restores the cancellation and re-hides the bug. Fix it where it is wrong — a degenerate direction has
  no probe, so take the function's existing behind-camera fallback, which computes exactly the direction-free
  answer the degenerate case wants. (2026-08-02, S116.)

- **A settle predicate is vacuously true on an empty cover — the drive runs zero iterations.**
  `for (f = 0; f < N && !view.AllTilesSettled(); f++) { view.LateUpdate(); }` evaluates the predicate
  *before the first pump*. With an empty cover "all tiles settled" is already true, so the body never
  runs, `LateUpdate()` is never called, and the test measures a map that never built anything — while
  `Assert.IsTrue(view.AllTilesSettled())` passes happily. Use the guard the codebase already has
  (`ThrottleTests.cs`, `MapViewEntitiesBackendTests.cs`, `MapViewSnapshotTests.cs`):
  `for (int f = 0; f < N && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)`. The
  `LoadedTileCount() > 0` conjunct is the whole fix — it makes "settled" mean *settled having loaded
  something* rather than *settled having done nothing*.
  Diagnosing it: a **monotonic** counter reading 0 against a 0 baseline means the code never ran, so
  suspect the drive before the production path. Confirm by asking whether the drive produced a mesh at
  all (`GetTileMeshes(id)` null, `LoadedTileCount()` zero), and by diffing the loop condition against a
  passing sibling — here a tooth two methods away used `kickTick < 0`, which is true at entry, so it
  always pumped once. Every other bare `!AllTilesSettled()` site in the suite was checked and is safe
  (each is preceded by an unconditional pump or an assertion forcing a non-empty cover); the bare shape
  is a latent trap for *new* drives, not an existing bug. (2026-09-04.)
