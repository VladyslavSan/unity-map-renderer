# Unity-Native Vector Map Rendering — Architecture & Roadmap

> A **Unity-native** engine that renders MapLibre-style *layered* vector maps from vector tiles, with a
> pluggable **bring-your-own data source** layer — built on **DOTS (ECS + Burst + Jobs)** for
> performance. Not a Cesium clone (no photorealistic 3D Tiles focus); closer to MapLibre/Mapbox:
> vector tiles + style-driven layered rendering.

---

## 1. Locked decisions

1. **Unity-native first.** Single target. Lean *into* the engine — Unity rendering-pipeline expertise
   is a feature, not something to abstract away.
2. **C# + DOTS** — ECS for entity/render management, **Burst + the Job System** for the data-parallel
   hot path (decode, tessellation, placement), `NativeArray` / `Unity.Mathematics` to stay GC-free.
   This is how we get native-class performance without leaving our wheelhouse.
3. **Spec-first clean-room.** Implement from the open specs (MVT / MLT / MapLibre Style Spec) +
   first-principles + the author's domain experience. Do **not** fork, depend on, or read MapLibre's
   source. Implementing a spec carries no copyright obligation.
4. **Portability is a non-goal** (an idea, not a target). We build the best Unity-native renderer. Good
   ECS design (data-oriented systems, logic separated from rendering) yields a *plausible* future
   C++/Unreal translation for free — but we spend zero effort designing for it now.

### "Implement yourself" ≠ "reinvent everything"
Build the architecture and the differentiators; depend on permissive libraries for solved subfields
that aren't your differentiator.

**Own it — clean-room from specs + experience + DOTS:**
- tile selection / scheduling / cache, and the **BYO data-source** abstraction
- MVT/MLT **decode** (hand-rolled proto reader is small and dependency-free; clean-room friendly)
- style-spec model + layer / paint / layout plumbing + **expression evaluation**
- fill + **line geometry** generation (joins / caps / miter — edge-case-heavy; budget for it)
- **polygon triangulation (fills)** → clean-room **earcut** (ear-clipping; what Mapbox/MapLibre use).
  Small enough to own; ISC as reference only; implementable **Burst-compatible** (struct/array-based) so
  fill tessellation jobifies. We classify MVT rings (outer vs hole) by signed area before triangulating.
- **label placement & collision** — the crown jewel; where domain experience differentiates the result
- the ECS rendering layer (this *is* the product) — meshes / materials / GPU-driven draw

**Vendor as clean permissive dependencies — don't reinvent** (one-line notice each):
- **text bidi** (UAX #9, mixed LTR/RTL) → a **managed** ICU-derived lib (ICU4N / BidiReshapeSharp) when
  mixed-direction labels arrive — never hand-roll full UAX #9. (the shaper ships only bounded Arabic joining +
  single-run RTL, which *is* small enough to own; see below.)
- **text shaping** (full GSUB/GPOS glyph-index shaping) → `HarfBuzzSharp` (MIT) — an entire subfield; never
  hand-roll — **BUT** only relevant to the *beyond-parity* "Model B" (runtime SDF from shipped fonts by glyph
  index). Our locked model consumes MapLibre's **glyph-PBF SDF** (codepoint-keyed), which pre-bakes the
  rasterisation offline and needs no runtime shaper — so HarfBuzz is **not** on the parity path (MapLibre
  itself doesn't use it; it uses an ICU subset via `mapbox-gl-rtl-text`).
- **SDF glyphs** → MapLibre **glyph-PBF** (codepoint-keyed, fetched from the style `glyphs` URL); NOT
  TextMeshPro, NOT runtime font rasterisation. (Model B / TextMeshPro kept only as a deferred beyond-parity option.)
- *(if MVT via lib instead of hand-roll)* → `protobuf-net` (MIT) / `Google.Protobuf` (BSD)
- *(later, geometry ops)* → **Clipper2** (Boost) — **not** GEOS (LGPL copyleft)

### Clean-room hygiene (keeps the claim defensible)
- Implement from **open specs + published technique + your experience.** Don't read MapLibre's *source*.
- A foreign renderer's *rendered output* is fair game as a **black-box behavioral reference**
  (golden-image diffs for correctness) — that touches no code.
- Vendored libs are clean *dependencies* (one-line notice), not "foreign source copied in."
- Prior employers: **general** domain knowledge/technique is yours (professional expertise). The only
  line — don't reproduce a *specific* employer's proprietary code/trade-secret from memory. Spec +
  general method sidesteps this.

---

## 2. Architecture (DOTS)

The map is a **data pipeline that ends in meshes**: camera state selects tiles → tiles are fetched and
decoded → features are tessellated into vertex/index buffers → buffers become renderable geometry.
The data-parallel stages run as Burst jobs across tiles; only mesh creation touches the main thread.

**This is not a `SystemBase`/ECS pipeline** — the hot path is Burst + Jobs scheduled from plain
orchestrator classes (`TileManager`, `SymbolPlacementSystem` are `VerifiedDisposable`, not
`SystemBase`). `Unity.Entities` is used narrowly, as one of three interchangeable render backends —
`Rendering/Backend/Entities/TileRenderer.cs` is the only file in the product that uses it. "DOTS (ECS +
Burst + Jobs)" stays the umbrella term for the stack (Burst + Jobs are DOTS regardless of ECS extent);
this diagram is the detail behind that umbrella.

