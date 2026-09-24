# Lessons learned — engineering gotchas

Generic, hard-won knowledge discovered while building this project: Unity/URP/HLSL/DOTS and the headless
test workflow. These are **engineering** gotchas — knowledge about the code and tooling, not process. Add
a lesson here when it (a) cost real debugging time, (b) is
not obvious from the code, and (c) will recur. Keep each entry tight and actionable.

## Shaders & HLSL

- **A vertex semantic index (`TEXCOORD3`) is a contract with the MESH's vertex layout, not with the
  `Attributes` struct it is written in — so "the first free slot in this pass" is the wrong way to choose
  one.** Unity binds a mesh's `VertexAttributeDescriptor` list to shader semantics by INDEX, globally: a
  mesh that declares `VertexAttribute.TexCoord3` feeds `TEXCOORD3` in *every* pass, whatever else that pass
  does or does not declare. **Why the wrong choice is tempting:** a forward pass mirroring stock URP Lit
  already spends `TEXCOORD0-2` on base UV + static/dynamic lightmap UV, so its custom streams start at
  `TEXCOORD3` — but the ShadowCaster / DepthOnly / DepthNormals passes carry no lightmap UVs, and there
  `TEXCOORD1` looks like the first unused slot. It is not; it is a slot the mesh never fills.
  **Why nothing catches it:** an unbound semantic is not a compile error — Unity zero-fills the stream, the
  same behaviour a builder may *rely on* elsewhere for a deliberately-omitted attribute — so the shader
  compiles, the draw is issued, and the geometry still appears. **The symptom shape is presence that reads
  as success:** a vertex modifier driven by the missing stream contributes nothing, so the geometry renders
  in its un-modified form and a debug view shows it there. **The check:** compare the builder's
  `VertexAttributeDescriptor` list against the `Attributes` struct of *every* pass of the shaders that
  consume it, not just the forward one. (Seen: 2026-09-07 — `StyledFillExtrusionTileBuilder` writes
  `TexCoord3`/`TexCoord4`; forward/GBuffer/unlit read `TEXCOORD3`/`TEXCOORD4`; ShadowCaster, DepthOnly and
  DepthNormals read `TEXCOORD1`/`TEXCOORD2`. `MapVertexModify` got zeroes, computed `elevation = 0`, and
  drew every building at its floor — so buildings entered the shadow map as flat footprints lying in the
  very ground they were meant to shadow, and cast nothing visible. Frame Debugger showed "buildings in the
  shadow map", which was read as proof that casting worked, for hours. Depth-only and depth-normals were
  wrong too, so SSAO also saw flat buildings. Fixed by moving three passes to `TEXCOORD3`/`TEXCOORD4` —
  six characters.)

  A regression tooth for this must be parameterised over **where the value comes from**, or it will not
  discriminate: `ShadowReceiveBisectTests.FillExtrusionCaster_ShadowFollowsTheExtrudedSilhouette` runs a
  baked-stream arm, a uniform-property arm and a mesh-geometry arm. Both of the first two red against the
  old semantics — zeroing `TEXCOORD3` collapses the base/height `lerp` to its base *and* normalises the
  extrude direction to zero, so the uniform path dies with the baked one — while the mesh-geometry arm,
  whose height no vertex modifier touches, stays green. That green control is what makes the pair evidence
  about a vertex channel rather than about shadows working at all. Every earlier fixture missed the bug
  because it raised a primitive cube (height = real geometry) and `_ExtrusionHeight` defaults to 0.

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

- **A fixture that drives a named shader pass directly gets the FALLBACK shader unless URP is the active
  pipeline at query time — and nothing about it looks broken.** `Material.FindPass` + `SetPass` /
  `DrawMeshNow` resolves against whichever SubShader the active render pipeline selects. Our fill/line
  SubShaders are tagged `"RenderPipeline" = "UniversalPipeline"`, so outside URP the only match is
  `Fill.shader`'s `FallBack "Hidden/Universal Render Pipeline/FallbackError"`, which has exactly one unnamed
  pass. **The signature:** `passCount = 1`, `passes = [0:<Unnamed Pass 0>]`, `shaderHasError = False`, and
  `FindPass` returning **−1 for every name, ForwardLit included**. No exception, no error log — a `-1` reads
  as "that pass does not exist", which is the wrong conclusion. `ShaderData` is how to tell the two apart:
  it shows the real multi-pass SubShader alongside the fallback ones, so a `ShaderData` dump that *has*
  ForwardLit while `FindPass` says −1 names the cause exactly. **Fix:** set
  `Shader.globalRenderPipeline = "UniversalPipeline"` for the duration of the fixture, restored in a
  `finally`. *(2026-09-08, during the fill boundary band's pass-level probes.)*

- **Declare a GPU capability with `#pragma require <cap>`, not `#pragma target N`** (cited in code as `shader-require-over-target`)**.** `#pragma target 3.5`
  raises the whole shader model — it maps to OpenGL ES 3.2+, needlessly excluding ES 3.0/3.1 Android
  devices — when the actual capability needed (e.g. `Texture2DArray` / `SAMPLE_TEXTURE2D_ARRAY`) is an ES
  3.0 feature. `#pragma require 2darray` (or `cubearray`, `samplelod`, …) keeps the floor at ES 3.0 while
  still compiling the variant that uses it. **The headless EditMode gate cannot catch a wrong choice
  here:** the Metal Editor backend tolerates a too-low target, so a mistake only shows up in a player
  build on the excluded API level, as the fallback error shader.

- **A new `Map/*` shader that mirrors URP Lit needs a matching editor `ShaderGUI`** (cited in code as
  `map-lit-shader-needs-shadergui`) **(`CustomEditor` in the
  `.shader`), or its `shader_feature`s never sync from the material's Inspector values.** That GUI's
  `ValidateMaterial` derives keywords like `_EMISSION`/`_NORMALMAP`/`_SURFACE_TYPE_TRANSPARENT` from the
  property values the artist sets; without it those keywords silently stay whatever they were compiled
  with, and a player build can strip a variant nobody ever enabled. This is a feature-completeness gap,
  not a rendering bug — a review that reads only the render path can approve a shader missing it.

- **`Material.HasProperty(id)` proves a property is declared in the shader's `Properties {}` block — it
  proves NOTHING about whether it is a live `UnityPerMaterial` CBUFFER member.** A property can be
  declared (`HasProperty` → true) and still be `#define`-shadowed or dropped from the CBUFFER in the
  `_Input.hlsl`, so a style-system bind of it compiles, runs, and changes no pixel. Shipped once: an
  internal `#define _Opacity 1.0` pinned opacity to 1 forever while `HasProperty("_Opacity")` kept reading
  true. A binding-guard test needs both checks — the Properties-block declaration AND the CBUFFER's real
  member list — not just the first.

- **A billboard/world-space vertex offset must respect the projection's Y sign, or it renders as an exact
  vertical mirror.** A clip-space displacement (`clip.xy += off / screenParams * 2 * clip.w`, applied
  after the projection) is immune to the projection's Y sign. A world-space displacement (offset the
  anchor, then re-project) passes the offset THROUGH the projection matrix, whose Y row is negated
  whenever `_ProjectionParams.x == -1` — true both in a headless camera→RenderTexture readback and the
  shipping on-screen path. Multiply the world-space offset's Y component by `_ProjectionParams.x` to
  match. **The tell in a pixel readback:** a mirror keeps the same ink count and height with
  `delta.x == 0`, and the two centroids' screen-y values sum to `SizePx − 1`; a pure scale error looks
  completely different (the count ratio would be `k²`).

