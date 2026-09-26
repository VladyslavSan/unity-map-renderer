# Meshing — design & SSOT

How decoded vector-tile geometry becomes drawn, lit meshes: the per-kind build pipeline, line antialiasing, the
URP shading convention every map shader follows, and the render-layer model that gives every painted style
layer a uniform place. Read this before touching `FillMeshGraph`, `LineMeshGraph`, `StyledFillTileBuilder`,
`StyledLineTileBuilder`, `RibbonJob`, the map shaders under `Shaders/Map/`, or `IRenderLayer`/`RenderLayerSet`.
How the stages chain as Burst jobs is `docs/job-scheduling-design.md`; how a tile's geometry reaches them is
`docs/tile-geometry-ir-design.md`.

1. **[Mesh pipeline](#1-mesh-pipeline)** — how MVT bytes become a mesh, per geometry kind (fill vs line orderings, the build/consume loop).
2. **[Line antialiasing](#2-line-antialiasing)** — what the line shader does at the edge, and why the ramp straddles the styled edge.
3. **[Lit rendering](#3-lit-rendering)** — the shared URP shader convention (Lit and Unlit): mirror-copy structure, multi-pass, styling as material properties.
4. **[Render-layer model](#4-render-layer-model)** — `IRenderLayer`/`RenderLayerSet`: every painted kind first-class, with a uniform draw order and shadow-casting contract.

---

# 1. Mesh pipeline

The path from decoded vector-tile geometry to a GPU `Mesh`, per geometry kind.

## Vocabulary: "tessellate" means triangulate

"Tessellation" is a loose word that can name four unrelated things, and that makes this path hard to reason
about. Here "tessellate" means **triangulate** and nothing else. The other meanings have their own names:

| Loose meaning | Name | Lives in |
|---------------|------|----------|
| the whole decode→mesh chain | **the mesh pipeline** | `FillMeshGraph`, `LineMeshGraph`, the `StyledFill/LineTileBuilder`s |
| inserting curvature points (globe) | **Subdivide** | `SubdivideJob` (line), `GlobeFillSubdivideJob` (fill) |
| earcut / ribbon-offset | **Triangulate** | `EarcutJob`, `RibbonJob` |
| "build one whole tile's mesh" (async scheduling) | **mesh build** (worker) + **consume** (main thread) | `TileManager` (`KickMeshBuild`, `MeshBuildTask`, `MaxMeshBuildsPerTick` / `ConsumeMeshBuild`, `MaxConsumesPerTick`) |

That last row is the tile-build loop — two verbs, no "phases":
- **build** the mesh — kick a background task that runs the mesh pipeline off-thread, producing `Mesh.MeshData`.
- **consume** it — upload the built data to a GPU `Mesh` + place the render object, on the main thread.

`MaxMeshBuildsPerTick` throttles builds and `MaxConsumesPerTick` throttles consumes.

## The named responsibilities

- **Decode** — MVT command bytes → integer tile-space rings (`MvtDecodeJob`). Decode runs once per tile, at
  fetch completion, for every source-layer (`docs/tile-geometry-ir-design.md`); every mesh build borrows its
  output. The stages below run per mesh build.
- **Clip** — cut rings, in tile space, to `[−b, extent + b]` — the tile plus however much of its **buffer** is
  kept. Fill only (see below). (`RingClipJob`, parameterised by the `TileBufferClip` knob in Core.)
- **Assemble** — classify rings into polygons + holes (outer/hole/skip by signed area). (`RingAssemblyJob`.)
- **Subdivide** — insert extra points so a straight edge in tile space follows a *curved* projected surface.
  Driven entirely by `IProjection.MaxRefineAngleRad` (∞ for Mercator ⇒ no split; a small angle for the globe).
  No projection constants leak in — the flat case is the degenerate value of one formula, not a branch.
- **Triangulate** — rings/centerline → triangle vertices + indices. Fills use ear-clipping (`EarcutJob`);
  lines extrude a ribbon (`RibbonJob`).
- **Project** — tile-space → geodetic surface (`TileToGeoJob`, projection-independent) → render-space `double3` +
  per-vertex `up`, through the chosen `IProjection` (`ProjectPointsJob<TProj>`).
- **Write** — stream the vertices/indices into a caller-allocated `Mesh.MeshData` on the worker; the main thread
  only **allocates** (at kick) and **applies** (at consume/upload).

## There is no single linear order — fills and lines compose these differently

This is the load-bearing point. **Triangulate-vs-Project is _opposite_ between the two kinds, and Subdivide sits
in a different slot for each.** Baking one universal sequence into the names would be a worse model than an
overloaded word.

| Kind | Order | Where |
|------|-------|-------|
| **Fill** | Decode → **Clip** → Assemble → **Triangulate** (earcut, flat tile space) → **Project** → *(globe only)* **Subdivide** | `FillMeshGraph.Schedule` schedules Clip→Assemble→Triangulate→Project over the borrowed decode output, and on the curved arm the globe Subdivide too (`GlobeFillSubdivideDispatch.Schedule` + `GlobeFillScatterJob`, gated by `!double.IsInfinity(proj.MaxRefineAngleRad)`), all inside one graph; `StyledFillTileBuilder` schedules the graph then schedules the write step. |
| **Line** | Decode → **Subdivide** (centerline, tile space) → **Project** → **Triangulate** (ribbon) | `LineMeshGraph.Schedule` schedules `RingGatherJob` → `TileToGeoJob`/`ProjectionDispatch` → `SubdivideJob` → `TileToGeoJob`/`ProjectionDispatch` → `RibbonBatchJob`; `LineRenderLayer.BuildGraphRequest` builds the request via `StyledLineTileBuilder.BuildLayerInput`, and `LineStreamWriteJob` writes the mesh. |

**Why fills Triangulate *before* Project.** Ear-clipping is a **planar 2D algorithm**, and triangle
**connectivity is projection-invariant** — which vertices form a triangle doesn't change when you bend the sheet
onto a globe. So earcut runs once in exact integer tile space (cheapest, most robust), and Project moves the
resulting vertices afterward. On the globe a post-Project **Subdivide** then refines the now-curved triangles.

**Why lines Triangulate *after* Project.** The ribbon extrusion needs the **projected 3D centerline plus its
`up`** to build the cross-section: `across = normalize(cross(along, up))`, tying the ribbon width to the same
`up` the centerline was projected with. The winding is therefore consistent across projections with no
per-projection flip; the uniform Unity-front reversal for stock Cull Back happens at the mesh-write boundary
(`docs/coordinates-and-projections.md` § "Handedness, winding & why the ECEF reflection is load-bearing" and
§ "Low-level rules (invariants)"). So the centerline is subdivided and projected first, then `RibbonJob`
triangulates in render space.

## Why the fill path Clips, and why only the fill path

Every MVT tile carries geometry past `[0, extent)` — its **buffer**, 64 tile units at extent 4096 by the
OpenMapTiles convention — so a neighbour's geometry is available for joins. Drawn whole, two adjacent tiles
both paint the 128-unit overlap strip. Under the fill's `SrcAlpha`/`OneMinusSrcAlpha`, `ZWrite`-off contract a
translucent fill composites to `1 − (1 − α)²` there instead of `α`: a uniform brighter **band** one
buffer-width wide along every seam, for any `fill-opacity < 1`, any `rgba()` `fill-color`, or any two stacked
translucent fill layers. (Opaque fills are unaffected — same colour over same colour composites identically —
but the ≈ 6 % overdraw saving is real regardless.)

**The window defaults to 0 buffer units** (`MapViewConfig.FillTileBufferClip`): each tile keeps
`[0, extent]` and nothing past it. A non-zero default would hedge against a seam crack between clipped neighbours, and
measurement shows no such crack. A negative config value disables the clip stage, which is not the same as a
zero margin; `TileBufferClip`'s `default` value is that disabled state.

Clip sits **after Decode and before Assemble**. Rings are still just rings there, with no
polygon/hole structure to keep consistent; `RingAssemblyJob` then classifies the geometry that will actually be
drawn, and its two existing filters (`rLen < 3`, degenerate `|area2|`) drop the clipped-to-nothing rings for
free. Sutherland–Hodgman against the four half-planes is orientation-preserving, so the CCW-in-tile-space
winding contract is untouched. The window's boundary is **inclusive**, and a ring whose bbox is already inside
is copied verbatim — which is what makes "already-inside geometry is bit-identical" structural rather than a
property of the arithmetic.

**Lines are NOT clipped.** Clipping an input polyline at the tile boundary turns the join at that
vertex into a **cap**, trading the alpha band for a notch at every seam. The line equivalent is clipping the
tessellated *ribbon* — a different and harder operation, not attempted here. Symbols clip on their own terms
(the single-world `[0, extent)` anchor rule); fill-extrusion and raster are untouched.
`ITileMeshRenderLayer.BuildGraphRequest` therefore carries the knob (`context.BufferClip`) to every kind but
only `FillRenderLayer` acts on it.

Alternative mechanisms for the same defect — per-tile stencil masks, a clip plane, a shader-side discard on
tile-space UV — are all viable and all out of scope.

## Threading & lifetime (both kinds)

Both kinds mesh on a scheduled job graph (`FillMeshGraph.Schedule`, `LineMeshGraph.Schedule`) that the pump
completes; no stage after Decode runs with `.Run()`. Decode runs `MvtDecodeJob` with `.Run()` once per tile, at
fetch completion, on the thread `TileDecodeDispatch`'s scheduler picks (off the main thread on desktop)
(`docs/job-scheduling-design.md` § "The dispatch discriminator — `.Run()` or `.Schedule()`"). Only
`Mesh.MeshData` **Allocate** (at kick) and **Apply** (at consume) are main-thread. Mesh *data* (`NativeArray` / `FillGraphOutput` / `MeshData`) is a
value-type struct disposed deterministically at the apply boundary (at the write graph's dispose nodes); the
`Mesh` is the single-owner class. Full contract: [`async-architecture.md`](async-architecture.md)
§"Disposal & cancellation contract" and the mesh-ownership rule in [`conventions-short.md`](conventions-short.md).

## Key types

| Type | Assembly | Role |
|------|----------|------|
| `FillMeshGraph` | `MapRenderer.Jobs` | Fill: schedules the Clip→Assemble→Triangulate→Project job graph over one `FillMeshPipeline.LayerInput`. Produces an uncompleted `FillGraphOutput`. (`FillMeshPipeline` is only the home of `LayerInput`, `HoleRingComparer` and two sizing helpers; it schedules nothing.) |
| `LineMeshGraph` | `MapRenderer.Jobs` | Line: schedules the gather→Subdivide→Project→ribbon job graph over one line `LayerInput`. Produces an uncompleted `LineGraphOutput`. |
| `RingClipJob` | `MapRenderer.Jobs` | Fill Clip: Sutherland–Hodgman of each ring against the tile-buffer window, in tile space. Winding- and space-preserving. |
| `TileBufferClip` | `MapRenderer.Core` | The knob: how much buffer to keep, in tile units at extent 4096, converted to the layer's own extent in one place. `default` ⇒ disabled. |
| `TileRenderOrigin` | `MapRenderer.Core` | The single source of a tile's bake/RTC origin (SW corner projected). Engine-free, shared by fills/lines/symbols/camera — **not** fill-specific, so it lives in Core, not on the fill mesher. |
| `TileToGeoJob` | `MapRenderer.Jobs` | Project stage part 1: tile-space → geodetic surface (projection-independent). Takes a `TileId`. |
| `RibbonJob` | `MapRenderer.Jobs` | Line Triangulate: projection-agnostic 3D ribbon from a `(point, up)` array. |
| `StyledFillTileBuilder` | `MapRenderer.Unity` | Fill orchestration: color eval → `FillMeshGraph` → write mesh (globe subdivide is a graph node on the curved arm, not a separate step). |
| `StyledLineTileBuilder` | `MapRenderer.Unity` | Line prologue: builds the line graph's `LayerInput` (`BuildLayerInput`) from the selected features and paint. |
| `MeshDataPayload` | `MapRenderer.Unity` | The per-`(tile, layer)` mesh handle the consume loop uploads + disposes. |
| `TileManager` | `MapRenderer.Unity` | The tile-build loop: `KickMeshBuild` / `MeshBuildTask` (build) and `ConsumeMeshBuild` (consume), throttled by `MaxMeshBuildsPerTick` / `MaxConsumesPerTick`. |

---

# 2. Line antialiasing

`docs/line-antialiasing-design.md` is the SSOT for the ramp: its derivation, the geometry pad, the on/off
switch, and the rejected directions. This section states what the shader does and the reasoning that fixes
the ramp's placement.

## What the shader does

`Line_VertexExtrude.hlsl` extrudes the ribbon to the styled half-width (a screen-space pixel width from the
projection measurement). By default `LineCoverage` antialiases both ribbon edges with a one-device-pixel
straddle, centred on the styled edge:

- **Outer edge:** `coverage = saturate((1 − |side|) / |∇side|)` — the ramp centres on the styled edge, so
  `a == 1` from the centreline out to half a pixel inside it and `a == 0` half a pixel outside it. The
  `_EDGE_ANTIALIASING_OFF` build flag restores a hard edge instead (coverage `1` up to the rasterized triangle
  silhouette, no feather).
- **Gap hole (cased/hollow lines):** the same straddle, centred on `|side| = innerFrac`; under
  `_EDGE_ANTIALIASING_OFF` this is a hard cut at `|side| < innerFrac` instead.
- **`_Blur` (MapLibre line-blur):** opt-in inward soft edge on top of the straddle, `0` ⇒ no-op. Not
  antialiasing.
- **Dash:** its own along-line `fwidth` feather — a different aliasing axis.

The min-width floor (half-width `≥ 0.5` device px, pixel widths only) guards a sub-pixel line from dropping
out.

## Why the ramp straddles the styled edge — the trilemma

A shader-space alpha fade is placed inset, straddling, or outset. The placement decides which of three
properties it can hold:

1. the configured width renders **solid** (full intensity to the edge),
2. the total apparent width is **unchanged** (AA doesn't fatten or thin the line),
3. the edge is **antialiased** (partial-alpha pixels around the teeth).

Antialiasing a hard edge *means* placing partial-alpha pixels near it, and those pixels are physically either
**outside** the configured edge or **inside** it — there is no third place:

| approach | solid core | width unchanged | AA'd | what gives |
| --- | --- | --- | --- | --- |
| **outset** (fade past the edge) | ✅ | ❌ | ✅ | fattens by the fade width |
| **inset** (fade inward) | ❌ | ✅ | ✅ | edge/core dims, reads thinner |
| **straddle** (50 % at the edge) | ~ (outer ½ px dims) | ✅ | ✅ | balanced, but a thin line goes soft |

**The straddle row resolves the trilemma.** The outer half-pixel dims — but that is the *definition* of an
antialiased edge, not a failure of the solid core. The straddle makes the ribbon `a == 1` everywhere from the
centreline out to half a pixel inside the styled edge, ramps 1 → 0 across the one pixel centred on that edge,
and so keeps the 50 % contour on the styled width: **solid core ✅, apparent width unchanged ✅,
antialiased ✅**. The "thin line goes soft" caveat is real; the min-width floor handles it, because it floors
the *styled* half-width — the thing the ramp centres on.

Two corollaries:

- **A fade-*width* knob is not a quality dial.** Correct coverage-AA is a ~1 px transition. Widening the fade
  just **blurs** (trades sharpness for smoothness) — it never adds *resolution*.
- **Extruding geometry + hard-clipping `|side| > 1` does nothing.** With one sample per pixel, a pixel is lit iff
  its centre passes the boundary test; whether that test is the triangle edge or a fragment `discard(|side| > 1)`,
  it is the same test at the same point → the identical staircase. Only **more samples per pixel** (MSAA/SSAA) or
  a **coverage gradient** (the fade) put intermediate values on a diagonal.

## Cased lines — why the placement must keep an `a == 1` interior

A cased road (e.g. `road_motorway_casing` + `road_motorway`) is **two separate transparent draws**: the casing
(wider, drawn under) and the fill (narrower, drawn over), both `Queue=Transparent`, `ZWrite Off`,
painter-ordered. There the edge ramp is a **compositing** question, not a coverage one.

- **An inset fade fails.** It puts partially transparent pixels inside the fill's own styled band. Those sit
  over the casing's interior, so the casing colour bleeds through and the crisp fill↔casing boundary becomes
  a muddy blend band.
- **The straddle does not.** It confines the ramp to the one pixel centred on the edge and is `a == 1`
  everywhere inside that, so no region of the fill's band lets the casing show through. The outer half pixel
  that ramps to transparent lies over the casing's interior, and blending a colour into `a == 1` casing at
  the boundary pixel is what a correct antialiased boundary looks like. Pinned by
  `CasedPair_InternalBoundary_StaysCrisp`.
- **MSAA does not fix the inset case.** MSAA antialiases geometry *coverage*, but the fill and casing are
  still blended, not replaced, so more samples give a cleaner version of the same bleed. The defect is in the
  compositing model.

**Single-pass cased rendering is the better architecture for cased lines, and it is not built.** It is not a
prerequisite for antialiased lines. The two directions that would reach it are rejected
(`docs/line-antialiasing-design.md` § "Rejected directions"):

- **Fuse the casing and fill into one draw** — this regresses junction draw order on real styles. Most line
  layers are unpaired, and a style interleaves other layers between a casing block and its fill block, so no
  single fused slot reproduces the declared compositing.
- **Replace-compositing** (an opaque queue, or blend-off plus alpha-to-coverage) — this conflicts with two
  locked rulings: map layers never ZWrite, and MSAA is not used (it gives no meaningful improvement even at 16×).

## Other edge-related state

- **`_Blur` (line-blur)** — a real MapLibre paint property: an opt-in inward soft edge, default 0 = hard. It is
  not AA.
- **Back-face cull** (`_Cull: 2` on the Lit and Unlit line materials, stock URP) — matches the fill. The
  mesh-boundary winding reversal makes ribbons Unity-front, so stock Back culls far-side globe lines.
- **Naming rule:** never name an internal shader prop after a MapLibre style term; the style binder writes the
  style value into it (the `_Blur` = `line-blur` collision, `docs/lessons-learned.md` § "Shaders & HLSL").

---

# 3. Lit rendering

Map geometry renders with Unity's **standard URP Lit (PBR) lighting model** by default; a flat
[Unlit variant](#unlit-variant--a-gpu-cheap-render-path) follows the same recipe against URP Unlit.
Appearance is driven by **both** MapLibre paint properties (color, width, opacity, …) **and** Unity Lit material
properties (metallic, smoothness, emission, normal maps, …) — both first-class. A basic URP Lit material + our
per-layer vertex logic + our styling as material properties. Style changes are material property changes — **no
mesh rebuild** (e.g. `material.SetFloat("_Width", …)` restyles line width live). This is the reference design
every map-geometry shader follows. Clean-room note: this concerns Unity's own URP rendering system — not MapLibre
— so URP source/docs (URP 17.5 / Unity 6000.x) are fair reference.

## Decision: mirror-copy URP's structure, hand-written HLSL (NOT ShaderGraph)

ShaderGraph is ruled out: it cannot share one HLSL vertex function across N per-layer graphs, gates the pass list
behind toggles, and is impractical for the complex per-layer logic (extrusion, fwidth AA, SDF symbols). We use
hand-HLSL, **mirror-copying URP's `Lit.shader` structure into `Shaders/` and extending it**:

- A thin `.shader` scaffold mirroring URP `Lit.shader`'s pass list; each pass `#include`s mirror-copied building
  blocks (`<Layer>_LitInput.hlsl` ← `LitInput.hlsl`; `<Layer>_<Pass>.hlsl` ← URP's pass files), kept
  verbatim-plus-our-delta so **upstream URP updates apply via `git diff`**.
- The **full** `UnityPerMaterial` (URP Lit's complete shape) **+ our map additions**; `SurfaceData` comes from
  `InitializeStandardLitSurfaceData`, **NEVER hand-assembled field-by-field**. Hand-assembling silently drops the
  entire URP Lit surface (base/normal/metallic/occlusion/emission/detail maps, alpha clip, workflow modes…); the
  reviewer greps for it. After `InitializeStandardLitSurfaceData`, the fragment **init-then-modulates**:
  `surfaceData.albedo *= _Color.rgb; surfaceData.alpha *= _Opacity;` — the only allowed pattern.
- We own **only** the per-pass vertex function (URP exposes no vertex hook), to inject the layer's vertex
  transform. Net result: the shader ≈ stock URP Lit + a vertex hook. If it looks wildly different, it's been
  stripped.
- Mirror-copied URP files are **our editable derivatives under `Shaders/`** with **UCL attribution headers** +
  a `THIRD-PARTY-NOTICES.txt` entry (Unity Companion License; not copyleft — see `ARCHITECTURE.md`
  § "Licensing posture"; `Assets/Code/ThirdParty/UnityCompanionLicense.txt` holds the committed text).
- Folder layout: `Shaders/Map/<Layer>/` (self-contained per layer; `Lit/` and `Unlit/` subfolders, with the
  pass files both share at the layer level), `Materials/Map/<Layer>/{Lit,Unlit}/` (template `.mat`), `Editor/`
  (custom `ShaderGUI` inspectors, `MapRenderer.Unity.Editor` asmdef). See `Shaders/README.md` for the file
  layout and naming rules.

Enforced by `ShaderStructureTests` + headless `LitFillSnapshotTests`: the complete `UnityPerMaterial` CBUFFER +
`InitializeStandardLitSurfaceData` per layer, the init-then-modulate pattern, the UCL headers, and the
`shader_feature_local _NORMALMAP` / `_METALLICSPECGLOSSMAP` pragmas so normal/metallic maps compile into the
variants.

## The multi-pass requirement (what a shallow "use Lit" misses)

URP Lit is a **multi-pass** material. The vertex transform (lateral extrusion, height, billboard, …) MUST be
applied **identically in every pass**, or the silhouette the shadow/depth/GBuffer sees diverges from the lit
pixels (shadow acne, depth mismatch, SSAO halos). Unity explicitly mandates sharing the vertex code via an
include/macro for vertex-moving shaders.

**Pass set** (mandatory minimum for a lit, shadowing, depth-participating map):
- `UniversalForward` (ForwardLit) — the visible lit pixels. **Mandatory.**
- `ShadowCaster` — casts shadows. **Mandatory** if the feature casts shadows. *Who actually casts is a
  render-kind decision — see "Render-layer model → Locked decisions → Shadows".*
- `DepthOnly` — camera depth prepass / `_CameraDepthTexture`. Needed when depth texture is on.
- `DepthNormals` — depth+normals prepass. Needed for SSAO.
- `UniversalGBuffer` — carried by **both Fill and Line as capability**.
- Meta / Universal2D / MotionVectors / XR — opt-in only (lightmapping / 2D / TAA / XR).

**Most of these passes are present but inert in the default configuration.** Flat fills and lines live in the
transparent queue band (see "Render-layer model"), which URP excludes from the opaque depth and GBuffer
prepasses; the renderer runs Forward+, so deferred never runs; and flat layers cast no shadows. They are kept
as capability, so each one goes live with the configuration change that needs it
(`docs/lessons-learned.md` has the per-pass reasons).

Each pass applies the layer's shared vertex transform identically — **Fill via the `MapVertexModify` hook, Line
via `Line_VertexExtrude.hlsl`** — before `TransformObjectToHClip`; the layer's `<Layer>_LitInput.hlsl` is
included by the `.shader` before the pass body. Reuse Unity's stock pass bodies (`LitForwardPass.hlsl`,
`LitGBufferPass.hlsl`, `ShadowCasterPass.hlsl`, `DepthOnlyPass.hlsl`, `LitDepthNormalsPass.hlsl`) and substitute
only the vertex math — version drift is then mostly keyword lists + Attributes/Varyings fields.

## Deferred vs. antialiasing

- **Flat fills and lines (transparent band):** forward-transparent, lit via `UniversalForward`. URP renders
  transparents in forward **regardless** of the deferred setting, and they sample the same lighting/SSAO as
  opaque geometry, so a deferred renderer stays a supported configuration. Their edge AA is shader coverage:
  the line straddle ("Line antialiasing") and the fill boundary band (`docs/fill-boundary-antialiasing-design.md`).
- **Fill-extrusion** writes depth; how it composes with the flat layers is `docs/depth-and-render-regimes-design.md`.
- **Coplanar z-fighting** between fills and lines on the flat plane: solve with render-queue / polygon offset /
  a tiny lift — NOT by changing the AA model.
- (MSAA-in-deferred and auto alpha-to-coverage are URP-version-sensitive; pin to the project's URP version.)

## Layer opacity & compositing

Every MapLibre paint layer has an `*-opacity`, and per-feature opacity is baked into the vertex alpha
(`StyledFill/LineTileBuilder` → `vColor.a`). For that alpha to composite, the painter contract uses straight
**alpha blending** (`Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha` — the 4-arg form carries
destination alpha correctly). An opaque layer (α=1) renders the same as a `One/Zero` blend would.

**The blend needs the transparent-surface keyword.** URP 17 deprecates the `_Surface` property:
`IsSurfaceTypeTransparent()` reads the `_SURFACE_TYPE_TRANSPARENT` keyword, and the fill forward pass ends with
`color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent())`, which returns 1.0 for an opaque surface. Without
the keyword the blend is a no-op, and a fill renders solid whatever its colour, opacity or pattern alpha.
`FillTweaker.ApplyPainterContract` enables the keyword on every fill clone. The base fill `.mat` also declares
it, because a player build strips a `shader_feature_local_fragment` variant that no material in the build
declares.

**Known limitation — translucent self-overlap accumulates.** Map data overlaps everywhere (casings under fills,
joins, a road crossing itself, multipart features). Per-primitive blending means a layer drawn at α<1 whose *own*
geometry overlaps composites those pixels twice and reads darker than its intended layer opacity. Opaque layers
never hit this. The fully-correct fix is to composite **the whole layer as a unit at its opacity** — render the
layer to an offscreen target, then blend that once. A separable piece of architecture, deferred until
semi-transparent overlapping layers are actually in use.

## Styling as material properties

- All style props (`_Width`, `_Color`, `_Opacity`, metallic/smoothness/emission, …) live in **one `CBUFFER`
  named `UnityPerMaterial`** in the shared header, identical in every pass (SRP Batcher requires this exact
  shape). Readable in vertex AND fragment of every pass — so `_Width` read in the vertex stage of
  ShadowCaster/DepthOnly is fine, *as long as the shared include is present in that pass*.
- **Data-driven paint: one side carries the value, the other holds the identity.** The shader multiplies a
  paint uniform by its vertex-stream counterpart, so writing the value into both applies it twice (a colour
  renders squared). A constant or zoom-only value rides the uniform, and the tile builder leaves the vertex at
  identity (a white `COLOR`; for a line, only the ribbon's own `WidthScale` factor). A data-driven value is
  baked per feature into the vertex stream (`fill-color` and `fill-opacity` into `COLOR`, `line-width` into
  `WidthScale`), and the uniform holds the identity base:
  `_BaseColor` white from the painter contract, `_Opacity` bound to a constant 1, `_Width` bound to a
  device-px constant 1. The base is a binding, not a direct `SetFloat`, so the layer-fade gate stays the only
  writer of `_Opacity`. The device-pixel ratio rides the uniform, never the bake
  (`docs/device-pixel-ratio-design.md` § "The px-valued surface").
- **Per-layer variation: use a separate Material instance per layer** (`material.SetFloat(...)` restyles live).
  **NEVER `MaterialPropertyBlock`** on batched renderers — it silently disables the SRP Batcher + GPU instancing.
- **Adopt the DOTS-instanced-property pattern from the start** (the BatchRendererGroup path the project targets):
  declare each style prop in the CBUFFER AND as a `UNITY_DOTS_INSTANCED_PROP`, with the redefine macro, so the
  same names work under SRP Batcher and BRG. Use `MaterialPropertyMetadata` (URP built-ins) or
  `UserPropertyMetadata` for fully custom props.

## Geometry contract

- **World-space extrusion (REQUIRED).** The vertex transform must extrude correctly under **arbitrary
  object→world transforms** (tile transforms, floating-origin rebasing) — extrude in world space, or transform
  the extrusion vector by the object→world matrix. Object-space extrusion before `TransformObjectToHClip` is
  correct **only under an identity transform** and breaks under real tile transforms.
- **Normals:** the lighting/surface normal is per-vertex on the `NORMAL` stream — **+Y** on a flat projection,
  where map geometry lies on XZ, and the surface up on the globe, with no shader change between them. The
  extrusion direction is a **separate** per-vertex attribute. Do NOT overload the lighting `NORMAL` channel
  with the extrusion direction. (GBuffer stores the surface normal; the offset only moves position — coherent.)
- **Tangents:** required only when sampling a normal/detail map. Minimal attrs:
  - Lit, no normal map: `POSITION` + `NORMAL(+Y)` + extrusion vector (in a free `TEXCOORD`) + UV0 if textured.
  - Lit, with normal map: add `TANGENT` (float4, `.w` = bitangent sign).
- Mesh must supply **UV0** (tile-space [0,1]) and **TANGENT** for normal/detail map sampling.

## Shared-skeleton structure

Each layer is self-contained under `Shaders/Map/<Layer>/`. The `.shader` drives includes in this order for every
pass: `<Layer>_LitInput.hlsl` (CBUFFER + DOTS bridge + `InitializeStandardLitSurfaceData`) → (Fill only)
`Fill_VertexModify.hlsl` (`MapVertexModify` body — fill-translate offset) → `<Layer>_<Pass>.hlsl` (vertex/fragment
entry points). Line does its extrusion via `Line_VertexExtrude.hlsl` (a shared helper included before every line
pass). Fill's input is `Fill_LitInput.hlsl`; Line's is `Line_LitInput.hlsl`. `Shaders/Common/LitInput.Template.hlsl`
is the reference skeleton a new layer's input is copied from; no `.shader` includes it.

**CBUFFER fork (Line).** The line needs its style-bound paint props (`_Width`, `_Blur` = line-blur, …) plus the
internal render param `_WidthIsPixels` in `UnityPerMaterial` in addition to the fill's — kept in two separate
labeled groups so style names never collide with internal ones. We cannot `#include Fill_LitInput.hlsl` and
append (the HLSL compiler rejects a duplicate `CBUFFER_START(UnityPerMaterial)`), so `Line_LitInput.hlsl` is a
verbatim fork of `LitInput.hlsl` (full URP Lit CBUFFER body copied, then line additions appended;
`InitializeStandardLitSurfaceData` also duplicated verbatim). The duplication is correct: `git diff` against URP
upstream stays meaningful for both files.

## Gotchas to design around

- Coplanar fill-vs-line z-fighting — mitigated by a tiny lift (~`0.001` world-m) along the **per-vertex surface
  normal** (`upWS`, from the mesh `NORMAL` stream — not a hardcoded `+Y`, so it holds on the globe too) in the
  line's vertex function, added to the world-space offset before rounding back to object space (in addition to
  the `Queue=Transparent`/`ZWrite Off` painter ordering). Invisible at any map scale.

## Material inspector — clean-room, not a URP subclass

The material GUI is a **clean-room reimplementation**, independent of URP's editor code — the editor asmdef
(`MapRenderer.Unity.Editor.asmdef`) references only `Unity.RenderPipelines.Core.Editor` / `Core.Runtime`, **not**
`...Universal.Editor`. It reproduces the familiar URP-Lit inspector layout — **Surface Options / Surface Inputs /
Advanced** foldouts — using Unity's public `MaterialHeaderScopeList` UI utility (Core.Editor), **without**
subclassing or copying URP's `BaseShaderGUI` / `LitShader` / `LitGUI`. (This is the one place we *reimplement*
rather than mirror-copy: URP's `LitShader` GUI is `internal`/unsubclassable, so copying it isn't even an option —
and the shader-side verbatim fork above already carries the UCL attribution; the GUI carries none.)

Three-level hierarchy, each level adding one foldout and its share of keyword sync:

- **`BaseShaderGUI : ShaderGUI`** (the RAW `UnityEditor.ShaderGUI`, `abstract`) — draws Surface Options / Surface
  Inputs / Advanced; owns the **surface-level keyword sync** in `ValidateMaterial` (mirroring the *behaviour* of
  URP's `SetMaterialKeywords`, not its source). Foldout bits `SurfaceOptions | SurfaceInputs | Advanced`
  (Advanced collapsed by default). *(Named `BaseShaderGUI` in our own namespace — distinct from URP's
  `UnityEditor.BaseShaderGUI`, which this assembly does not reference.)*
- **`LitShaderGUI : BaseShaderGUI`** (`abstract`) — adds the **Detail Inputs** foldout (detail mask / albedo /
  normal) and extends `ValidateMaterial` with the Lit + Detail keywords.
- **One `sealed` concrete inspector per shader** — `FillShaderGUI` / `LineShaderGUI` / `FillExtrusionShaderGUI`
  derive from `LitShaderGUI`; the Unlit twins (`FillUnlitShaderGUI`, `LineUnlitShaderGUI`,
  `FillExtrusionUnlitShaderGUI`) derive from `BaseShaderGUI`, since an unlit shader has no Detail inputs. Not
  one shared editor gated on `HasProperty`. Each adds one geometry-specific foldout via
  `AddScope("Map — <Layer>", Expandable.MapFeature, …)`: Fill draws its opacity / outline-colour / antialias
  props; Line draws `_Width` / `_Blur` / `_GapWidth` / `_LineOffset` / dash / translate props; Fill-extrusion
  draws its opacity / height / base / translate props. Their **runtime** render-state contracts live in
  `FillTweaker` / `LineTweaker` / `FillExtrusionTweaker`, not the GUI.

`Expandable.MapFeature` is the persisted expand-state bit for the feature foldout (outside URP's `Expandable`
range, so no collision).

**Manual check (not loop-closable):** opening a template `.mat` under `Materials/Map/` and confirming the
inspector renders the full Lit-style UI plus the feature section (and that editing affects the material) is a
human step — the headless loop cannot verify inspector UI.

## Unlit variant — a GPU-cheap render path

**What it is.** A **lightweight Unlit render path** beside the Lit one: flat-shaded map geometry that skips PBR
lighting entirely (`Map/FillUnlit`, `Map/LineUnlit`, `Map/FillExtrusionUnlit`; `FillUnlit` also serves
background). It trades the lit look (metallic/smoothness/normal maps, real-time shadows, SSAO, deferred) for a
smaller GPU cost: fewer passes → fewer shader variants → no lighting/shadow work, cheaper fragments. The target
is mobile / low-end / battery-sensitive deployments, or any scene where flat colour is acceptable and frame
budget is tight.

**The same recipe against URP `Unlit.shader`.** The Lit shaders are *stock URP `Lit.shader` mirror-copy + our
per-layer vertex hook + styling-as-material-properties*; each Unlit shader applies that recipe to URP
`Unlit.shader` (vendored as a reference template under `Shaders/Unity/Unlit/`):
- A mirror-copy of URP `Unlit.shader`'s pass list per layer under `Shaders/Map/<Layer>/Unlit/`, UCL-attributed
  the same way, with the **identical** vertex hook (`Fill_VertexModify.hlsl` / `Line_VertexExtrude.hlsl`), so
  extrusion, fill-translate, the z-fight lift, and floating-origin rebasing behave as in Lit.
- **Styling as material properties** and the DOTS-instanced property bridge (BRG-compatible) stay; only the
  *lighting* side of the fragment goes away. The fragment is flat albedo × `_BaseColor` × vertex colour, and
  alpha × `_Opacity` — no `SurfaceData`.
- **Three passes:** `UniversalForward` (unlit), `DepthOnly`, `DepthNormalsOnly`. The depth passes reuse the
  layer's shared depth-pass files verbatim. There is **no `ShadowCaster`** — stock URP Unlit has none, and an
  unlit material cannot cast a shadow. There is no GBuffer pass.

**Selection is a material-set substitution.** `MapViewConfig.MaterialSet` references a `MapMaterialSet`, and
that set's `RenderMode` (`Lit` = 0, the default; `Unlit`) puts the whole view in lit or unlit mode. Under
`Unlit` the host skips the ambient-probe setup. It still ensures a directional light, because the unlit
fill-extrusion reads its direction for a cheap half-Lambert so buildings do not render as flat blocks; flat
fills and lines ignore it. Materials are owned per layer by the material factory, and styling is a
material-property swap, so the mesh build, the render-layer model, and the backends do not change. Both sets
read the same `UnityPerMaterial` style props, so a style JSON drives either one the same way.

**Invariants.** Same vertex transform in every retained pass (or shadow/depth desync — see "The multi-pass
requirement"); no `MaterialPropertyBlock` on batched renderers; DOTS-instanced props for the BRG path;
`git diff` against URP `Unlit.shader` upstream stays meaningful (verbatim-plus-delta).

## Key references (Unity URP)

- URP `Lit.shader` (authoritative pass list): https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader
- URP `Unlit.shader` (template for the Unlit variant above): https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Unlit.shader
- ShaderLab Pass tags; Deferred rendering path; Depth-only pass (share vertex code across passes); SRP Batcher
  compatibility; DOTS-instanced shader properties; BatchRendererGroup shaders — Unity 6000.x URP manual.

---

# 4. Render-layer model

`ARCHITECTURE.md` §"Layer ordering": *"the style is an ordered list of layers, composited in order."* The
`IRenderLayer`/`RenderLayerSet` model makes that true for **everything the style can paint**: every painted
layer takes a uniform **global draw order** (its slot in the one ordered painter's chain) and declares its
own **shadow casting** (whether it is drawn into the shadow map) — the two cross-kind contracts. How a
layer's geometry comes to exist differs by kind and is NOT a member of the interface; production reads it
off the layer's runtime type instead (see "Per-kind pinning" below). Every kind today is also redrawn by
its own backend or persistent renderer, with no per-camera orchestrator re-issuing the draw — a uniform fact
of the current implementers, not a member either.

Fill, line, symbol, background and fill-extrusion are all first-class in the one ordered model. Raster is a
**reserved seat** — one class + one factory arm, not built.

## Per-kind pinning

Every painted layer declares, via `IRenderLayer`:
- **global draw index** — its slot in the one ordered painter chain.
- **shadow casting** — `IRenderLayer.CastShadows`, a `ShadowCastingMode` the backends transport verbatim
  (see "Locked decisions → Shadows").

A layer's geometry-build path is not a member of the interface — production dispatches on the layer's
runtime type instead, and there are three paths:
- **Feature-driven, per tile** — `ITileMeshRenderLayer` (fill, line, fill-extrusion): built once per
  `(tile, layer)` by the Burst pipeline off the tile's selected features, registered with the backend.
  `TileManager.ComputeDenseLayerIds` dispatches on this interface.
- **Source-less, per tile** — `BackgroundRenderLayer`: no features to select, so
  `TileManager.KickSourcelessBackground` meshes its per-covered-tile quad directly via `TileBuildGraph`.
  `TileManager` dispatches on this CONCRETE TYPE — `BackgroundRenderLayer` does NOT implement
  `ITileMeshRenderLayer`.
- **Frame-placed** — `SymbolRenderLayer`: rebuilt every frame from screen-space placement (global collision
  → per-slot billboard mesh), never the Burst tile pipeline.

| Layer kind | Geometry build | Draw slot |
|---|---|---|
| **Fill** | `ITileMeshRenderLayer` — once per `(tile,layer)`, Burst kernels, `Mesh.MeshData`; backend redraws (BRG `OnPerformCulling` / EG entities / MeshRenderers) | global index |
| **Line** | `ITileMeshRenderLayer` (same) | global index |
| **Symbol/text** | Frame-placed — global collision → per-slot billboard mesh rebuilt every `Tick`; a persistent per-slot `MeshRenderer` (`WorldSymbolRenderer`) swaps its mesh each `Tick`, so the backend redraws it with no orchestrator | global index **per symbol layer** |
| **Background** | Source-less, per-covered-tile (`BackgroundQuad` + `TileBuildGraph`) — dispatched by concrete type, NOT `ITileMeshRenderLayer` | global index |
| **Fill-extrusion** | `ITileMeshRenderLayer` (+ ZWrite on) — `FillExtrusionRenderLayer` / `StyledFillExtrusionTileBuilder` | global index |
| **Raster** *(reserved seat, not built)* | Per-tile textured quad — feature-driven vs. source-less dispatch is open (see "Non-goals / open questions") | global index |

`ITileMeshRenderLayer` layers and `BackgroundRenderLayer` both participate in the tile produce/consume loop
and the `ITileRenderBackend`; `SymbolRenderLayer` participates in the per-frame placement loop instead. What
is **uniform** across all five kinds is registration (one factory), draw order (one index), material
ownership, and `ApplyZoom`. `ARCHITECTURE.md`'s "two geometry classes" is this build distinction — symbol
build is per-frame collision, NOT the Burst mesh pipeline.

## Interface shape

`IRenderLayer` is the base (the uniform members); `ITileMeshRenderLayer` is the one capability interface a
feature-driven layer (fill, line, fill-extrusion) also implements. Abridged — `IRenderLayer.cs` and
`ITileMeshRenderLayer.cs` carry every member and its contract:

```csharp
internal interface IRenderLayer : IDisposable
{
    StyleLayer        StyleLayer      { get; }
    int               DrawIndex       { get; }  // the slot: backend materialIndex; stable across an in-place restyle
    ShadowCastingMode CastShadows     { get; }  // transported verbatim by every backend
    LayerSubSlot      MaterialSubSlot { get; }  // Base, or Above for symbol text over its own icon
    Material          Material        { get; }  // owned by the layer; queue = draw-order band + MaterialSubSlot
    void SetDrawOrder(int declaredOrder);       // stamps the queue band: LayerDrawOrder.QueueFor(order, subSlot)
    void ApplyZoom(in StyleFrameInputs inputs);
    void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds);
}

// Feature-driven tile-mesh capability (fill, line, fill-extrusion). Every implementer meshes on the job graph;
// there is no synchronous mesh-write member. Background is ALSO built once per tile, but does NOT implement
// this interface — it has no features, so TileManager.KickSourcelessBackground schedules its quad directly,
// dispatched by concrete type (see "Per-kind pinning" above).
internal interface ITileMeshRenderLayer : IRenderLayer
{
    ILayerMeshBuild BuildGraphRequest(IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
        in TileLayerProcessContext context, int materialIndex, string payloadName);
}
```

Concrete: `FillRenderLayer`, `LineRenderLayer`, `SymbolRenderLayer`, `BackgroundRenderLayer`,
`FillExtrusionRenderLayer`; a raster layer would be one more. **Adding a feature-driven kind (fill-extrusion's
shape) is one class + one `RenderLayerFactory` arm** — no edits to the layer set, the backends, or the tile
consume loop, because those dispatch on `ITileMeshRenderLayer`, not the concrete type. A source-less kind
(background's shape) is NOT this cheap: `TileManager` already branches on `BackgroundRenderLayer`'s own
concrete type in three places (the background material scan, `KickSourcelessBackground`, the
has-a-background check), so a second source-less kind would need its own such branches too.

## Locked decisions

- **One global draw-order index over ALL painted layers.** `RenderLayerSet.Build` walks `style.Layers` once and
  numbers every painted layer — fill, line, symbol, background, fill-extrusion (and raster, if built) — each
  owning a queue BAND (`LayerDrawOrder.QueueFor(declaredOrder, subSlot)`): fill/line/background use only
  `LayerSubSlot.Base`; a symbol layer's icon takes `Base` and its own text `Above`, so the badge always draws
  under the number it frames. `RenderLayerSet.Build` makes slot == draw order == material index, and the list
  contains every painted layer. An in-place restyle keeps each surviving layer's slot (`DrawIndex`) and
  restamps its band from the new declared order (`SetDrawOrder`), so the two can diverge
  (`docs/tile-pipeline-design.md` § "Partial-survival restyle — slot vs draw order, the tombstone, the three
  exits"). Only genuinely unpainted kinds (unknown, unsupported) and unconfigured-material layers take no
  slot. `LayerDrawOrder`'s queues are monotonic under a 5000 ceiling, and a too-large style throws rather than
  saturating. The 2-sub-slot band gives a runway of ~1000 painted layers; realistic styles stay far below it.
- **Backends get the full-width material list aligned to the global index, with null at non-tile slots** (those
  slots never receive `AddTileLayer`). Two backend touch points: BRG's ctor `RegisterMaterial` loop must skip
  nulls; the Entities prototype seed must use the **first non-null** entry (slot 0 may be a background layer).
  The tile produce loop filters `layer is ITileMeshRenderLayer`; the payload carries its own `MaterialIndex`, so
  consume is indifferent to gaps, guarded by `(uint)materialIndex >= _layers.Count`.
- **Global symbol collision, per-layer symbol draw** (MapLibre semantics). Collision stays ONE cross-layer pass
  (`CollisionJob` over all candidates — a road name and a city name keep competing in one greedy pass); each
  symbol layer draws its own survivors at its own queue via its per-slot mesh/material. `SymbolPlacementSystem`
  partitions survivor quads by `ShapedSymbol.MaterialIndex` into per-slot meshes; slot `g` IS the g-th declared
  symbol layer. `SymbolRenderLayer`'s icon material gets `QueueFor(declaredOrder, Base)` and its text material
  `QueueFor(declaredOrder, Above)` (the icon always draws under its own layer's text), so two symbol layers on one
  feature still draw in style order, band before band.
- **Per-layer materials live on the layer object, one owner.** Every layer owns its material clone;
  `SymbolRenderLayer`'s includes the halo bind, and the label subsystem consumes the layer set's materials
  instead of cloning its own.
- **Shadows: `fill-extrusion` casts; everything else only receives.** Scope is per-render-KIND and global.
  Per-style-layer control is not part of this model (an open item, UMR-98), and neither is a `_CastShadows`
  material toggle (a per-material switch whose only job is to turn casting off IS per-layer control).

  | Geometry | Casts | Receives | Why |
  |---|---|---|---|
  | `fill-extrusion` (buildings) | **On** | yes | Has volume; the shadow is meaningful, and it is the point. |
  | `fill` (ground-draped) | Off | **yes** | Coplanar with the ground ⇒ casting is a known acne source and buys nothing. Receiving is what makes building shadows land on parks/water. |
  | `line` (roads) | Off | **yes** | Same reasoning. Receiving is the visible payoff — building shadows crossing roads. |
  | `background` | Off | yes | A ground quad; same class as fill. |
  | symbols / text | Off | **no** | Camera-facing billboards at a fixed offset above ground: a cast shadow would be a floating dark quad, and a received one would darken the glyphs it exists to make legible. |

  The declaration lives on `IRenderLayer.CastShadows`, is collected by `TileManager.LayerShadowModes` into a
  full-width per-slot list beside the material list, and is TRANSPORTED by all three backends — never
  re-derived from the layer type or material, which is how three backends drift apart. Backends express it
  differently and must agree: the GameObjects backend binds the two renderer flags **per rent** (a pooled node
  outlives one layer's tenancy), Entities keeps **two Prefab prototypes** over one `RenderMeshArray` value
  (`RenderFilterSettings` is shared-component data, so a per-entity write would be a structural change),
  and BRG run-length groups its already-ordered draw commands into one `BatchDrawRange` per cast mode AND
  drops non-casters from the emit order on a `BatchCullingViewType.Light` pass — that filter is what makes
  "only fill-extrusion casts" assertable in EditMode without a GPU.

  **Known, accepted limitation:** a shadow-casting material in the transparent queue casts a **fully opaque**
  shadow regardless of `fill-extrusion-opacity` — the `ShadowCaster` pass is opaque. Not a bug; the fix would
  be dithered/transparent shadows, which is not in scope.
- **Layer-kind dispatch has one registry.** `RenderLayerFactory` is the only type-switch;
  `MapView.BuildSourceSpecs` ("which sources to fetch") and the label subsystem ("which layers are mine") derive
  from the built `RenderLayerSet`, not from re-walking `style.Layers` with their own `is` checks.

**The numbering.** Queue = `3000 + drawIndex × 2 + subSlot`. For a declared interleave `background,
fill(land), line(road), symbol(road-label), fill(building), symbol(place-label)`:

```
background=3000  land=3002  road=3004  road-label=3006/3007  building=3008  place-label=3010/3011
                                       (icon/text)           └── building fill occludes road labels;
                                                                 place labels on top — as declared
```

Symbols take their declared slot rather than one overlay queue above everything, so the mutual order of two
symbol layers, and of a symbol layer and the layers around it, is the style's.

## Symbols: drawn by persistent renderers

Symbols draw as **persistent per-slot `MeshRenderer`s** (`WorldSymbolRenderer`), not immediate-mode
`Graphics.RenderMesh`. `Tick` does everything up to and including the per-slot mesh write (project → collide →
fade/emit → mesh upload) and swaps the renderer's mesh; Unity redraws it every camera
render automatically. This: (i) avoids the Editor "blink" of immediate mode (a Game-View repaint without the
player loop gets no re-submission) with **no `beginCameraRendering` orchestrator**; (ii) inherits deterministic
`renderQueue` ordering against BRG tiles (URP's `CommonTransparent` sort ranks `renderQueue` above the
camera-distance tiebreak); (iii) is headless-testable, so ordering and redraw are checked by snapshot tests
rather than manual Editor checks. The shader's `"Queue" = "Overlay"` tag is only the demo/no-style fallback
default; `ZTest Always` is the symbol regime (`docs/depth-and-render-regimes-design.md`).

A future `CommandBuffer`/`ScriptableRenderPass` fallback (e.g. a symbol needing draw state a `MeshRenderer`
can't express) would need a `beginCameraRendering` orchestrator to re-issue its draw every render — no
implementor today.

## Background: a real layer, not a camera hack

`BackgroundRenderLayer` parses `background-color` / `background-opacity` into a typed `Background.PaintProperties`
(Core, the Fill pattern) and owns **only** a material — a clone of the FILL base
(`MaterialFactory.CreateBackgroundMaterial`; reusing the fill shader's flat lit path keeps the ground look
consistent with fills). Colour binds as a uniform (`_BaseColor`/`_Opacity`) over white vertex colours (the
line-colour pattern — background has no features to bake per-vertex). Geometry is **per-covered-tile**:
`BackgroundQuad` synthesizes one full-tile-extent quad per covered tile, and
`TileManager.KickSourcelessBackground` hands it to `TileBuildGraph`, which meshes it as a fill layer
(`FillLayerBuild`) — earcut winding and globe subdivision for free; each is then registered with the active
`ITileRenderBackend`, like fill/line. So the background is **projection-correct** (flat on Mercator, curved on
the globe) and a mid-stack background occludes layers below it and not above. `MapHost`'s camera clear colour
is only the above-horizon clear / no-style default. (The ±85.05°–90° polar caps have no surface — a
globe/tile-cover limitation shared by fill/line, owned by `docs/projection-globe-track-design.md`.)

## Fill-extrusion and the raster seat

- **Fill-extrusion** = per-tile, backend-redrawn, ZWrite on. One `ITileMeshRenderLayer` class + one factory
  arm. Both the roof and the walls mesh on the job graph: `FillExtrusionMeshGraph.Schedule` builds both, owned
  by `TileBuildGraph.LayerBuild` via `ITileMeshRenderLayer.BuildGraphRequest`, with no managed prologue step.
  How its depth composes with the transparent band is `docs/depth-and-render-regimes-design.md`.
- **Raster** (not built) = per-tile, backend-redrawn: a quad per tile, textured from a raster source, registered
  via `AddTileLayer`. The model change is zero — the OPEN problem is per-tile texture binding under the
  per-layer-material invariant (per-layer materials are shared across tiles; a raster tile needs its own
  texture → texture-array / BRG per-instance texture id / per-tile material).

## Non-goals / open questions

- Symbol BUILD stays per-frame placement — never folded into the Burst tile-mesh pipeline. The three tile
  backends stay engines that redraw tile meshes on their own; they learn nothing about symbols or
  backgrounds (null slots aside).
- **Line-following labels behind buildings.** Symbols stay `ZTest Always`, and "labels unoccluded by 3D" is the
  point-label default. Whether line-following labels should be hidden behind fill-extrusion is open.
- **Raster per-tile texture vs per-layer material** — open; decided with the raster layer.
- **Raster's dispatch path.** `ITileMeshRenderLayer` is now feature-driven (it selects features to mesh), and
  a raster tile has none — it is a fetched texture, not a Burst mesh build. Whether raster follows the
  feature-driven path or background's source-less, dispatch-by-runtime-type path is open, decided with the
  raster layer.