```
                       ┌───────────────────────────── per-frame tick ─────────────────────────────┐
Camera/ViewState ─► TileManager  (Rendering/Tile/TileManager.cs) — cover → fetch → build → consume → evict
                                     │
                     ┌──────── async + Burst jobs (off main thread) ────────┐
                     ▼                                                       │
  BYO DataSource ─► fetch ─► MvtDecoder / MvtDecodeJob (MapRenderer.Jobs) ─► FillMeshGraph /
  (HTTP/PMTiles/               (managed proto parse + Burst per-tile          LineMeshGraph
   local/in-mem)                geometry decode)                             (MapRenderer.Jobs,
                                                                               scheduled job graphs) ──┘
                                                                  │
                                                                  ▼
                                                    consume  ── main-thread sync point: each
                                                    tile-layer mesh registers as a draw item via
                                                    ITileRenderBackend (Entities / BRG / GameObjects —
                                                    TileManager has no per-backend branching)
                                                                  │
                       ┌──────────────────────────────────────────┴───────────┐
                       ▼                                                        ▼
            Tile-mesh layers                                    SymbolPlacementSystem  (separate
            → tile meshes, one build per                        path, per frame: screen-space
            tile add/remove, drawn via                          placement + collision, Burst job
            the selected backend                                over a spatial grid)
                                                                          │
                                                                          ▼
                                                                Symbol/text billboards
                                                                (SDF, camera-facing)
```

**Consume must stay a lean record, not a rebuild.** A tile-layer mesh is built off-thread; the main-thread
step registers it as a draw item and does no per-tile-layer work heavier than that (no ECS structural
change, no synchronous rebuild) — an `ITileRenderBackend` that does more than the GPU upload plus the
registration has moved the build cost back onto the frame it was taken off.

### Module boundaries — what belongs where

**The product is `MapRenderer.Unity` + `MapRenderer.Jobs`.** That is the production target, and its
architecture, readability and performance are what matter.

**`MapRenderer.Core` is legacy: it is not a destination for new code.** It is large because much of this
renderer was written engine-free first, and while that code lives there it still earns a fast `dotnet test`
loop (`Tools/core-tests`, ~0.1s, no Editor lock, runs with the Editor open) — worth using, never worth
designing for. Engine-free is no longer a property we target. New work goes to `Unity`/`Jobs`, and Core
shrinks as subsystems nativize.