- **A winding/orientation test over geometry whose real shape is produced in the VERTEX SHADER (deferred
  extrusion — floor and roof share one stored position, height added as `extrudeUp · t` in the VS) must
  reconstruct the post-shader position before taking a cross product.** Reading raw mesh vertices measures a
  degenerate, flat quad: every triangle's cross product is near zero and gets skipped, so the test reports
  zero triangles checked rather than failing — vacuous, not passing. Second trap: if the reference "outward"
  direction is itself derived from the same geometry, the sign check is relative wearing absolute, and
  passes with both the geometry and the reference inverted together. Ground the reference on an independent
  fixture invariant instead — for a convex footprint, the direction from the shape's centroid to an edge
  midpoint.

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
  forcing repaint, which is why the panel is now off by default. See `docs/telemetry-design.md` § "Why"
  (item 1) and `docs/symbol-label-perf-design.md` § "The memo is structurally dead under continuous motion".
  (2026-07-25, corrected 2026-07-27.)

- **`DynamicGI.UpdateEnvironment()` on WebGPU writes a garbage ambient probe instead of failing.** The sky
  convolution needs a synchronous GPU texture readback, which WebGPU lacks. The console logs "Texture
  Readback is not supported by WebGPU", and `RenderSettings.ambientProbe` then holds garbage
  (coefficients near 1e34). **The symptom shape:** every URP Lit surface renders flat white, while Unlit
  surfaces and symbols keep their colours. Because the garbage depends on memory state, the symptom comes
  and goes with load and build, and a commit bisect finds nothing. **The check:** log the 27 probe
  coefficients, or set a flat probe and see if colour returns. `MapHost.EnsureEnvironmentLighting`
  skips the call on WebGPU and validates the probe elsewhere. (2026-09-25.)

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

- **Batched geometry (Entities Graphics AND hand-packed BRG) does NOT automatically receive the scene's
  ambient light probe the way a `MeshRenderer` does.** A Lit shader that samples SH (`SAMPLE_GI`) still
  compiles and draws, but with nothing feeding the batch's `unity_SH*` constants it always reads back 0 —
  so any surface lit only by ambient (a shadowed face, or a wall the sun never hits) renders pure black.
  This stayed invisible as long as geometry was flat and top-lit by the sun; the first vertical geometry
  (building extrusions) was the first surface to expose it. Fix by binding `RenderSettings.ambientProbe`
  into the batch's SH once, globally — don't chase this as a shadow or AO bug first.

- **`sizeof(NativeArray<T>)` is 48 bytes in the Editor, not 16 — the `AtomicSafetyHandle` rides inside the
  struct under `ENABLE_UNITY_COLLECTIONS_CHECKS`.** A managed array of `NativeArray<T>` handles
  (`new NativeArray<T>[count]`) therefore costs 48 B/slot in an Editor GC measurement, not the 8 a bare
  reference would suggest — a sizing estimate at pointer size can be several times low. For contrast, none
  of these cause a managed allocation on their own: `new NativeArray<T>(n, Persistent|TempJob)` + `Dispose`,
  `IJob.Run()` (Burst or not), and `Mesh.AllocateWritableMeshData` + `Dispose`. So when a Burst-job
  pipeline shows GC, look at the managed container holding the handles, not the handles or the dispatch.

- **A `Persistent`-allocated native container's finalizer CAN safely dispose it from the GC finalizer
  thread, but `GC.Collect(); GC.WaitForPendingFinalizers();` does NOT reliably trigger that finalizer.**
  The identical code and command finalized cleanly on one run and never ran the finalizer at all on the
  next — Mono's conservative stack scanning can keep the object rooted. A finalizer-backed leak detector
  is a real diagnostic (it fires whenever the GC eventually gets to it) but cannot back a deterministic
  pass/fail test.

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
that the test failed: read the failure message and check it is the one you aimed at. See "RED-verify: inject
the real defect, not a model of one" below for the matching rule on failure counts.

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
production case and name it. See "A green tooth can lie in two ways — check which one before trusting it"
below.

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
collapsing to one, because it is exactly what lets two arms disagree without any test noticing. See "An
oracle or fixture can make a tooth blind without ever going red" below for the general shape of this gap.

### Four of the fill shader's six passes never rasterise a fill fragment in the shipped configuration

The entry above says to name the configuration. This is the worked example that cost the most: an assumption
made twice during the fill-antialiasing epic — that a change to the fill shader's depth-writing passes was
observable in a rendered frame — when none of them runs. Measured 2026-09-08, not inferred:

| pass | why it never rasterises a fill fragment |
|---|---|
| DepthOnly | URP builds the depth prepass with `RenderQueueRange.opaque` — `UniversalRenderer.cs:356` |
| DepthNormals | same — `UniversalRenderer.cs:357` |
| ShadowCaster | `FillRenderLayer.cs:57` is `CastShadows => Off`, and the BRG backend drops those slots |
| GBuffer | the renderer asset is `m_RenderingMode: 2` (Forward+), so deferred never runs |

Fills render at custom render queue 3000, outside the ≤ 2500 opaque range those two prepasses filter on.
**And note the sub-claim that is NOT true**, because it is the one a reader supplies for themselves:
DepthOnly is *not* disabled on the committed fill materials — both carry `disabledShaderPasses: []`. The
`BaseShaderGUI.cs:1142` call that would disable it runs in the **Inspector GUI**, not at runtime, and the
committed asset state is what ships.

**How to apply.** Two consequences, and the second is the one worth carrying. First: a change to those
passes cannot be RED-verified against a frame — the instrument has to be structural (a source-level fence),
and a rendered tooth for it is only buildable once a fill becomes opaque-mode or casts shadows, at which
point whoever lands that owns the tooth. Second: **every row here is a configuration, not a construction.**
Flip `CastShadows`, move a fill into the opaque queue, or switch the renderer to deferred, and the pass goes
live with whatever was written under the assumption nobody would run it. See "A recorded limitation needs a
test that would fail if it stopped being true" below.

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
vacuously. See "A green tooth can lie in two ways — check which one before trusting it" below.

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
| a gate killed with the agent turn that launched it | no results XML and a log that simply stops — indistinguishable from a run still in progress, so you keep waiting | the process table (no Editor alive) together with the log's mtime |
| a chunk/branch assertion inside `if (…)` | the test passes | a precondition asserting the guard is satisfiable |
| `git commit -a` with a new file in the change | commits, and the gate that just passed was run on the working tree | `git status` after committing — `-a` stages modifications and deletions, never untracked files |
| a shell function whose name shadows a real binary (`strip`, `test`, `time`) | the pipeline runs and the comparison reports IDENTICAL | a floor check on the input: a diff of two EMPTY files is also identical |
| a tree-freeze fingerprint over a stage that adds a `.cs` | fires SOURCE DRIFT during your own gate, because Unity writes the `.meta` mid-run | hash source only; a `.meta` appearing is the gate, not an agent |

