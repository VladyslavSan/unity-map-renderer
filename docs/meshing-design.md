# Meshing — design & SSOT

How decoded vector-tile geometry becomes drawn, lit meshes: the per-kind build pipeline, line antialiasing, the
URP-Lit shading convention every map shader follows, and the render-layer model that gives every painted style
layer a uniform place. Read this before touching `FillMeshPipeline`, `StyledFillTileBuilder`,
`StyledLineTileBuilder`, `LineRibbonJob`, the map shaders under `Shaders/Map/`, or `IRenderLayer`/`RenderLayerSet`.

1. **[Mesh pipeline](#1-mesh-pipeline)** — how MVT bytes become a mesh, per geometry kind (fill vs line orderings, the build/consume loop).
2. **[Line antialiasing](#2-line-antialiasing)** — why there is no edge AA, the trilemma, the single-pass-cased-line path forward.
3. **[Lit rendering](#3-lit-rendering)** — the shared URP-Lit (PBR) shader convention: mirror-copy structure, multi-pass, styling as material properties.
4. **[Render-layer model](#4-render-layer-model)** — `IRenderLayer`/`RenderLayerSet`: every painted kind first-class along three orthogonal axes.

---

# 1. Mesh pipeline

The path from decoded vector-tile geometry to a GPU `Mesh`, per geometry kind.

## The word "tessellation" is retired

"Tessellation" used to name **four unrelated things**, which is exactly why this path was hard to reason about.
The only survivor is `LineTessellator`, where "tessellate" now means strictly **triangulate**.

| Former loose meaning | Now called | Lives in |
|----------------------|-----------|----------|
| the whole decode→mesh chain | **the mesh pipeline** | `FillMeshPipeline`, the `StyledFill/LineTileBuilder`s |
| inserting curvature points (globe) | **Subdivide** | `SubdivideCenterline`, `GlobeFillSubdivideJob` |
| earcut / ribbon-offset | **Triangulate** (the only surviving "tessellate") | `Earcut`, `LineTessellator` (oracle), `LineRibbonJob` |
| "build one whole tile's mesh" (async scheduling) | **mesh build** (worker) + **consume** (main thread) | `TileManager` (`KickMeshBuild`, `MeshBuildTask`, `MaxMeshBuildsPerTick` / `ConsumeMeshBuild`, `MaxConsumesPerTick`) |

That last row is the tile-build loop — two verbs, no "phases":
- **build** the mesh — kick a background task that runs the mesh pipeline off-thread, producing `Mesh.MeshData`.
- **consume** it — upload the built data to a GPU `Mesh` + place the render object, on the main thread.

`MaxMeshBuildsPerTick` throttles builds and `MaxConsumesPerTick` throttles consumes.

## The named responsibilities (inside one mesh build)

- **Decode** — MVT command bytes → integer tile-space rings. (`MvtDecodeJob`.)
- **Assemble** — classify rings into polygons + holes (outer/hole/skip by signed area). (`RingAssemblyJob`.)
- **Subdivide** — insert extra points so a straight edge in tile space follows a *curved* projected surface.
  Driven entirely by `IProjection.MaxRefineAngleRad` (∞ for Mercator ⇒ no split; a small angle for the globe).
  No projection constants leak in — the flat case is the degenerate value of one formula, not a branch.
- **Triangulate** — rings/centerline → triangle vertices + indices. Fills use ear-clipping (`Earcut`/`EarcutJob`);
  lines extrude a ribbon (`LineRibbonJob`, with `LineTessellator` as its planar differential oracle).
- **Project** — tile-space → geodetic surface (`TileToGeoJob`, projection-independent) → render-space `double3` +
  per-vertex `up`, through the chosen `IProjection` (`ProjectPointsJob<TProj>`).
- **Write** — stream the vertices/indices into a caller-allocated `Mesh.MeshData` on the worker; the main thread
  only **allocates** (at kick) and **applies** (at consume/upload).

## There is no single linear order — fills and lines compose these differently

This is the load-bearing point. **Triangulate-vs-Project is _opposite_ between the two kinds, and Subdivide sits
in a different slot for each.** Baking one universal sequence into the names would be a worse model than the
overload it replaced.

| Kind | Order | Where |
|------|-------|-------|
| **Fill** | Decode → Assemble → **Triangulate** (earcut, flat tile space) → **Project** → *(globe only)* **Subdivide** | `FillMeshPipeline.Schedule` does Decode→Assemble→Triangulate→Project; `StyledFillTileBuilder` adds the globe Subdivide (`GlobeFillSubdivideDispatch`, gated by `!double.IsInfinity(proj.MaxRefineAngleRad)`) then writes the mesh. |
| **Line** | Decode → **Subdivide** (centerline, tile space) → **Project** → **Triangulate** (ribbon) | `StyledLineTileBuilder.WriteMeshData` (called via `LineRenderLayer.WriteInto`): `SubdivideCenterline` → per-point `TileToGeoJob` + `IProjection.ProjectPoint` → `LineRibbonJob` → writes the mesh. |

**Why fills Triangulate *before* Project.** Ear-clipping is a **planar 2D algorithm**, and triangle
**connectivity is projection-invariant** — which vertices form a triangle doesn't change when you bend the sheet
onto a globe. So earcut runs once in exact integer tile space (cheapest, most robust), and Project moves the
resulting vertices afterward. On the globe a post-Project **Subdivide** then refines the now-curved triangles.

**Why lines Triangulate *after* Project.** The ribbon extrusion needs the **projected 3D centerline plus its
`up`** to build the cross-section: `across = normalize(cross(along, up))`, tying the ribbon width to the same
`up` the centerline was projected with (**winding consistent by construction** across projections — no
per-projection flip; the uniform Unity-front reversal for stock Cull Back happens at the mesh-write boundary,
see [`coordinates-and-projections.md` §7.1](coordinates-and-projections.md) and §8). So the centerline
is subdivided and projected first, then `LineRibbonJob` triangulates in render space.

## Threading & lifetime (both kinds)

The Burst jobs run via `.Run()` **on the mesh-build worker thread**, not the main thread — only `Mesh.MeshData`
**Allocate** (at kick) and **Apply** (at consume) are main-thread. Mesh *data* (`NativeArray` /
`TileMeshBuffers` / `MeshData`) is a value-type struct disposed deterministically at the apply boundary; the
`Mesh` is the single-owner class. Full contract: [`async-architecture.md`](async-architecture.md) §"Disposal &
cancellation contract" and the mesh-ownership rule in [`conventions-short.md`](conventions-short.md).

## Key types

| Type | Assembly | Role |
|------|----------|------|
| `FillMeshPipeline` | `MapRenderer.Jobs` | Fill: coordinates Decode→Assemble→Triangulate→Project; owns `LayerInput`. Produces `TileMeshBuffers`. |
| `TileRenderOrigin` | `MapRenderer.Core` | The single source of a tile's bake/RTC origin (SW corner projected). Engine-free, shared by fills/lines/symbols/camera — **not** fill-specific, so it lives in Core, not on `FillMeshPipeline`. |
| `TileToGeoJob` | `MapRenderer.Jobs` | Project stage part 1: tile-space → geodetic surface (projection-independent). Takes a `TileId`. |
| `LineRibbonJob` | `MapRenderer.Jobs` | Line Triangulate: projection-agnostic 3D ribbon from a `(point, up)` array. |
| `LineTessellator` | `MapRenderer.Core` | The planar differential **oracle** for `LineRibbonJob` (`LineRibbonJobTests`). |
| `StyledFillTileBuilder` | `MapRenderer.Unity` | Fill orchestration: color eval → `FillMeshPipeline` → globe Subdivide → write mesh. |
| `StyledLineTileBuilder` | `MapRenderer.Unity` | Line orchestration: Subdivide → Project → `LineRibbonJob` → write mesh. |
| `MeshDataPayload` / `IRenderLayerPayload` | `MapRenderer.Unity` | The per-`(tile, layer)` mesh handle the consume loop uploads + disposes. |
| `TileManager` | `MapRenderer.Unity` | The tile-build loop: `KickMeshBuild` / `MeshBuildTask` (build) and `ConsumeMeshBuild` (consume), throttled by `MaxMeshBuildsPerTick` / `MaxConsumesPerTick`. |

---

# 2. Line antialiasing

> **The active epic lives in [`line-antialiasing-design.md`](line-antialiasing-design.md)** — the chosen
> direction (a strict ±0.5 px analytical straddle, still alpha-blended), the acceptance teeth and the stage
> sequence. Note that the two "ways forward" sketched at the end of this section are both **closed**: fusion
> regresses junction draw order on real styles, and replace-compositing is ruled out by the no-depth and
> no-MSAA decisions recorded there. This section stays the record of *why* the removed AA failed; that doc
> is where the rebuild is designed.

**State:** line edge antialiasing is **removed** (commit `0b910c7`). Lines render with a **hard edge**, the
configured width drawn **solid**, thin lines held at ≥ 1 px by a **min-width floor**. `line-blur` (`_Blur`) is
kept as an opt-in soft edge; dashing is unchanged. This section is the SSOT for *why* there is no edge AA and
what a correct version looks like — it supersedes the earlier "opaque-core + feathered edge" AA model (which the
lit-rendering convention in §3 no longer describes).

## What the shader does now

`Line_VertexExtrude.hlsl` extrudes the ribbon to exactly the styled half-width (screen-space pixel width from the
projection measurement, `_MetersPerPixel` retired). `LineCoverage`:

- **Outer edge:** hard — the ribbon spans `|side| ≤ 1`, coverage is `1` up to the rasterized triangle silhouette.
  No feather.
- **Gap hole (cased/hollow lines):** hard cut at `|side| < innerFrac`.
- **`_Blur` (MapLibre line-blur):** opt-in inward soft edge, `0` ⇒ no-op. Not antialiasing.
- **Dash:** unchanged (its own along-line `fwidth` feather stays — a different aliasing axis).

The **only** thin-line safeguard is the min-width floor: half-width `≥ 0.5 px`, so a sub-pixel line renders a
stable 1 px hairline instead of dropping out.

## Why edge AA was removed, and how it came back — the trilemma, resolved

> **Superseded as a verdict, kept as history.** Edge AA was removed in `0b910c7` for the reasons below, and
> **restored** by the line-AA epic as a strict one-device-pixel straddle behind `_EDGE_ANTIALIASING_OFF`
> (`docs/line-antialiasing-design.md`, SHIPPED). The analysis of why the *removed* model failed is accurate
> and worth keeping; the conclusion that no shader fade can work is not. What follows is the original
> argument, with the resolution marked.

A shader-space alpha fade (inset / straddle / outset) cannot satisfy all three at once:

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

**The trilemma is what the shipped model resolves, and the table's "straddle" row is where it does it.** The
row is right that the outer half-pixel dims — but that is the *definition* of an antialiased edge, not a
failure of the solid core. The shipped straddle makes the ribbon `a == 1` everywhere from the centreline out
to half a pixel inside the styled edge, ramps 1 → 0 across the one pixel centred on that edge, and so keeps
the 50 % contour exactly on the styled width: **solid core ✅, apparent width unchanged ✅, antialiased ✅**.
Measured, both in pixel-width and world-unit-width mode: a styled 6.0 px line renders a coverage integral of
6.003 px. The "thin line goes soft" caveat is real and is handled by the pre-existing min-width floor, which
floors the *styled* half-width — the thing the ramp centres on.

Two corollaries:

- **A fade-*width* knob is not a quality dial.** Correct coverage-AA is a ~1 px transition. Widening the fade
  just **blurs** (trades sharpness for smoothness) — it never adds *resolution*. That is why widening it "helped
  but never fixed" the staircase.
- **Extruding geometry + hard-clipping `|side| > 1` does nothing.** With one sample per pixel, a pixel is lit iff
  its centre passes the boundary test; whether that test is the triangle edge or a fragment `discard(|side| > 1)`,
  it is the same test at the same point → the identical staircase. Only **more samples per pixel** (MSAA/SSAA) or
  a **coverage gradient** (the fade) put intermediate values on a diagonal.

## The cased-line objection — answered by an `a == 1` interior

A cased road (e.g. `road_motorway_casing` + `road_motorway`) is **two separate transparent draws**: the casing
(wider, drawn under) and the fill (narrower, drawn over), both `Queue=Transparent`, `ZWrite Off`,
painter-ordered. This is where every shader-fade AA fails, and it is a **compositing** problem, not a coverage
one:

- The fill's AA edge is a skirt that ramps to **transparent**. "Transparent" over the casing means **the casing
  colour bleeds through the fill's edge pixels** → the crisp fill↔casing boundary becomes a muddy blend band.
- The general form as originally stated: *"alpha-fade AA does not compose when transparent layers stack.
  No fade width — inset, outset, or straddle — escapes it."*

**MSAA does not fix this.** MSAA anti-aliases geometry *coverage*, but the fill and casing are still blended (not
replaced), so 4× samples just give a slightly cleaner version of the same mush. The problem was never coverage;
it is the compositing model.

> **[RESOLVED] The generalisation was too strong — it is true of an INSET fade, not of a straddle.** The
> removed model faded *inward from the styled edge*, so the fill's own styled band contained partially
> transparent pixels, and those sat over the casing's interior and bled it. A strict straddle confines the
> ramp to the one pixel centred on the edge and is `a == 1` everywhere inside that: there is no transparent
> region within the fill's band for the casing to show through. What ramps to transparent is the outer half
> pixel, which lies over the *casing's interior* — and blending a colour into `a == 1` casing at the
> boundary pixel is exactly what a correct antialiased boundary looks like.
>
> Pinned by tooth T2 (`CasedPair_InternalBoundary_StaysCrisp`): zero casing contribution anywhere ≥ 1 px
> inside the fill band, and at most one blended pixel per flank. RED-verified against a re-added inset fade,
> which bleeds **77.3 %** casing colour 3.75 px inside a 5 px half-width fill.
>
> The single-pass cased line below remains the better architecture and is still not built; it is no longer a
> prerequisite for antialiased lines.

## The way forward — single-pass cased line (not yet built)

The fix changes the **architecture**, not a shader knob. Two viable directions:

1. **Single-pass cased line (preferred).** Fuse the `*_casing` + main layers into **one** mesh / one draw. The
   fragment picks fill-vs-casing colour by **signed distance** from the centreline, antialiases only the
   **outer** silhouette, and treats the internal fill↔casing boundary as a hard colour swap (or a 1 px
   colour-to-colour AA). No transparent stacking → nothing to bleed. This is how SDF text and MapLibre-style
   casing work. Cost: the two style layers must be recognised and fused at build time.
2. **Opaque line compositing.** Draw lines opaque (`ZWrite On`, opaque queue, depth-offset off the fills) so the
   fill *replaces* the casing instead of blending, with MSAA for the outer edge. Crisp boundary because it is a
   replace. Cost: coplanar-depth handling against the fills.

MSAA is **orthogonal** and complementary once compositing is fixed: with hard geometry edges and no shader fade,
it cleans the outer silhouettes cheaply. It is currently off (`m_MSAA: 1`) in the URP assets.

## What was deliberately kept

- **Min-width floor** (`Line_VertexExtrude.hlsl`) — the sole thin-line safeguard now.
- **`_Blur` (line-blur)** — a real MapLibre paint property, opt-in soft edge, default 0 = hard. Not AA.
- **Dash** coverage and its along-line feather.
- **Back-face cull** (`MapLine.mat` `_Cull: 2`, stock URP) — matches `MapFill`; the mesh-boundary winding
  reversal makes ribbons Unity-front, so stock Back culls far-side globe lines (retired the double-sided
  globe-line workaround). Unrelated to AA but landed alongside.

A git **stash** holds the `_LINE_AA_OUTSET` outset-AA toggle + `sideScale` groundwork — useful raw material if
the single-pass route reuses a distance parametrization. Naming gotcha that still stands: never name an internal
shader prop after a MapLibre style term (the `_Blur` = `line-blur` collision) — see `docs/lessons-learned.md`
§ Shaders & HLSL.

---

# 3. Lit rendering

Map geometry renders with Unity's **standard URP Lit (PBR) lighting model**, not a custom unlit shader.
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
  a `THIRD-PARTY-NOTICES.txt` entry (Unity Companion License; not copyleft — see `ARCHITECTURE.md §4`;
  `Assets/Code/ThirdParty/UnityCompanionLicense.txt` holds the committed text).
- Folder layout: `Shaders/Map/<Layer>/` (self-contained per layer — shaders + includes), `Materials/` (template
  `.mat`), `Editor/` (custom `ShaderGUI` inspectors, `MapRenderer.Unity.Editor` asmdef). See `Shaders/README.md`
  for the current file layout and naming rules.

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
- `ShadowCaster` — casts shadows. **Mandatory** if the feature casts shadows.
- `DepthOnly` — camera depth prepass / `_CameraDepthTexture`. Needed when depth texture is on.
- `DepthNormals` — depth+normals prepass. Needed for SSAO.
- `UniversalGBuffer` — carried by **both Fill and Line as capability**. The line's GBuffer pass is
  **present-but-inert by default policy** (transparent queue → URP excludes it from the GBuffer prepass) until
  opacity-driven activation moves the line to an opaque queue.
- Meta / Universal2D / MotionVectors / XR — opt-in only (lightmapping / 2D / TAA / XR).

Each pass applies the layer's shared vertex transform identically — **Fill via the `MapVertexModify` hook, Line
via `Line_VertexExtrude.hlsl`** — before `TransformObjectToHClip`; the layer's `<Layer>_LitInput.hlsl` is
included by the `.shader` before the pass body. Reuse Unity's stock pass bodies (`LitForwardPass.hlsl`,
`LitGBufferPass.hlsl`, `ShadowCasterPass.hlsl`, `DepthOnlyPass.hlsl`, `LitDepthNormalsPass.hlsl`) and substitute
only the vertex math — version drift is then mostly keyword lists + Attributes/Varyings fields.

## Deferred vs. antialiasing

- **Fills (opaque):** Deferred-eligible — author the GBuffer pass; deferred lighting + SSAO + MSAA/auto
  alpha-to-coverage edge AA.
- **Lines (transparent):** forward-transparent, lit via `UniversalForward`. URP renders transparents in forward
  **regardless** of the deferred setting, and both sample the same lighting/SSAO — so mixing forward-transparent
  lines with deferred-opaque fills is the *supported* design. (Line edge AA itself is removed — see §2.)
- **Coplanar z-fighting** between fills and lines on the flat XZ plane: solve with render-queue / polygon offset /
  a tiny Y lift — NOT by changing the AA model.
- (MSAA-in-deferred and auto alpha-to-coverage are URP-version-sensitive; pin to the project's URP version.)

## Layer opacity & compositing

Every MapLibre paint layer has an `*-opacity`, and per-feature opacity is baked into the vertex alpha
(`StyledFill/LineTileBuilder` → `vColor.a`). For that alpha to actually composite, the painter contract uses
straight **alpha blending** (`Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha` — the 4-arg form carries
destination alpha correctly). Opaque layers (α=1) render byte-identically to the old `One/Zero` path; α<1 now
blends.

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
- **Normals:** on today's Mercator-only mesh bake, map geometry is flat on XZ → the lighting/surface normal is
  **+Y** (per-vertex on the `NORMAL` stream, so the globe track can supply non-+Y normals without a shader
  change); the extrusion direction is a
  **separate** per-vertex attribute in XZ. Do NOT overload the lighting `NORMAL` channel with the extrusion
  direction. (GBuffer stores the +Y world normal; the offset only moves position — coherent.)
- **Tangents:** required only when sampling a normal/detail map. Minimal attrs:
  - Lit, no normal map: `POSITION` + `NORMAL(+Y)` + extrusion vector (in a free `TEXCOORD`) + UV0 if textured.
  - Lit, with normal map: add `TANGENT` (float4, `.w` = bitangent sign).
- Mesh must supply **UV0** (tile-space [0,1]) and **TANGENT** for normal/detail map sampling.

## Shared-skeleton structure

Each layer is self-contained under `Shaders/Map/<Layer>/`. The `.shader` drives includes in this order for every
pass: `<Layer>_LitInput.hlsl` (CBUFFER + DOTS bridge + `InitializeStandardLitSurfaceData`) → (Fill only)
`Fill_VertexModify.hlsl` (`MapVertexModify` body — fill-translate offset) → `<Layer>_<Pass>.hlsl` (vertex/fragment
entry points). Line does its extrusion via `Line_VertexExtrude.hlsl` (a shared helper included before every line
pass). Fill's input is `Fill_LitInput.hlsl`; Line's is `Line_LitInput.hlsl`.

**CBUFFER fork (Line).** The line needs its style-bound paint props (`_Width`, `_Blur` = line-blur, …) plus the
internal render param `_WidthIsPixels` in `UnityPerMaterial` in addition to the fill's — kept in two separate
labeled groups so style names never collide with internal ones. We cannot `#include Fill_LitInput.hlsl` and
append (the HLSL compiler rejects a duplicate `CBUFFER_START(UnityPerMaterial)`), so `Line_LitInput.hlsl` is a
deliberate verbatim fork of `LitInput.hlsl` (full URP Lit CBUFFER body copied, then line additions appended;
`InitializeStandardLitSurfaceData` also duplicated verbatim). The duplication is intentional and correct;
`git diff` against URP upstream stays meaningful for both files.

## Gotchas to design around

- `MaterialPropertyBlock` silently kills the SRP Batcher + GPU instancing — per-layer materials / DOTS-instanced
  props instead.
- The vertex offset must be in EVERY pass via the shared include, or shadows/depth/SSAO desync.
- Don't overload the lighting `NORMAL` with the extrusion direction.
- Coplanar fill-vs-line z-fighting — mitigated by a tiny lift (~`0.001` world-m) along the **per-vertex surface
  normal** (`upWS`, from the mesh `NORMAL` stream — not a hardcoded `+Y`, so it holds on the globe too) in the
  line's vertex function, added to the world-space offset before rounding back to object space (in addition to
  the `Queue=Transparent`/`ZWrite Off` painter ordering). Invisible at any map scale.
- Deferred renders transparents (lines) in forward anyway; don't expect lines in the GBuffer.
- The pass list and MSAA-in-deferred / alpha-to-coverage behavior are URP-version-dependent; pin them.

## Material inspector — clean-room, not a URP subclass (S58)

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
  `UnityEditor.BaseShaderGUI`, which is no longer referenced.)*
- **`LitShaderGUI : BaseShaderGUI`** (`abstract`) — adds the **Detail Inputs** foldout (detail mask / albedo /
  normal) and extends `ValidateMaterial` with the Lit + Detail keywords. (The Detail foldout is **present**, not
  dropped.)
- **`FillShaderGUI` / `LineShaderGUI`** (both `sealed : LitShaderGUI`) — **separate concrete inspectors, one per
  shader** (not one shared editor gated on `HasProperty`). Each adds one geometry-specific foldout via
  `AddScope("Map — Fill/Line", Expandable.MapFeature, …)`: Fill draws its outline-colour / antialias props; Line
  draws `_Width` / `_Blur` / `_GapWidth` / `_LineOffset` / dash / translate props. Their **runtime** render-state
  contracts live in `FillMaterialTweaker` / `LineMaterialTweaker`, not the GUI.

`Expandable.MapFeature` is the persisted expand-state bit for the feature foldout (outside URP's `Expandable`
range, so no collision).

**Manual gate (NOT loop-closable):** opening `MapFill.mat` / `MapLine.mat` and confirming the inspector renders
the full Lit-style UI plus the feature section (and that editing affects the material) is a human step — the
headless loop cannot verify inspector UI.

## Unlit variant — a GPU-cheap render path (NOT IMPLEMENTED YET)

**Goal (not built).** Offer a **lightweight Unlit render path** alongside the Lit one, selectable per project —
flat-shaded map geometry that skips PBR lighting entirely. It trades the lit look (metallic/smoothness/normal
maps, real-time shadows, SSAO, deferred) for a much smaller GPU cost: fewer passes → fewer shader variants →
no lighting/shadow/depth-normals work, cheaper fragments. The target is mobile / low-end / battery-sensitive
deployments, or any scene where flat colour is acceptable and frame budget is tight.

**Why it's a small, self-contained job.** The Lit shaders are (by design, §3 above) *stock URP `Lit.shader`
mirror-copy + our per-layer vertex hook + styling-as-material-properties*. An Unlit variant is the **same
recipe against URP `Unlit.shader`** instead:
- Mirror-copy URP `Unlit.shader` per layer under `Shaders/Map/<Layer>/` (Unlit template), UCL-attributed the
  same way; keep the **identical** vertex hook (`Fill_VertexModify.hlsl` / `Line_VertexExtrude.hlsl`) so
  extrusion, fill-translate, the z-fight lift, and floating-origin rebasing behave exactly as in Lit.
- Keep **styling as material properties** (`_Color`, `_Opacity`, `_Width`, `_Blur`, …) and the DOTS-instanced
  property bridge (BRG-compatible) — only the *lighting* side of the fragment goes away; `surfaceData.albedo *=
  _Color.rgb; alpha *= _Opacity` collapses to a direct colour output.
- **Fewer passes:** `UniversalForward` (unlit) + optionally `DepthOnly`/`ShadowCaster` **only if** the scene
  still wants map geometry in depth / casting shadows; drop `UniversalGBuffer` / `DepthNormals` (no deferred, no
  SSAO). This is the bulk of the variant savings.

**Selection (not built).** A project/config switch (natural home: `MapViewConfig`) picks the **Lit** vs
**Unlit** material set; because materials are already owned per-layer by the material factory / `MapMaterialSet`
(styling is a material-property swap, not a mesh or pipeline change), this is a **material-set substitution** —
the mesh build, render-layer model, and backends are untouched. Both sets read the same `UnityPerMaterial`
style props, so a style JSON drives either identically.

**Invariants to preserve when it *is* built.** Same vertex transform in every retained pass (or shadow/depth
desync, §"multi-pass requirement"); no `MaterialPropertyBlock` on batched renderers; DOTS-instanced props for
the BRG path; `git diff` against URP `Unlit.shader` upstream stays meaningful (verbatim-plus-delta). Enforce
with an Unlit analogue of `ShaderStructureTests` + a headless snapshot test.

## Key references (Unity URP)

- URP `Lit.shader` (authoritative pass list): https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader
- URP `Unlit.shader` (template for the deferred Unlit variant above): https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Unlit.shader
- ShaderLab Pass tags; Deferred rendering path; Depth-only pass (share vertex code across passes); SRP Batcher
  compatibility; DOTS-instanced shader properties; BatchRendererGroup shaders — Unity 6000.x URP manual.

---

# 4. Render-layer model

`ARCHITECTURE.md` §"Layer ordering": *"the style is an ordered list of layers, composited in order."* The
`IRenderLayer`/`RenderLayerSet` model makes that true for **everything the style can paint**: every painted layer
gets a uniform place along three orthogonal axes — **build/lifetime** (how its geometry comes to exist), **draw
order** (its slot in the global painter's chain), and **presence** (whether a backend redraws it by itself or an
orchestrator must re-issue it). The axes are named and kept separate; the model does NOT pretend symbols build
like fills.

**Status:** fill, line, symbol, and background are all first-class in the one ordered model (shipped). Raster and
fill-extrusion are **reserved seats** — one class + one factory arm each, unscheduled (payload of the tile-
pipeline unification epic, `docs/per-layer-tile-processing-design.md`).

## The three axes, pinned per kind

Every painted layer declares:
- **build kind** — `TileMesh` (built once per tile, Burst pipeline, backend-registered) / `FramePlaced` (rebuilt
  every frame from screen-space placement). (A former `ViewGeometry` kind — a self-built view-refreshed mesh —
  was background's only user and is removed; the axis is `{ TileMesh, FramePlaced }`.)
- **draw persistence** — `Persistent` (a backend redraws it every render on its own) / `Immediate` (an
  orchestrator must re-issue it every camera render).
- **global draw index** — its slot in the one ordered painter chain.

| Layer kind | Build (lifetime) | Presence | Draw slot | State |
|---|---|---|---|---|
| **Fill** | `TileMesh` — once per `(tile,layer)`, Burst kernels, `Mesh.MeshData` | `Persistent` — backend redraws (BRG `OnPerformCulling` / EG entities / MeshRenderers) | global index | shipped |
| **Line** | `TileMesh` (same) | `Persistent` | global index | shipped |
| **Symbol/text** | `FramePlaced` — global collision → per-slot billboard mesh rebuilt every `Tick` | `Persistent` — a persistent per-slot `MeshRenderer` (`LabelSlotPresenter`) swaps its mesh each `Tick`, so the backend redraws it with no orchestrator | global index **per symbol layer** | shipped |
| **Background** | `TileMesh` — per-covered-tile, source-less (`TileBackgroundLayerProcessor`) | `Persistent` — one quad per covered tile, same as fill/line | global index | shipped |
| **Raster** *(future)* | `TileMesh` — per-tile textured quad | `Persistent` | global index | reserved seat |
| **Fill-extrusion** *(future)* | `TileMesh` (+ ZWrite on) | `Persistent` | global index | reserved seat |

The build kinds genuinely differ and the model **names** the difference: `TileMesh` layers participate in the
tile produce/consume loop and the `ITileRenderBackend`; `FramePlaced` layers participate in the per-frame
placement loop. What is **uniform** across all of them is registration (one factory), draw order (one index),
presence (one enum), material ownership, and `ApplyZoom`. `ARCHITECTURE.md`'s "two geometry classes" is this
build/lifetime axis — symbol build is per-frame collision, NOT the Burst mesh pipeline, and that is not changing.

## Interface shape

`IRenderLayer` splits into a base (the uniform axes) plus per-build-kind capability interfaces:

```csharp
internal enum RenderLayerBuild { TileMesh, FramePlaced }
internal enum DrawPersistence  { Persistent, Immediate }

internal interface IRenderLayer : IDisposable
{
    StyleLayer       StyleLayer  { get; }
    RenderLayerBuild Build       { get; }   // lifetime class — which loop feeds it
    DrawPersistence  Persistence { get; }   // who re-draws it each render
    int              DrawIndex   { get; }   // set once by RenderLayerSet.Build; band = LayerDrawOrder.QueueFor(DrawIndex, subSlot)
    LayerSubSlot     MaterialSubSlot { get; } // which sub-slot of the band Material occupies (Base, or Above for symbol text over its own icon — G7/D7)
    Material         Material    { get; }   // owned by the layer, queue encodes DrawIndex + MaterialSubSlot
    void ApplyZoom(double zoom);
}

// TileMesh capability — the mesh WriteInto (fill, line; later fill-extrusion, raster).
internal interface ITileMeshRenderLayer : IRenderLayer
{
    void WriteInto(Mesh.MeshData md, IReadOnlyList<ITileFeature> features, double zoom, double extent,
        TileId id, double3 tileOriginRender, IProjection projection, out int vertexCount, out Bounds bounds);
}
```

Concrete: `FillRenderLayer`, `LineRenderLayer`, `SymbolRenderLayer`, `BackgroundRenderLayer`; later
`RasterRenderLayer`, `FillExtrusionRenderLayer`. **Adding a kind = one class + one `RenderLayerFactory` arm** —
no edits to the layer set, the backends, the tile consume loop, or an orchestrator (they all operate on the axes,
not the concrete types).

## Locked decisions

- **One global draw-order index over ALL painted layers.** `RenderLayerSet.Build` walks `style.Layers` once and
  numbers every painted layer — fill, line, symbol, background (raster, fill-extrusion when they land) — each
  owning a queue BAND (`LayerDrawOrder.QueueFor(drawIndex, subSlot)`, G7/D7): fill/line/background use only
  `LayerSubSlot.Base`; a symbol layer's icon takes `Base` and its own text `Above`, so the badge always draws
  under the number it frames. `index == draw order == material index` holds; the
  list simply contains all painted layers now. Only genuinely unpainted kinds (unknown, unsupported) and
  unconfigured-material layers take no slot. `LayerDrawOrder`'s monotonic-queue + 5000-ceiling contract is
  unchanged in KIND (a too-large style throws rather than saturating); the 2-sub-slot band halves the
  layer-count runway (~2000 → ~1000 painted layers; realistic styles stay far below either).
- **Backends get the full-width material list aligned to the global index, with null at non-tile slots** (those
  slots never receive `AddTileLayer`). Two backend touch points: BRG's ctor `RegisterMaterial` loop must skip
  nulls; the Entities prototype seed must use the **first non-null** entry (slot 0 may be a background layer).
  The tile produce loop filters `layer is ITileMeshRenderLayer`; the payload carries its own `MaterialIndex`, so
  consume is indifferent to gaps, guarded by `(uint)materialIndex >= _layers.Count`.
- **Global symbol collision, per-layer symbol draw** (MapLibre semantics). Collision stays ONE cross-layer pass
  (`LabelCollisionJob` over all candidates — a road name and a city name keep competing in one greedy pass); each
  symbol layer draws its own survivors at its own queue via its per-slot mesh/material. `LabelPlacementSystem`
  partitions survivor quads by `LabelInstance.MaterialIndex` into per-slot meshes; slot `g` IS the g-th declared
  symbol layer. `SymbolRenderLayer`'s icon material gets `QueueFor(DrawIndex, Base)` and its text material
  `QueueFor(DrawIndex, Above)` (G7/D7 — the icon always draws under its own layer's text), so two symbol layers
  on one feature still draw in style order, band before band.
- **Per-layer materials live on the layer object, one owner.** `SymbolRenderLayer` owns its material clone (halo
  bind included); the label subsystem consumes the layer set's materials instead of cloning its own. Fill/line
  already work this way.
- **Layer-kind dispatch has exactly one registry.** `RenderLayerFactory` is the only type-switch;
  `MapView.BuildSourceSpecs` ("which sources to fetch") and the label subsystem ("which layers are mine") derive
  from the built `RenderLayerSet`, not from re-walking `style.Layers` with their own `is` checks.

**The numbering, precisely.** For a declared interleave `background, fill(land), line(road), symbol(road-label),
fill(building), symbol(place-label)`:

```
target:  background=3000  land=3001  road=3002  road-label=3003  building=3004  place-label=3005
                                                 └── building fill occludes road labels; place labels on top — as declared
```

(Before unification, symbols pinned to Overlay 4000 above everything with mutual order undefined, and background
was a hardcoded camera-clear hack — both fixed.)

## Symbols: presence via persistent renderers

Symbols draw as **persistent per-slot `MeshRenderer`s** (`LabelSlotPresenter`), not immediate-mode
`Graphics.RenderMesh`. `Tick` does everything up to and including the per-slot mesh write (project → collide →
fade/emit → `SymbolBillboardJob` → mesh upload) and swaps the presenter's mesh; Unity redraws it every camera
render automatically. This: (i) kills the Editor "blink" (a Game-View repaint without the player loop got no
re-submission under immediate mode) with **no `beginCameraRendering` orchestrator**; (ii) inherits deterministic
`renderQueue` ordering against BRG tiles (URP's `CommonTransparent` sort ranks `renderQueue` above the
camera-distance tiebreak); (iii) is headless-testable, so the ordering/presence teeth
are real snapshot tests (`SymbolLayerOrderSnapshotTests`) rather than manual Editor verifies. The shader's
`"Queue" = "Overlay"` tag + `ZTest Always` survive only as the demo/no-style fallback default.

An `Immediate` draw persistence + a `beginCameraRendering` orchestrator remain the model's vocabulary for a
future `CommandBuffer`/`ScriptableRenderPass` fallback (e.g. a symbol needing draw state a `MeshRenderer` can't
express) — no implementor today.

## Background: a real layer, not a camera hack

`BackgroundRenderLayer` parses `background-color` / `background-opacity` into a typed `Background.PaintProperties`
(Core, the Fill pattern) and owns **only** a material — a clone of the FILL base
(`MaterialFactory.CreateBackgroundMaterial`; reusing the fill shader's flat lit path keeps the ground look
consistent with fills). Colour binds as a uniform (`_BaseColor`/`_Opacity`) over white vertex colours (the
line-colour pattern — background has no features to bake per-vertex). Geometry is **per-covered-tile**:
`TileBackgroundLayerProcessor` synthesizes one full-tile-extent quad per covered tile (reusing
`StyledFillTileBuilder.WriteMeshData` — earcut winding + globe subdivision for free) and registers each with the
active `ITileRenderBackend`, exactly like fill/line. So the background is **projection-correct** (flat on
Mercator, curved on the globe) and a mid-stack background occludes layers below it and not above. The
`Bootstrapper` hardcoded sky survives only as the above-horizon clear / no-style default. (The ±85.05°–90° polar
caps still have no surface — a pre-existing globe/tile-cover limitation shared by fill/line, deferred to the
globe track.)

## Raster + fill-extrusion: reserved seats (unscheduled)

- **Raster** = `TileMesh` + `Persistent`: a quad per tile, textured from a raster source, registered via
  `AddTileLayer`. The model change is zero — the OPEN problem is per-tile texture binding under the per-layer-
  material invariant (per-layer materials are shared across tiles; a raster tile needs its own texture →
  texture-array / BRG per-instance texture id / per-tile material — decided in the raster stage).
- **Fill-extrusion** = `TileMesh` + `Persistent` + ZWrite on. One `ITileMeshRenderLayer` class + one factory arm;
  the depth-vs-transparent-band interaction is its stage's design problem.

## Non-goals / open questions

- The shipped fill/line build pipeline — kick/produce/consume, budget, cancellation, `.Run()`-on-worker Burst
  kernels, `MeshDataPayload` — is untouched. Symbol BUILD stays per-frame placement — never folded into the Burst
  tile-mesh pipeline. The three tile backends stay Persistent tile-mesh engines; they learn nothing about symbols
  or backgrounds (null slots aside).
- **`ZTest Always` on symbol text vs future depth-writing layers.** Fine today (all flat layers ZWrite off —
  painter order alone composites). When fill-extrusion lands (ZWrite on), "labels unoccluded by 3D" is MapLibre's
  point-label default, so `ZTest Always` likely survives — but line-following labels behind buildings deserve a
  look in that stage.
- **Raster per-tile texture vs per-layer material** — unresolved by design; decided in the raster stage.