| assembly | role | what belongs |
|---|---|---|
| `MapRenderer.Unity` | **the product** | MonoBehaviours, mesh building, rendering glue, tile/label coordination |
| `MapRenderer.Jobs` | **the product** | the native/decode assembly: Burst + `Unity.Collections` jobs, the **tile decoders**, and the types that **own** blittable geometry |
| `MapRenderer.Core` | **legacy — no new code** | engine-free code that predates this rule: tile/Web-Mercator math, geometry, earcut, style/expression evaluation, text shaping |
| `MapRenderer.App` | **the product** | the composition root (`MapHost`, scene wiring) plus the dev-facing surfaces built on it — camera control, menus, diagnostics/telemetry panels |
| `MapRenderer.Tests.EditMode` | test runner | headless EditMode tests (the bulk of the gate, `./Tools/run-tests.sh`) |
| `MapRenderer.Tests.PlayMode` | test runner | PlayMode tests (multi-frame/async behaviour EditMode can't exercise); the same gate runs them |
| `MapRenderer.Tests.Shared` | shared test infra | fixtures/helpers referenced by both test runners |
| `MapRenderer.Unity.Editor` | editor-only | the URP `ShaderGUI` for the map shaders (`Editor/ShaderGUI/`); Editor platform only, never in a player build |

> **`Jobs` is not "only Burst-able code" — read the row literally.** Since IR C1 it also holds a hand-rolled
> **managed** protobuf decoder (`Mvt/MvtDecoder.cs`), the managed selection seam (`Tiles/FeatureSelector.cs`)
> and a string lookup (`Tiles/SourceLayerResolver.cs`) — none blittable, none Burst. They belong there because
> they follow `ITileLayer`, and `ITileLayer.Geometry` carries a `NativeArray`-bearing struct; splitting the
> decoder from the buffer it produces is exactly the misplaced boundary described below. "It isn't blittable"
> is therefore **not** an argument for moving something out of `Jobs`.

**Placement rule: put code where it belongs architecturally, then test it wherever it lands.** Testability is
never a placement argument — the Unity EditMode runner tests everything; it is merely slower. The fast runner
also *shims* what it needs (13 `Unity.Mathematics` types today; `double2` is 22 lines), so "the fast runner
can't compile it" is usually one shim away from false, and is never on its own a reason to shape production
code.

> **Never contort a design to keep something in Core.** If keeping a type in Core forces geometry to travel as
> `uint[]` because Core cannot express a blittable buffer, forces a sidecar object to carry what an existing
> type should own, or forces an interface to exist for a single implementer — **the boundary is wrong. Move the
> code to `Jobs`/`Unity`.** Do not invent a workaround that preserves the boundary.

**Why this is written down (it went wrong, three times, from one cause).** The MVT decode seam sat in Core.
Core cannot express a blittable buffer, so decoded geometry could only travel as a managed `uint[]`. That single
misplaced boundary produced, in order: `IMvtGeometryCarrier` (a public, MVT-named interface invented to carry
encoded bytes sideways onto otherwise format-neutral features, with multiple implementers); per-consumer decode
(geometry materialized once *per style layer* — 108 times per tile on the repo's own default style, 61 of those
decoding one source layer); and `TileGeometryStore` (a detached cache holding geometry the decoded tile itself
should own, bound to its tile only by convention — nothing in the type system could tell it had been paired
with the wrong one). Each workaround was individually reasonable and locally correct. All three were
consequences of refusing to move ~10 files out of a 206-file assembly.

`Structure/CoreAssemblyBoundaryTests` pins that what is *still in* Core stays runnable outside the Editor, so
the existing fast loop does not rot silently. It guards a property of legacy code — it is **not** a goal to
extend, and **never** a reason to keep or place a type in Core.

### New code is designed for performance, not nativized later

**Design new features data-oriented and native-first from the start.** Every hot subsystem here written
managed-first has had to be rewritten over native containers afterwards — symbols, then MVT decode — so the
"explore in managed, optimise later" saving has proven illusory on the data plane, twice. The representation
answers are already solved and reusable (tagged unions for variants, a flat string pool for text, native
columns for per-feature data), so new data-plane code starts there at near-zero marginal cost and skips the
retrofit.

The discriminator is **not** "is it hot?" — it is *structural*:

| | born native | managed is correct |
|---|---|---|
| **which** | the **data plane** — anything that recurs per tile / feature / vertex / glyph / frame, or is read inside a job | the **control plane** — runs once per style load, per user action, or per lifecycle transition |
| **shape** | blittable structs, `NativeArray`/`NativeList`/`NativeHashMap`, index handles, string pools | classes, `List`/`Dictionary`, references, `UnityEngine.Object` |
| **test** | "will this be read inside a job, or loop over scene-sized data?" | "is this bounded by config size and touched once?" |

A managed capture on the data plane is not just slower — it is *disqualifying*: a body that closes over a
`Dictionary` or a class reference cannot become an `IJob` at all without being rewritten first. That is a
design constraint to honor up front, not a performance note to revisit.

This does not license nativizing the control plane. Native containers there buy nothing and cost real
legibility, debuggability and disposal risk — see the allocation ladder in `docs/conventions.md`, whose top
rung is still *allocate nothing*, not *allocate native*.

When a nativization retires a managed type, delete it outright rather than keeping it as a test-only
remnant — a type modeling a state the native producer can no longer emit is a liability future readers must
account for, not a safety net.

### Two geometry classes, two paths
| Class | Built | Lifetime | Rendered as |
|---|---|---|---|
| fills / lines / fill-extrusions | once per tile (Burst job) | rebuilt on tile add/remove | static meshes / GPU buffers in the scene |
| **symbols / text / icons** | **placed every frame** (screen-space collision) | transient | camera-facing SDF billboards |

Labels stay upright and don't scale with zoom → screen-space placement + collision **every frame**.
This is a separate path from fills/lines and is where these projects historically die — sequenced last.

### Styling model — build geometry once, restyle via material

Core principle (matches MapLibre/Mapbox GPU rendering): **bake topology once; drive appearance through
shader uniforms / material properties.** Changing a style value sets a material value — no mesh rebuild.
This also makes smooth zoom-driven width and live restyling cheap.

**Lines use GPU-side expansion** — the mesh stores the *centerline*, not the final ribbon:
- per vertex: centerline position, **extrusion normal** (unit perpendicular, packed), `distance-along-line`
  (dashes), side (+/−), and a reserved **per-feature width-scale** attribute (for data-driven width).
- the **vertex shader** offsets each vertex along the normal by `½ · width` (width from material) → width
  is a uniform, not geometry.
- the **fragment shader** does AA (feathered edge from the interpolated extrude amount; `blur` uniform) +
  dash pattern + color/opacity.

**Build-time vs material-time — what's tweakable without a rebuild:**

| Material/uniform (no rebuild) | Baked at build (rebuild to change) |
|---|---|
| `line-width`, `line-color`, `line-opacity` | `line-join` shape (miter/bevel/round) |
| `line-blur` (AA), `line-gap-width` (casing) | `line-cap` shape (butt/round/square) |
| `line-offset`, `line-dasharray`, `line-pattern` | centerline + per-zoom simplification |

Fills: triangulated once; color/opacity/pattern via material. Fill-extrusion height: material or vertex
attr. Symbols/text: SDF shader, size/halo via material.

**Two MapLibre style dimensions to support:**
- **Zoom-dependent** (width interpolates over zoom): evaluate on CPU per frame → set the uniform. This is
  where build-once-restyle shines — smooth changes, zero rebuilds.
- **Data-driven** (per-feature width/color): bake a per-vertex attribute and combine with the zoom uniform
  in-shader. A lone uniform only gives per-layer values; the attribute preserves per-feature variation
  while base width stays a material knob.

**Width units — DECIDED: meters canonical, pixels derived in-shader.** The vertex shader extrudes the
centerline by `½ · widthMeters` — **world meters is the single canonical shader unit.** A style width in
**meters** is used directly; a width in **pixels** (MapLibre semantics) is converted px→m from camera
parameters. Doing the conversion *in the shader* lets us ship a per-frame `metersPerPixel` uniform first
(exact for top-down / orthographic) and later upgrade to a **per-vertex perspective-correct** factor
(exact pixel width under a *tilted* camera — the MapLibre-grade result) **without changing mesh format or
materials.** Width stays a material knob (`_Width` + a unit-mode flag).
- *Why per-vertex matters:* under perspective tilt, `metersPerPixel` varies with depth across the screen;
  a single per-frame factor is exact only at the focal depth. Per-vertex scaling by clip-space depth fixes it.
- **AA / edge feather** uses screen-space derivatives (`fwidth`) → ~1px regardless of unit or camera.
- **"meters" = Web-Mercator meters** (the geometry's own units; stretched by latitude). Apply an optional
  `1/cos(lat)` correction only if true-ground-meters are wanted.

**Unity path:** hand-written HLSL URP shaders (ShaderGraph cannot share one HLSL vertex function across
N per-layer graphs and is impractical for extrusion + fwidth AA). Each layer is self-contained under
`Shaders/Map/<Layer>/` — its `.shader`, `<Layer>_LitInput.hlsl` (the `UnityPerMaterial` CBUFFER +
DOTS bridge), and its pass bodies. `MapVertexModify` is a per-layer vertex hook; Fill defines its body
in `Fill_VertexModify.hlsl`, included by the `.shader` before any pass that calls it.
See `Shaders/README.md` for the layout and include-order rules; `docs/meshing-design.md` §3 (lit rendering)
for the full design rationale.

Per-layer styling: **per-layer Material instances** (never `MaterialPropertyBlock` — it silently
disables the SRP Batcher). Style properties (`_Color`, `_Width`, `_Opacity`, …) live in the shared
`UnityPerMaterial` CBUFFER and are additionally registered as `UNITY_DOTS_INSTANCED_PROP` so the same
names work under SRP Batcher AND BatchRendererGroup (BRG) without any call-site changes.
Submitted via BatchRendererGroup / `Graphics.RenderPrimitives` (instanced, GC-free).

A per-layer material has three owners, split by what each is free to change without touching the others:
the base `.mat` asset sets the **look** (smoothness, surface feel — live-editable); `MaterialFactory` clones
the base and re-asserts the renderer's **contract** (`ZWrite` off for the painter's algorithm, the color
identity the vertex bake or the applier expects); `ZoomStyleApplier` drives the per-layer **style** values
(color/opacity/width from the MapLibre style) frame to frame. Each base `.mat` lives in a `MapMaterialSet`
asset, so editing one base live-tunes every layer that clones it.

### Layer ordering & draw submission
The style is an **ordered list of layers**, composited in order (painter's algorithm). Most layers are
**coplanar** (the ground plane), so Unity's default sort — render queue → camera distance → depth buffer —
**z-fights and reorders them wrongly**. Rule: **we own draw order; we never rely on Unity's automatic sort.**

- **Flat layers (fills, lines — the bulk):** painter's algorithm — **ZWrite off**, draw in style order.
  Implemented via a **custom URP `ScriptableRenderPass` / `BatchRendererGroup`** that issues draws in layer
  order with `SortingCriteria.None` (owns the order; scales past the integer-queue trick). Simple interim
  fallback: each declared layer owns a small contiguous **band** of queue values
  (`material.renderQueue = base + layerIndex * SubSlotsPerLayer + subSlot`), ZWrite off — a symbol layer's
  icon and text occupy two sub-slots of their own band so the icon always draws under its own text (G7/D7,
  road-shields Stage 2); every other kind uses one sub-slot. Widening the stride from 1 to 2 halves the
  interim's layer-count runway (~2000 → ~1000; realistic styles stay far below either).
- **3D layers (fill-extrusion, terrain, globe):** ZWrite on + depth test; give each layer a **depth
  range/slice** so the depth buffer enforces *both* layer order *and* 3D occlusion (MapLibre's approach).
- Order *within* a layer across tiles is irrelevant (disjoint regions); order *across* layers is strict.

Step 2+ concern (first multi-layer render); Step 0 (single layer) doesn't hit it.

### Performance levers (the differentiator)
- **Burst + Jobs** for decode and tessellation → parallelize across all in-flight tiles; no GC in the hot path.
- **GPU-driven rendering** for lines/fills: keep compact per-feature buffers and expand/style in shaders
  via **BatchRendererGroup** / `Graphics.RenderPrimitives` / `GraphicsBuffer`, instead of fat CPU meshes
  and a GameObject per feature (the old Mapbox-Unity-SDK mistake). This is where your URP/HDRP tricks pay off.
- **`Unity.Mathematics`** for SIMD-friendly vectorized math in jobs.
- **Off the main thread by default** (cited in code as `off-main-thread-principle`)**.** Only work the Unity API forces onto main — creating, destroying, or
  uploading a `UnityEngine.Object` (`Mesh`, `Texture2D`, `GameObject`, `Material`) — runs there. The
  destination for everything else (decode, geometry, projection math) is a **Burst job**, not a managed
  background thread: a managed closure can never reach a Burst worker, and on a WebGL build it has nowhere
  to run at all (`docs/web-target.md`). Where a body is still managed at the type level, a scheduler seam
  may dispatch it off-main as an interim step, but that seam is deleted at each site once the site's data
  is native — see `docs/job-scheduling-design.md`, the dispatch discriminator and the scheduler seam.

### Coordinate precision / floating origin (non-negotiable)
Unity transforms are 32-bit float; world-scale Mercator coordinates jitter badly. The core computes in
**Web Mercator doubles**; each tile carries a `double` origin and the render layer rebases to a 32-bit
local origin near the camera (recenter the map root as the camera moves). Designed in from Step 3. The
per-frame frame (origin, rebase rotation, camera-relative position) has one owner, rebuilt fresh each
frame — never read the camera's position back off a Unity `Transform` to recover it.

### Projection: spherical is the default, and the camera stays projection-agnostic
The shipped demo scene runs the **spherical (globe)** projection, not Web Mercator — Mercator is the
planar/legacy path. This is easy to get backwards from the code alone: the projection-dependent tile
builders default their standalone `IProjection` to `WebMercatorProjection`, and a scene's projection choice
is a serialized bool that only the `.unity` file carries, not a C# default. Check the scene, not a field
initializer, when reasoning about which projection is live.

Camera interaction (pan/zoom/tilt) holds no projection constants of its own — no tile pixel size, no
Mercator latitude clamp, no earth circumference. Pixel-to-ground conversion and world-edge clamping are a
service the active `IProjection` provides, so the same interaction code drives both the planar and the
spherical projection without a branch.

### BYO data-source abstraction
A small C# interface — `fetch(tileCoord) -> bytes` + declared encoding (MVT now, MLT later, raster
later). HTTP, local files, PMTiles, a proprietary backend, or in-memory generated tiles all look
identical to the pipeline. This is a first-class product surface. Fetching itself goes through
`UnityWebRequest`, never `System.Net.Http.HttpClient` — `HttpClient` allocates heavily per request, and
WebGL cannot use it at all.

---

## 3. Roadmap (Unity milestones)

### Step 0 — Spike (de-risks the premise)
Decode one real MVT tile (hand-rolled proto reader), triangulate its polygons in a Burst job, build a
Unity `Mesh`, render it. **Exit: a real tile's land/water polygons appear on screen in Unity.**

### Step 1 — Data pipeline + BYO source
Async tile fetch + cache, jobified decode, the data-source interface. Drive from a live vector-tile
endpoint *and* a local file source to prove the abstraction.

### Step 2 — Fills + lines
Tessellation jobs; tile load/unload by camera zoom; per-layer materials. Stand up the **GPU-driven line
rendering** path early (it's both a perf win and a showcase of pipeline mastery).

### Step 3 — Style subset + camera + floating origin
A working MapLibre Style Spec subset (sources, layers, paint/layout for fill+line, zoom expressions,
filters); camera → view-state; floating-origin rebasing. **Exit: pan/zoom/tilt a styled multi-layer
basemap at world scale, jitter-free.**

### Step 4 — Symbols / text / collision (the hard ~half)
SDF glyph atlas, `HarfBuzzSharp` shaping, per-frame placement/collision (Burst job over a spatial grid),
camera-facing billboards. Validate against MapLibre's rendered output (golden images).

### Step 5 — 3D + polish
Fill-extrusion / 3D buildings (depth-correct — the Unity advantage); raster + hillshade; terrain;
perf passes (BatchRendererGroup, GraphicsBuffer procedural rendering, Burst tuning); package as a Unity
package + sample scenes.

### Future (non-goal now)
Translate the data-oriented core to C++ for an Unreal port — "porting our own entities," no
Mapbox/MapLibre in the lineage. Plausible *because* the DOTS code is already value-types-and-buffers;
not a current target.

---

## 4. Licensing posture
- **Spec-built parts** (MVT CC-BY *text* but free to implement; MLT; style spec): **zero obligation** —
  implementing a spec isn't a derivative work.
- **Vendored deps** are all permissive: HarfBuzzSharp (MIT), protobuf-net (MIT) / Google.Protobuf (BSD),
  Clipper2 (Boost). Commercial + closed-source fine. Fill triangulation is **clean-room earcut** — ISC
  only as reference, implemented ourselves → no dependency/obligation.
- **Compliance = one file.** A bundled `THIRD-PARTY-NOTICES.txt` (and an in-app credits screen if you
  ship binaries) reproducing each dep's copyright + license satisfies them all. No copyleft, no
  open-sourcing, no in-UI attribution required.
- **Avoid copyleft in the core** (GPL/AGPL viral; LGPL link-level; MPL file-level). GEOS = LGPL → use
  Clipper2 instead. (Not legal advice; a short IP-lawyer pass before commercial launch is cheap insurance.)
- **`Unity.Mathematics`** (used throughout, not a vendored dependency) ships under the **Unity Companion
  License**, not MIT — permitted use is scoped to Unity-dependent projects. That is no restriction here (this
  is one), but it does mean the math types/functions can't be lifted into a non-Unity codebase under open terms.

## 5. Risks
| Risk | Mitigation |
|---|---|
| DOTS / Entities API churn & learning curve; Entities Graphics constraints | Keep the hot path as **Burst jobs + NativeArrays** (stable surface); use ECS where it clearly pays; don't force every subsystem into it |
| Hard kernels (placement, line tessellation) subtly wrong | Vendor shaping; clean-room earcut + placement carefully; **golden-image diffs vs MapLibre output** |
| Coordinate jitter at world scale | Floating-origin rebasing from Step 3; doubles in core, floats at render |
| GC / frame spikes | NativeArray everywhere in hot path; no managed allocs per frame; GraphicsBuffer for large geometry |
| Text / i18n complexity | HarfBuzzSharp; sequence last (Step 4) |