**The rule:** for any step whose failure mode is "did nothing", the check must observe the change, not the
command. `git diff` for edits, artifacts on disk for builds, named results for test runs.

**The second rule, for measurements rather than actions:** a measurement that returns a plausible number
is indistinguishable from a correct one. Every row above except the first is a *measurement* that answered
confidently and wrongly. What separates them is never more care — it is a second reading that must agree.
Run a control probe (one input you know is live, one you know is not) and report both; put a floor under
any count that could come back empty for the wrong reason; and when two of your own measurements disagree,
that contradiction IS the finding — reconcile it before reporting, never pick the one you prefer.

### A design doc that *names* its own tooth is the easiest place for a missing tooth to hide

A rule stated as "a structure test greps for X" or "the Editor's check catches Y" reads as settled to every
later reader — reviewer, planner and developer alike. Nobody re-checks a claim that sounds like a report of
existing coverage, so the gap survives exactly as long as the sentence does. This is worse than a rule with
no tooth: the prose actively suppresses the question.

*Epic A (2026-09-02) — four instances, none found by reading the doc:*

- Rule 2 of `docs/job-scheduling-design.md` § "Safety — making the Editor's check sufficient" said "a
  structure test greps for both attributes and allows exactly the earcut batch job's fields." No such test
  existed. Found only because a developer needed one of those attributes and asked
  whether it was allowed.
- That section's first qualification said the schedule-time write-write check "is contingent on the JobsDebugger
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
indistinguishable from coverage until someone tries to run it. See "A recorded limitation needs a test that
would fail if it stopped being true" and "A green tooth can lie in two ways — check which one before
trusting it" below.

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

## Performance measurement

- **A sudden, large per-frame cost in a `.Run()` Burst job with no matching code change is usually the
  Editor's Jobs ▸ Burst ▸ Enable Compilation toggle switched OFF, not a regression.** `.Run()` still
  executes with Burst off — silently, as plain managed IL — so nothing errors, it just gets ~10-15× slower,
  and the profiler marker name (`ExecuteJobFunction.Invoke()`) is identical either way. Check
  `Unity.Burst.BurstCompiler.Options.EnableBurstCompilation` before diagnosing the code. This is an Editor
  session setting, not committed code, so it never affects a player/WebGL build (Burst AOT-compiles there
  regardless).

- **A lambda that captures only method-scoped (loop-stable) locals is cached by the compiler to ONE
  delegate per method call, not one per iteration — only a capture of a loop-local variable allocates per
  iteration.** `Array.Sort(arr, (a,b) => f(x,a,b))` inside a `for`-loop, with `x` declared outside the loop,
  allocates its delegate once, reused every pass. Before treating a closure as a per-iteration GC cost, ask
  which kind of local it captures, and RED-verify the claim (inject the closure form, confirm the meter
  goes red) — a `new T[]`/`new List<>` sitting next to the lambda is far more often the real per-iteration
  allocation.

- **An Editor/dev-build profiler delta on managed code that touches native containers overstates the
  release-build win — it is an upper bound, not the number.** `ENABLE_UNITY_COLLECTIONS_CHECKS` (the
  safety-handle bookkeeping on every `NativeList.Add`/`NativeArray[i]`) is on in the Editor and dev builds,
  stripped in release players. When picking a perf target from an Editor capture, prefer a marker whose
  cost is real work (math, hashing, an algorithm) over one dominated by per-element container access or
  dictionary churn — the latter's release-build prize is mostly gone already.

- **State a performance cost in absolute ms/frame, not only as a ratio — a true multiplicative factor can
  still be a rounding error.** An "8×" scan cost turned out to be ~50 µs/frame against a 7.67 ms budget (a
  once-per-frame linear pass), because the ratio was real but the baseline was tiny. Compute the absolute
  number before calling anything a regression.

- **Before architecting around a hot spot (moving it off-main, deferring a frame, making it incremental),
  ask whether the cost is real WORK or accidental OVERHEAD.** Architecture relocates work and buys latency
  to do so; removing overhead deletes the cost and costs nothing. Two costs that looked like they needed a
  whole async redesign turned out to be a synchronous Burst job (the actual cost was per-element
  safety-check overhead in a managed loop, not the work itself) and a one-line fix (a decay map retaining
  zero-value entries, inflating a lookup from hundreds of keys to tens of thousands). Overhead tells:
  per-element managed container calls, an unboundedly growing collection, or work over a set that should
  have been pruned first. When a cost-attribution guess is wrong twice, stop guessing — delete the
  candidate cost and re-measure rather than building a third model.

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
  - **Two further calibration points, for figures below what the constraint above can size.**
    `GC.GetTotalMemory(false)` deltas ARE trustworthy, but only for large per-op allocations (roughly
    ≥100 KB): warm the body once, divide by N, confirm `GC.CollectionCount(0)` is unchanged across the
    window, and calibrate a known allocation in the same test. It measures *retained* heap, so it
    quantises to the runtime's heap-expansion granularity — it can read byte-identical across
    configurations that genuinely differ, or exactly 0 when an allocation fits existing free space; never
    use it to rank components against each other. For counting or ranking, read the same recorder
    `Is.Not.AllocatingGCMemory()` uses, for a count instead of a verdict:
    `Recorder.Get("GC.Alloc")`, filtered to the current thread, `sampleBlockCount` after N calls. It
    counts allocation *events*, is exactly proportional, and has no noise floor — but a window that
    collects zero samples reports the PREVIOUS window's value, not a fresh zero, so never rest a
    conclusion on one zero reading; corroborate by additivity across configurations. Separately, an
    ablation-differencing measurement over a WHOLE pipeline run has its own, much higher resolution floor
    — one such harness could not resolve components below ~50 KB/tile (readings quantised to a fixed
    step, or landed on exactly 0, for anything smaller). To size a smaller component, write a dedicated
    probe that repeats just that allocation until the total window clears a few MB, and expect the probe
    to read 20-50% under a structural `sizeof` estimate — bracket the true size between the two rather
    than trusting either alone.

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

- **Never run `./Tools/run-tests.sh` through a pipe (`| tail`, `| grep`).** Bash without `pipefail` reports
  the LAST command's exit code, so a piped invocation reports `tail`'s or `grep`'s status, discarding the
  one code the script goes out of its way to make trustworthy. Redirect to a file and read the file
  (`./Tools/run-tests.sh > Logs/out.txt 2>&1; echo $?`), or use `${PIPESTATUS[0]}` if a pipe is unavoidable.

- **Landing a test with `[Ignore]` (or `[Explicit]`) turns the gate red even with zero real failures.**
  NUnit sets the run-level `result` to `Skipped:Ignored`, and the gate treats any `result != Passed` as
  failure regardless of `failed=`. The "land it ignored now, un-ignore it later" pattern does not work
  here — land the tooth in the stage that makes it pass instead.

