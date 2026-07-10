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
  `_AaEdgeWidth` were later removed — lines render a hard edge now, `docs/line-antialiasing.md`. The naming
  rule above is unaffected and still binding.)*

- **Per-line transparent-fade AA cannot render a crisp *cased* line — it's a compositing problem, not a
  coverage one, and MSAA doesn't fix it.** A cased road is two stacked transparent draws (casing under, fill
  over; `Queue=Transparent`, `ZWrite Off`). Any shader-space alpha fade on the fill's edge ramps to
  *transparent*, which over the casing **bleeds the casing colour through** → the fill↔casing boundary blurs.
  Inset / outset / straddle all bleed (fade = transparency = show-through); MSAA only cleans geometry
  coverage while the two layers still *blend*, so it gives the same mush at higher cost. There is also a
  hard trilemma for any shader fade: {solid core, unchanged apparent width, antialiased} — pick two. After a
  long exploration this is why edge AA was removed entirely (hard edge + min-width floor kept); the real fix
  is architectural — a **single-pass cased line** (one draw, colour-by-signed-distance, AA only the outer
  silhouette). Full write-up + the parked outset-AA stash: **`docs/line-antialiasing.md`**.

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

## DOTS / Entities Graphics

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

- **`Child` is `ICleanupBufferElementData`, so destroying a transform-parent leaves a cleanup-zombie.**
  `ParentSystem` adds a `Child` buffer to any entity that becomes a parent; because it is a *cleanup*
  buffer, `EntityManager.DestroyEntity` on that parent does not finalize it — the entity lingers (and
  `EntityManager.Exists` still returns true) until a later `ParentSystem` tick removes the component. If
  you maintain your own handle/parent bookkeeping, treat your own dictionary as the source of truth and
  remove the entry on destroy, so the transient `Exists` staleness never leaks into your public lifecycle
  API. Destroy children before the parent (we ref-count layers per tile root and destroy the root only
  when its last layer goes), so no live child is ever orphaned. (Seen: S53b follow-up — per-tile root
  entities.)

- **Don't guard a native container `Dispose()` with `if (x.IsCreated)` — it's a bloat antipattern.**
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
  with Burst. It compiles clean, so it only shows at runtime (cost a full gate cycle in S100's `LineRibbonJob`).
  Keep scratch as `var x = new NativeArray<T>(…, Allocator.Temp)` locals and pass values (not the arrays) into
  helper methods — the pattern `LineRibbonJob`/`LineRibbonJob` follow. Only INPUT/OUTPUT containers
  (assigned before scheduling) belong as job fields.

## Test workflow

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
