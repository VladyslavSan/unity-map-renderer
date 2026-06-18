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
- **label placement & collision** — the crown jewel; where domain experience differentiates the result
- the ECS rendering layer (this *is* the product) — meshes / materials / GPU-driven draw

**Vendor as clean permissive dependencies — don't reinvent** (one-line notice each):
- **polygon triangulation** → `LibTessDotNet` (MIT, robust w/ holes + self-intersection) or an earcut C# port
- **text shaping** (i18n) → `HarfBuzzSharp` (MIT) — an entire subfield; never hand-roll
- **SDF glyphs** → TextMeshPro SDF, or generate offline
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

```
                       ┌───────────────────────────── per-frame ────────────────────────────┐
 Camera/ViewState ─► TileSelectionSystem ─► requests
                                              │
                       ┌──────── async + Burst jobs (off main thread) ────────┐
                       ▼                                                       │
   BYO DataSource ─► FetchSystem ─► DecodeJob(MVT/MLT) ─► TessellationJob(s) ──┘
   (HTTP/PMTiles/                    (Burst, per tile)    (fill/line/extrusion,
    local/in-mem)                                          Burst, NativeArrays)
                                                                  │
                                                                  ▼
                                                       MeshBuildSystem  ── main-thread sync point:
                                                       NativeArray<Vertex/Index> → Mesh / GraphicsBuffer
                                                                  │
                       ┌──────────────────────────────────────────┴───────────┐
                       ▼                                                        ▼
            Fills / lines / extrusions                          LabelPlacementSystem  (per frame,
            → ECS render entities                                screen-space collision, Burst job
            (Entities Graphics / BatchRendererGroup,             over a spatial grid)
             per-layer materials, draw order)                           │
                                                                        ▼
                                                              Symbol/text billboards
                                                              (SDF, camera-facing)
```

### Two geometry classes, two paths
| Class | Built | Lifetime | Rendered as |
|---|---|---|---|
| fills / lines / fill-extrusions | once per tile (Burst job) | rebuilt on tile add/remove | static meshes / GPU buffers in the scene |
| **symbols / text / icons** | **placed every frame** (screen-space collision) | transient | camera-facing SDF billboards |

Labels stay upright and don't scale with zoom → screen-space placement + collision **every frame**.
This is a separate path from fills/lines and is where these projects historically die — sequenced last.

### Performance levers (the differentiator)
- **Burst + Jobs** for decode and tessellation → parallelize across all in-flight tiles; no GC in the hot path.
- **GPU-driven rendering** for lines/fills: keep compact per-feature buffers and expand/style in shaders
  via **BatchRendererGroup** / `Graphics.RenderPrimitives` / `GraphicsBuffer`, instead of fat CPU meshes
  and a GameObject per feature (the old Mapbox-Unity-SDK mistake). This is where your URP/HDRP tricks pay off.
- **`Unity.Mathematics`** for SIMD-friendly vectorized math in jobs.

### Coordinate precision / floating origin (non-negotiable)
Unity transforms are 32-bit float; world-scale Mercator coordinates jitter badly. The core computes in
**Web Mercator doubles**; each tile carries a `double` origin and the render layer rebases to a 32-bit
local origin near the camera (recenter the map root as the camera moves). Designed in from Step 3.

### BYO data-source abstraction
A small C# interface — `fetch(tileCoord) -> bytes` + declared encoding (MVT now, MLT later, raster
later). HTTP, local files, PMTiles, a proprietary backend, or in-memory generated tiles all look
identical to the pipeline. This is a first-class product surface.

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
- **Vendored deps** are all permissive: LibTessDotNet (MIT), HarfBuzzSharp (MIT), protobuf-net (MIT) /
  Google.Protobuf (BSD), Clipper2 (Boost). Commercial + closed-source fine.
- **Compliance = one file.** A bundled `THIRD-PARTY-NOTICES.txt` (and an in-app credits screen if you
  ship binaries) reproducing each dep's copyright + license satisfies them all. No copyleft, no
  open-sourcing, no in-UI attribution required.
- **Avoid copyleft in the core** (GPL/AGPL viral; LGPL link-level; MPL file-level). GEOS = LGPL → use
  Clipper2 instead. (Not legal advice; a short IP-lawyer pass before commercial launch is cheap insurance.)

## 5. Risks
| Risk | Mitigation |
|---|---|
| DOTS / Entities API churn & learning curve; Entities Graphics constraints | Keep the hot path as **Burst jobs + NativeArrays** (stable surface); use ECS where it clearly pays; don't force every subsystem into it |
| Hard kernels (placement, line tessellation) subtly wrong | Vendor triangulation/shaping; clean-room placement carefully; **golden-image diffs vs MapLibre output** |
| Coordinate jitter at world scale | Floating-origin rebasing from Step 3; doubles in core, floats at render |
| GC / frame spikes | NativeArray everywhere in hot path; no managed allocs per frame; GraphicsBuffer for large geometry |
| Text / i18n complexity | HarfBuzzSharp; sequence last (Step 4) |

## Open question for the next session
Ready for the **Step 0 design note** — ECS layout (entities/components/systems), the data-source
interface, MVT decode approach (hand-rolled vs lib), triangulation pick, and the URP/HDRP rendering
approach — or do you want to start coding the spike directly?