- **`./Tools/run-tests.sh` can exit 133 (SIGTRAP) even when every test passed — a flaky Unity Editor
  teardown crash on macOS, not a real failure.** Confirm it's benign before treating it as one: the
  results XML must be THIS run's (a fresh `end-time`, `result="Passed"`), and the crash stack in
  `Logs/test-run.log` must be pure Editor shutdown (`SceneTracker::Update`, the AppKit/CFRunLoop event
  loop) with no job, dispose, or thread-teardown code from the change under test in it. It is
  non-deterministic — the same tree can exit 0 on one run and 133 on the next.

- **A gate whose wall clock is wildly longer than usual is probably the machine sleeping, not the run
  hanging.** On battery, macOS suspends and resumes between test items; the run finishes correctly but the
  elapsed time between launch and results is unbounded. Read the NUnit `duration=` attribute in the
  results XML (test time, not wall time) and compare log size against a known-good run before diagnosing a
  hang or a runaway loop. Wrap a long unattended run in `caffeinate -dimsu ./Tools/run-tests.sh` to avoid
  it.

- **A batch run stuck retrying `Failed to handshake to channel: LicenseClient-…`, or one that dies after
  ~75s on a licensing failure and writes no results, is usually an orphaned `UnityLicensingClient`
  process — not an expired license.** An interrupted batch run or a force-quit interactive Editor can
  leave its licensing helper reparented to `init`, still holding the named pipe the next run's client
  needs. Its executable path looks legitimate (it's still under the Editor's or Hub's own install) — the
  discriminator is the process's parent PID and start time, not its path. Kill it; Unity/Hub relaunches a
  fresh one on demand.

- **A headless run that dies partway with `fatal error in the mono runtime`, under a Burst
  `CreateTargetMachine` stack right after a `[Licensing::Module]` handshake failure, is an expired or
  unavailable batch Burst AOT license — not a code defect.** Scripts still compile clean in this case;
  Burst dies before the tests run. Fix: open the Unity Editor interactively once (refreshes the cached
  license), quit, then re-run the batch gate.

- **`ProfilerRecorder` is a fixed-capacity ring buffer (commonly 64) — once more samples accrue than the
  capacity, `Count` is no longer a safe index bound for `GetSample(i)`.** A read loop bounded by
  `recorder.Count` throws `IndexOutOfRangeException` once the ring wraps, and because one throw takes down
  every assertion after it in the same test, the failure presents as several unrelated-looking reds rather
  than one. It fires only once enough frames have been sampled to wrap the ring, which is why it reads as
  random. Clamp with `math.min(Count, Capacity)` against the recorder's actual capacity, not a duplicated
  literal.

- **Off-main work that completes on wall-clock time (a `ThreadPool` mesh/symbol build) cannot be settled by
  a bare `yield return null` in EditMode — the yield is instantaneous there, so the ThreadPool never gets
  real wall-clock and the wait starves.** Two settle strategies actually work: an inline synchronous drain
  helper that blocks on the in-flight work and consumes it (deterministic, but it consumes — it can't test
  an unconsumed backlog, and if it's silent for one kind of work by construction it can't exercise that
  path either); or move the test to PlayMode and `yield return null` across real frames, which gives the
  ThreadPool genuine wall-clock. `Thread.Sleep` as an EditMode settle-poll is banned outside the couple of
  cases structurally forced into it — it is slow and still flaky.

- **A test that releases a parked worker without awaiting it can leave that worker running past the test's
  own return — mutating a process-wide counter during the NEXT test and producing a failure in an
  unrelated file.** EditMode tests share one process and one static-counter space. Before returning from a
  test that unblocks a background worker, wait for it to finish (or don't dispose the gate it's waiting
  on) — the eye-catching hazard in that shape (disposing a gate with a concurrent waiter) is usually not
  the one that actually causes the contamination; the missing wait is.

- **PlayerPrefs is a single per-project store shared between batch test runs, Play mode, and the
  interactive Editor.** A test that writes or clears PlayerPrefs under a production key namespace destroys
  the user's real settings every time the gate runs. Any test touching PlayerPrefs must inject or
  namespace-isolate its own key prefix — never write directly under a production key.

- **A visual/coverage test helper that samples the "background" color from a FIXED pixel location can be
  sampling actual geometry instead, silently.** If a fixture's content happens to reach that pixel, every
  coverage value computed against that background is wrong, with no visible symptom in the rendered
  image — and because it depends on camera pose, a fixture clean at one heading/tilt can go corrupt at
  another. Any coverage-based visual test needs an explicit background-purity precondition, not a glance
  at the render.

- **Renaming, moving, or deleting a `docs/*.md` file that a source comment cites can fail the build.** A
  structure test resolves every such citation against the tracked file list, so "docs-only" is not
  automatically gate-exempt — only a prose-only edit is. A rename/move/delete of a cited doc needs the
  gate, and the resulting failure will look unrelated to the rename until you read the message (it names
  the orphaned citation). Grep the code for a doc's basename before renaming or splitting it.

- **A gate check of the form "zero `CS1574`/`CS0419` warnings" can be vacuous by construction.** Those
  diagnostics (a dangling `<see cref>`) only fire when `GenerateDocumentationFile`/`/doc` is enabled; a
  project with no `csc.rsp` never turns it on, so Roslyn never parses `<see cref>` targets at all, and the
  count reads zero whether or not any reference is broken. A whole-codebase `<see cref>` token grep is a
  floor, not a guarantee, either — it only proves the named symbol exists somewhere, never that a specific
  cref resolves to the member it names.

### `ShadowReceiveBisectTests` only measures correctly in a FULL run — a filtered run reds 16 of 19

Measured 2026-09-17 at `025b4309`, twice, identically.

```
./Tools/run-tests.sh EditMode 'ShadowReceiveBisectTests'   → total=19 passed=3  failed=16
./Tools/run-tests.sh                                       → all 19 PASS
```

Every failure reports **zero** darkening between the shadows-OFF and shadows-ON frames — including
`StockUrpLit_GroundDarkensUnderACastShadow`, which uses a stock URP Lit material and none of this repo's
shaders. That is the tell: a fault in our shaders cannot red the stock-URP rung, so the cause is the
render environment, not the code under test. The three cases that survive a filtered run are exactly the
ones that *search* for a working configuration (`MapScale_ShadowKnobSweep_FindsAKnobThatRestoresTheShadow`,
`MapScale_AtTheAppsRealAlbedo_…`, `MapScale_UnderTheAppsSkyboxAmbient_…`) rather than asserting a fixed one.

**Why it matters, and it is not academic:** the natural way to iterate on a shadow tooth is to filter to
the fixture, because a full run costs minutes and a filtered one costs seconds. Do that and you are handed
16 failures that have nothing to do with your change. The trap is that the run looks like a clean,
well-scoped measurement — `result="Failed(Child)"` with a plausible per-case message each time — so it
reads as "I broke something" rather than "I measured wrong".

**The rule: RED-verify anything in this fixture with a FULL `./Tools/run-tests.sh`, never a testFilter.**
Budget the wall-clock accordingly; there is no fast loop for this fixture.

`Assert.Inconclusive`/`NoGpuContext` does NOT fire here — a GPU context is present and
`PaintColorRenderTests` renders 4/4 green in the same headless batch session. So the usual "no GPU in
batch mode" explanation is ruled out, and reaching for it will send you the wrong way.

The precise missing precondition has not been isolated; what is established is the discriminator above
(full run green, filtered run red, stock-URP rung among the casualties).

**This is not confined to `ShadowReceiveBisectTests`.** The same artefact appeared on
`Visual.DataDrivenFillSnapshotTests.DataDriven_ConstantControl_ProducesSingleCluster` — a lit FILL test
with a directional light, no shadows involved — so the common factor looks like lit content rather than
the shadow path. Treat a filtered run as an invalid instrument for any lit visual fixture, not just this
one.

The cheap experiment that tells "I broke it" apart from "I measured wrong", and it costs one minute:
`git stash push -u` to the last commit, confirm `git status` is clean, run the single test filtered, and
see whether it still fails. It did — on code neither the change nor its author had touched:

```
pristine committed code + FULL run      → PASS
pristine committed code + FILTERED run  → FAIL   (deterministic, repeated)
```

A failure that reproduces on a pristine tree is never your change. Do this before debugging a
filtered-run red, not after.

- **An intermittent multi-second tile-load stall in the Editor that clears for the rest of the session, with
  framerate unaffected, is a Burst SYNCHRONOUS compile on a background worker, not the network.**
  `[BurstCompile(CompileSynchronously = true)]` blocks the calling thread until Burst finishes compiling that
  job type, on its first schedule. Mesh/decode jobs run on a `ThreadPool` worker rather than the main thread,
  so the Editor keeps rendering while tiles simply stop arriving — it reads as "loading is slow", never as a
  freeze. A generic job compiles per type argument, so a projection/comparer combination first reached at a
  new zoom can trigger a fresh blocking compile mid-session: an abrupt zoom change reveals several at once
  and stacks them, while a slow pan hides the same cost one job at a time. Confirm by clearing
  `Library/BurstCache` (or touching a file in the jobs assembly) and re-entering play: the stall returns
  once, then disappears for the rest of the session. Editor-only — a player build is AOT-compiled, so none
  of this ships.

- **A `private` `[SetUp]`/`[TearDown]`/`[OneTimeSetUp]` method declared on a base test-fixture class
  silently never runs.** NUnit discovers these by reflection and does not see a private one on a base
  class — no error, no warning, the hook is simply never invoked. A fixture that looks like it saves and
  restores global state then restores nothing. `protected` is the minimum visibility that works; keep the
  method non-virtual if the point is that a subclass cannot shadow or skip it — visibility was never what
  bought that. Measured a 3-level hierarchy, two tests per fixture: `private [SetUp]` on the base ran **0**
  times, `protected [SetUp]` ran **2** times (once per test) — in both standalone NUnit 3.14 and the NUnit
  3.5.0.0 Unity vendors through `com.unity.ext.nunit`. NUnit runs the attributed hooks at every level of the
  hierarchy, base-first on setup. Guard against a silent regression with a test that asserts the base
  hook's effect is visible from inside a test body (`VisualTestFixtureDiscoveryTests`, UMR-176).

- **An unrestored `QualitySettings`/`RenderSettings` write in a batch-mode test escapes the process into
  `ProjectSettings/*.asset`, a TRACKED file.** Unity serializes `QualitySettings.SetQualityLevel(...)` to
  `ProjectSettings/QualitySettings.asset` even in batch mode, so a visual test that forces the quality
  level (or the ambient recipe) and does not restore it does more than leak into the next test — it
  modifies the repository, and every later run reads the changed value as the project default. This is why
  the restore in a render-state fixture is load-bearing, not tidy, and it is invisible from reading the
  test body alone. Canary: after running the visual suite, `git status` shows nothing outside `Assets/`
  (UMR-176 — a RED run with the restore deliberately disabled left `m_CurrentQuality: 1` at `0` in
  `ProjectSettings/QualitySettings.asset`).

- **`[UnitySetUp]`/`[UnityTearDown]` on a derived fixture run OUTSIDE a `[SetUp]`/`[TearDown]` pair on its
  base, not nested inside it — inverting NUnit's usual base-first rule.** Measured ordering, sync hooks on
  the base class and coroutine hooks on the derived fixture:
  ```
  [UnitySetUp]    derived
  [SetUp]         base
  test body
  [TearDown]      base
  [UnityTearDown] derived
  ```
  The two DO coexist — a coroutine fixture can derive from a synchronous base — but the derived coroutine
  setup cannot rely on the base having applied anything yet (it runs first), and the derived coroutine
  teardown cannot rely on that state still being applied (the base already restored it). A `[UnitySetUp]`
  that pumps frames to settle a scene settles it BEFORE the base's render state is applied; a
  `[UnityTearDown]` that samples a final frame sees already-restored state. Where a coroutine fixture needs
  base state applied around it, put that state's own hook on a `[UnitySetUp]`/`[UnityTearDown]` too, or make
  the coroutine fixture a sibling base rather than a subclass (measured once, UMR-176, but the mechanism —
  Unity's coroutine hooks wrapping the sync chain rather than nesting in it — is deterministic).

- **A statement that provably never executes can still be required for the build.** C# definite-return
  analysis does not know that `Assert.Inconclusive` (or any other always-throwing call) throws, so a
  `return <expr>;` sitting after one is unreachable at runtime AND may be the only thing making its method
  satisfy CS0161. Deleting it on the reachability argument alone compiles by luck of the corpus, not by
  construction.

  Surfaced while removing 104 unreachable returns after `Assert.Inconclusive` (UMR-176): a scanner matched
  `return <expr>;` as well as bare `return;`, justified correctly on runtime grounds, with no check that the
  enclosing method retained a terminal return. Exactly one site in the tree had that shape
  (`LayerOcclusionTests.GpuContextInconclusive`), and it was safe only because its `return false;` sits
  outside the `try`/`finally` and was untouched.

  A deletion argued from "this never runs" needs a SECOND argument about control flow. A brace-balance check
  does not catch this — the braces stay balanced either way. And when removing a guard would leave a method
  with no exit value, that is a signal the guard was load-bearing for something other than the condition
  being removed — surface it, do not add a replacement return to make it compile.

## Verification and test design

A test can pass and still protect nothing: it can be blind to the defect it claims to catch, or measure a
different thing than its name says. None of this is caught by running the gate, because the code and the
test agree while both are wrong. The entries below are ways that happens, found the expensive way, kept here
so the next test does not repeat one.

### RED-verify: inject the real defect, not a model of one

**RED-verifying** a regression test means injecting the actual bug it exists to catch and confirming the
test fails in the exact predicted way, then reverting — not simulating the defect analytically and trusting
the model. A green test does not prove it guards what its name or comment claims; only an injected defect
does. Aim the injection at the arm you are actually unsure of, not at the whole suite — a global inversion
trips the well-covered path and proves nothing about the one in doubt.

- **Prove the injection landed before reading its result.** A `for f in $FILES` loop in `zsh` does not
  word-split an unquoted variable the way `bash` does — it can run once over the whole list as a single
  filename, change nothing, and still report success. A green run under an injection you believe you made
  looks identical to an injection that never applied. Check `git diff --stat`, or grep for the token you
  introduced, before trusting the run — and be most suspicious exactly when the result confirms a pattern
  you already expect.
- **In a RED-verification table, the failure COUNT and the NAMED failures must reconcile exactly.** A row
  reporting "6 failed" that names five tests has one unaccounted failure, and the gap silently weakens
  whatever the row is used to support. Capture failed test names from the results file, not just the count;
  when the conclusion is going into a permanent comment, re-run the injection to close the gap rather than
  writing it off as a likely flake.
- **A tooth with several assertions goes RED on the FIRST one that throws — that verifies only that
  assertion, not the tooth.** Read the failure message and name the clause that actually fired; every later
  clause got no evidence. A later clause can even be structurally unreachable — if an earlier guard always
  throws first on any input that would trip the later one, no injection can ever verify it, and recognizing
  that is a finding, not a chore.
- **A tooth added to close a coverage gap must be RED-verified by an injection that reds ONLY the new
  coverage.** Flipping a shared final result (negating the whole comparison, say) reds the old corpus too,
  so it proves the tooth fires, not that the extension closed anything. Ask: would this injection still red
  if the extension were reverted? If yes, it is testing the mechanism, not the extension — find a narrower
  one.
- **A flag tested in two places that gate each other cannot be RED-verified by removing just one.** Deleting
  it from a skip/continue check alone, or from the write alone, can be observably identical, because the
  other site still masks it — only removing both reds. Before calling a guard's injection inert, grep every
  site that tests the flag.
- **RED-verifying that a field is part of a composite dictionary key must drop it from BOTH `Equals` and
  `GetHashCode`.** `Dictionary<K,V>` only calls `Equals` between keys that already hash to the same bucket —
  removing the field from `Equals` alone leaves two keys differing only in it in different buckets, so
  `Equals` is never reached and the test stays green for the wrong reason.
- **A zero-alloc test's RED-verify injection must make the extra allocation ESCAPE, or the JIT dead-store-
  eliminates it.** Boxing into an unused local (`object _ = value;`) before the real call has no observable
  effect and is legally elided — the test stays green, and that reads as "this test is vacuous" when it is
  the injection recipe that is a no-op. Force it to escape (`GC.KeepAlive(box)`, a return, a field write)
  before trusting the result.
- **When one change both fixes a defect at several sites and adds the regression test that would have caught
  it, write the test FIRST.** Fixing the sites first consumes the population the test needs to observe — by
  the time the test is due, there is nothing left for it to catch, and the only ways back are destructive
  (revert the fixes) or dishonest (claim a RED that never ran). Land the test, watch it report every site,
  then fix them down to zero.

### A green tooth can lie in two ways — check which one before trusting it

A test can pass for two unrelated reasons that both defeat its purpose: it cannot DISCRIMINATE at the values
it happens to use, or it claims a guarantee its assertions do not actually check.

- **Vacuous at the chosen values.** A test built to catch `v * (1/d)` written where `v / d` was meant used
  device-pixel-ratio values where the two are bit-identical (1, 2 — a power-of-two reciprocal is exact) and
  common resolutions that also happened to agree. It would have passed against the exact bug it existed to
  reject. When a test's justification is a numeric property, compute the discrimination before trusting it —
  name witness values and confirm they actually differ.
- **Over-claiming what it pins.** A test can build the object it asserts on AFTER the operation it claims to
  pin has already returned, so reordering steps INSIDE that operation leaves it green. Ask where the
  observation is taken relative to the thing claimed to be pinned.
- **Presence, count and compilability are invariant under relocation — only a hash catches it.** Restoring a
  deleted block with a plain string replace can prepend instead of restoring the original site, if the
  search string used to mark "deleted" was empty. The file then contains every expected token, exactly once,
  and compiles — to the wrong behavior. Hash a file before an injection and compare hashes after restoring;
  never rely on a `grep` alone to confirm a restore.
- **Membership without one-to-one matching lets every actual collapse onto one expected value.** "For each
  actual, assert it matches SOME expected" is satisfied by four vertices that are all the same point. Add an
  independent shape invariant the positions alone cannot fake (non-zero area, a bounding box, a winding
  sign), or consume matches one-to-one.
- **Right value, wrong element survives RED-verification.** Injecting the value defect a test checks for
  still reds it even when the fix (or the bug) landed on the wrong one of several similar elements — the
  test answers "is the arithmetic right", never "is this the element it belongs on". Add a test pinning
  identity or region membership, not just magnitude, whenever a change targets one of several similar parts.
- **A RED row is evidence only for the assertion that fired.** Everything after it, in the same run, got no
  evidence — the run stopped before reaching it. After a RED, ask of every assertion with no row naming it:
  what defect would reach THIS one specifically, past every earlier assertion?

### A recorded limitation needs a test that would fail if it stopped being true

A limitation written into a design doc is not thereby verified — recording it more than once, or having more
than one reviewer sign off on it, adds confidence but no evidence. Ask of any recorded limitation: **which
test would go RED if this stopped being deliberate?** If the answer is none, the limitation is
indistinguishable from an undiscovered defect, and the wording should say so rather than imply it is
checked. Suspect a limitation whose justification is only an analogy to another subsystem's rule — the
discriminator that made the rule right there may not hold here — and treat "no test can see this" as a
finding to write down, not a footnote to skip past.

### Other ways a tooth can pass without protecting anything

- **A sequencing change can disarm a test without weakening any assertion.** Extra ticks, reordered phases
  or new setup can destroy a test's PRECONDITION while every expected value stays correct — a version
  counter that increments per call can reach the same number through an extra tick as through the bug it was
  meant to catch, and a "first frame" assertion can pass only because nothing was built yet. Reviewing "was
  any assertion weakened" is not enough; check that each touched test's precondition still holds.
- **A test repointed to follow moved code can keep its name, its assertions and its pass, and still stop
  covering the thing it existed to cover.** A source-scraping test anchored inside the type it tests, then
  re-anchored after an extraction to a callee, can turn "X calls Y" into "Y contains Y's own body" — a
  tautology that never reds again. Checking that the test still runs and still passes proves the run is
  fresh, not that it still observes anything; ask of every relocated test "what edit made this RED before,
  and does that edit still make it RED?"
- **A structure fence that grows a "scope note" explaining why some code is legitimately outside its reach
  has usually just been defeated.** Code moved to a location the fence's extraction anchor cannot see, with
  a comment claiming the split was structurally necessary, is a common way to satisfy a forbidden-pattern
  check while still doing the forbidden thing. Read the fence's assertion MESSAGE (the invariant it
  protects), not just its predicate (its reach) — ask what edit would pass today that would have failed
  before the note was added. A fence naming a FILE fails the same way when the type spans files (a partial
  class): fence on the identifier's total production footprint, not one file's contents.
- **A structure test matching source as plain text needs different matchers for what it forbids and what it
  requires.** A substring match on a FORBIDDEN token fails loud on an innocent false positive — annoying,
  safe. The same match on a REQUIRED token fails OPEN: a rename that makes the token a substring of an
  unrelated identifier can satisfy the precondition from the signature line alone, silently voiding the
  whole test. Anchor required tokens with word boundaries; forbidden tokens can stay permissive.
- **"Measure the port's divergence and record it as the test's bound" produces a test no bad port can
  fail** — whatever the port emits becomes the accepted figure. Pre-commit the ceiling you would refuse
  before measuring, not after. Real Burst-vs-managed rounding on ported transcendental math sits at a few
  ULP; a genuine transcription error sits several orders of magnitude higher, so a ceiling anywhere in
  between is safe and never fires spuriously. Name the ULP domain too — the stored float stream and the
  double intermediates give very different numbers for "the same" divergence.
- **Pick a fix site DOWNSTREAM of where the RED test injects its input, never upstream of it.** A fix placed
  above where the failing test constructs its data is invisible to that test — the only way to make it green
  is to edit the test, which re-bakes it into asserting the very convention it was written to falsify. Trace
  which code sits between the test's construction and its observable, and choose the fix site from those; if
  the only single site is upstream, use a shared helper called from every path instead.
- **When a design doc claims a property the code does not hold, assert the DEFECT, not the property.** The
  property would be red on arrival, and `[Ignore]`/`[Explicit]` reds this gate too (see "Landing a test with
  `[Ignore]`" above). Assert the measured wrong value instead, with a failure message that says what to do
  when it goes red:
  ```csharp
  Assert.Greater(countAtTilt60, 2 * countAtTilt0,
      "KNOWN DEFECT: tile count should stay roughly constant under tilt; today it is far higher. " +
      "When this assertion FAILS, replace it with Assert.LessOrEqual and delete this message.");
  ```
  Green today, red the moment someone fixes it — and the fix instruction lives where the next developer
  actually reads it. Calibrate the threshold from a measured half-fix, not a round number, so a partial fix
  cannot report the property restored.

### An oracle or fixture can make a tooth blind without ever going red

- **A parity oracle proves two paths agree — it proves nothing about code both paths execute.** If the
  change under test lives in code shared by both arms, they move together, agree, and pass structurally
  rather than by measurement. Before naming an oracle as a change's test, name the code the change touches
  and ask what the oracle's reference side executes — if the reference runs the changed code too, it is
  blind. Rank candidate references by whether they are a genuinely different implementation, untouched by
  the diff, and able to say which quantity diverged.
- **A corpus-sweep oracle only tests the code paths the fixture's DATA actually drives into.** A filter or
  branch keyed on a property the fixture never populates (a present-key vs. absent-key case, a first vs.
  last scan slot) can sit unreachable through thousands of comparisons, and a broken branch still sweeps
  green. Add a synthetic-fixture test that constructs the exact shape the branch needs, and RED-verify it
  against the real defect rather than an incidental one.
- **Check whether the fixture builds the object a derivation actually describes — not whether the maths is
  right.** A formula derived against an idealized geometry (a circle, a linear projection, a symmetric
  bracket) can be tested against a fixture that builds something else (a low-facet polygon, a perspective
  projection, a one-sided bracket), and the two disagree exactly where they differ. Check the fixture's
  builder defaults (an unpassed parameter silently changes the shape), the space every constant lives in,
  and whether the test is posed where the candidate formulae would actually disagree — not at a value where
  they happen to produce the same number.
- **A snapshot golden that moves is not by itself proof of a regression — check how the fixture manufactures
  its input first.** A test can build its own fixture by borrowing an unrelated production API as a
  quad/data factory, so a change to that API's defaults moves the golden even though the path the test
  claims to guard is untouched. Before re-baking or reverting: predict the delta from first principles
  before measuring, check whether every dimension (not just position) moved as a pure translation would,
  read the whole result rather than the first component a framework happens to report first, and re-run the
  OLD formula against the rest of the diff to confirm it reproduces the old numbers exactly.
- **A test building a production input struct with an object initializer that omits a field silently drives
  whichever arm `default` selects.** If production always supplies something else and the field selects a
  branch, the test exercises an arm production never takes, and nothing reports it — not a failing test, not
  a compile error, not a line-coverage gap (the lines still run, just the wrong ones). The structural fix is
  a positional constructor on a behavior-selecting struct, so omitting a field is a compile error; failing
  that, grep the struct's test construction sites whenever a field is added. When recording what "production"
  passes, name the configuration — a launch-time switch means production has as many values as the switch
  has settings, and a test exercising the other one is a real but differently-shaped gap, not a phantom arm.
- **Once you have measured that a correctly-configured path diverges by a known amount, bit-for-bit
  agreement becomes evidence the path did not run.** A managed-vs-Burst comparison whose transcendental
  fields are known to differ by a few ULP under relaxed float mode turns an unexpectedly EXACT match into
  the more useful signal — better evidence of a silent fallback to managed code than an enabled-flag check
  that a fallback can defeat invisibly.
- **"A large divergence implies a transcription error" only holds for a CONTINUOUS output.** Where a
  continuous quantity is quantised — an `int` cast, `ceil`/`floor`, a step count, a threshold comparison — a
  sub-ULP input difference can legitimately flip a whole quantum and shift every value downstream of it,
  which can look like an error many orders larger than real rounding noise. Compare the quantised values
  themselves first (a difference of exactly one quantum on a small number of elements is a boundary flip,
  not a bug); only once those match element-for-element does a continuous-stream ULP ceiling mean anything.
- **A constant or conversion is often untested not because it cannot be tested, but because every test runs
  at the one value where it is a no-op** — 0°, 180° (self-inverse), an axis-aligned case, or a device pixel
  ratio of 1.0 where a `÷ dpr` is the identity. Render or compute at a discriminating value instead (45° is
  usually enough), derive the expected value before measuring, and if the result disagrees, stop — treat it
  as a finding, not a reason to flip the expectation or the constant.
- **A test asserting WHICH item a per-tick budget picks must make every candidate eligible within the SAME
  tick, or it measures readiness order instead of priority order.** If eligibility depends on an async hop
  (a decode, a fetch) that can land across an unpredictable number of ticks, a scan that "pumps until
  something happens" accepts a partial ready-set as a valid start and silently tests whichever candidate
  became ready first. Use a synchronous scheduler for the async hop under test, a fixed tick count, and
  assert the full ready-set as a precondition before asserting the pick.

### Measuring coverage in a rendered image

- **Measure a width as an alpha-weighted coverage integral, never as a count of pixels over a threshold.**
  Sum `saturate((value − background) / (reference − background))` across a cut. A threshold count moves by
  ±1–2 px when a feathered edge pixel crosses the threshold, so it flakes on a correct change.
- **A coverage normaliser is a named reference region, read from unsaturated values.** When a test divides
  by a full-coverage reference to get a coverage RATIO, never take "the brightest pixel on the column" as
  that reference. A coverage function that ends in `saturate` pins that pixel at the clamp, so the ratio
  loses the variation it must measure, and a run where the maximum lands on another pixel rescales the
  result. A classifier that only sorts pixels into classes against the brightest one measures no ratio, so
  this rule does not apply to it (`FillPaintTests.Classify` does that).
- **An antialiased edge that is axis-aligned and on a pixel boundary has no partially covered pixel.** The
  two pixel centres either side of the edge read 1.0 and 0.0, which is correct antialiasing. A test that
  requires a partial pixel at an edge must offset the fixture by a fraction of a pixel (a quarter pixel is
  enough), or it fails against a correct renderer.

### Run a new instrument against an unmodified tree before trusting what it reports

Before shipping a stop rule, precondition count, fence or RED recipe, run it against the current, unmodified
tree and check its output is what it should be there — not the code it measures, the instrument itself. The
gate cannot catch a miscalibrated instrument, because the instrument and the code agree while both are
wrong.

- A fence scoped to a folder when its predicate names two specific files can trip on a clean tree.
- `Is.Not.AllocatingGCMemory()` reads green on a `new NativeArray<T>(…, Allocator.Persistent)` — it measures
  MANAGED allocation only, so it is vacuous by construction against anything native (see "Measuring
  per-frame GC allocation" above). Any test using it to police a native allocation needs a different
  instrument.
- A `grep -c` precondition count can be right for the wrong reason — counting LINES when the intent was to
  count logical entries that happen to be one per line. A correct number is not evidence the command counts
  the right population; ask what it actually enumerates.
- A reflection member-count test ("assert exactly N properties") can red on today's unmodified tree if
  `GetProperties()`'s default `BindingFlags` include statics the author did not count — pass
  `Public | Instance` explicitly, and cover `GetFields` too, since a future knob added as a public field
  walks straight past a properties-only test.

- **A gate's total test count is a fact about ONE branch — comparing it against a baseline from a different
  branch turns a correct result into a false alarm, or hides a real regression.** A branch cut from a
  different base than the one an expected total was measured on will legitimately read a different number.
  Before predicting a total, name the base commit the current branch is cut from and the total measured
  there: `expected = baseline(that commit) + cases added by this diff`.

- **A test that enumerates repository files by tracked-files-only is blind to the commit that adds it, and to
  every new file — exactly when it is most needed.** A file-citation or naming fence that scans only tracked
  files never sees its own untracked self, so it can go green on a tree that already violates it; the
  violation surfaces only once the file is committed. Scan tracked and untracked-but-not-ignored sources;
  resolve citations only against tracked targets, or an uncommitted local file could satisfy a reference that
  exists on no one else's checkout.

- **When a stage replaces an independent implementation with a new one, capture the OLD implementation's
  output as a committed golden BEFORE deleting it — not after.** A capture taken from the new code compares
  the new implementation against itself: green forever, pinning nothing, and indistinguishable from a real
  regression test until someone checks when it was captured. Capture is step zero, before the first
  production edit, with the source commit named in the test's own comment; use per-stream hashes rather than
  one whole-object hash, so a later failure can localize to which part changed.

### A dead-code sweep needs more than a caller count

**"Zero production callers, only a test caller" is not a sound predicate for a dead-code sweep.** It
conflates an ad-hoc accessor that exists only for a test to look inside (a real target) with one member of a
uniform family that happens to be the one a test touches (not a defect) — nine identical `*Kind` properties
on `LinePaintProperties`, with only one holding a production caller, are eight false positives waiting to be
"cleaned up" into an inconsistent family. Look at the declaring type before acting on a flagged member: keep
it if it is one of several uniform siblings, or if a shader or other non-C#-call-site consumer reaches it by
name.

### Before deleting a skip-guard: the degenerate-substitution test

A visual test often opens with a guard — "nothing rendered, skip" — and it is tempting to delete the guard
on the grounds that the assertion below will catch the same thing. **Often it will not, and the failure is
silent: the test still runs, still passes, and now checks nothing.**

Compute **D**, what each quantity the test measures reads when the subject does not render. D is usually 0,
but compute it per path, never assume: for a two-region comparison it is *the same value in both*; for a
helper with a sentinel it is whatever the sentinel makes downstream code return (`row = -1` →
`IsCenterPixelBackground` returns `true`).

Substitute D into **every assertion that would then run — including in the callers, when the guard sits in a
helper.** Three outcomes:

1. **D is excluded by an absolute threshold** — `> tol`, `>= floor`, an `InRange` band not containing D, or a
   threshold on the far side of zero like `< -5`. Safe to delete.
2. **D satisfies the assertion** — `|a - b| <= tol`, `variance < max`, `count <= N`, an `IsFalse` whose flag
   stays false on the degenerate path. **The guard is the only teeth. Convert it to `Assert.Fail`, never
   delete.**
3. **D fails only by strictness** — `a > b` where both sides collapse to D, so it fails solely because
   `0 > 0` is false. Fragile: safe against an exactly-zero blank, a coin flip against a near-zero one. Treat
   as case 2 unless the reading is provably exactly zero.

**The direction of the operator is not the discriminator.** `Assert.Less(rowShift, -5f)` looks like a "small"
assertion and is case 1, because the threshold sits on the far side of zero. Two `Assert.IsFalse` calls in
this suite have opposite verdicts: `IsFalse(solidCenterIsBg)` is safe because the sentinel makes the helper
return `true`, while `IsFalse(touchesBorder)` is case 2 because `TryCentroid` returns early with the flag
still `false`. Nothing about the form separates them — only what the degenerate path produces.

**This is a screen, not a verdict.** It is conservative and will flag a site that a separate argument clears:
a branch that is provably unreachable by control flow is safe for a reason the test cannot see.

**Companion rule.** An absolute threshold whose adequacy rests on fixture content is only as safe as the
thinnest fixture, and must be *measured*, not reasoned about — then **record the measured margin beside the
threshold**. `MaxDifferingFraction = 0.002` tells the next reader nothing; *"0.002 — the thinnest golden,
`gv1-label`, is 0.271% inked, a 1.36x margin"* tells them at once that one re-bake with a shorter label eats
it.

*Why this is written down:* a review of 28 such deletions found four wrong, one of which —
`Assert.LessOrEqual(|rim / interior - 1.0|, 0.02)` over a plain column mean with no background subtraction —
passes *perfectly* when nothing renders, because both readings become the identical clear colour and the
ratio is exactly 1.

### The leak instruments are `Mesh`-only and self-baselined, so a leaked `Material` is invisible

Every leak check in the suite counts `Resources.FindObjectsOfTypeAll<Mesh>()` — `RestyleHarness.cs`,
`SourceTileGraphBuildTests.cs`, `DisposalLeakGuardTests.cs`, `SymbolWorldRenderTests.cs`. Nothing counts
`Material`, and each one compares against a baseline taken *inside its own test*, so an object leaked by a
different test sits in both readings and subtracts out.

**A test that constructs a `UnityEngine.Object` and never destroys it goes green.** When converting cleanup
in bulk, the only guard is reading the code: every constructed object must reach a destroy, and no grep of
the results will tell you otherwise.

### `using` needs `IDisposable`, or a `ref struct`. A `Dispose()` method is not enough

Pattern-based `using` — the form that needs no interface — applies **only to `ref struct` types**. A plain
class with a `Dispose()` method does not qualify: `CS1674`. `UnityEngine.Object` and its family
(`GameObject`, `Material`, `Mesh`) implement nothing of the sort, so `using var go = new GameObject(...)`
does not compile either.

The mirror-image error compiles cleanly and fails at runtime: **a `using` on a local that the method
RETURNS.** The object is disposed at the end of the factory, before the caller ever touches it —
`using var set = new RenderLayerSet(); set.Build(...); return set;` handed every caller an emptied set. The
rule that covers both: **`using` belongs where the object is constructed and consumed in the same method; a
factory that returns the object owns nothing, and the caller tracks the return.**
