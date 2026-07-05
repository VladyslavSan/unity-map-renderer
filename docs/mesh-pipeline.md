# Tile mesh pipeline — how MVT bytes become a mesh

The path from decoded vector-tile geometry to a GPU `Mesh`, per geometry kind. This is a **reference** for
the stable target that S89 (render-layer unification) and S100/S102 (the 3D extrusion frame + the mesh-build
rename) settled — read it before touching `TileMeshPipeline`, `StyledFillTileBuilder`,
`StyledLineTileBuilder`, `LineRibbonJob`, or the tile-build loop in `TileManager`.

## The word "tessellation" is retired

"Tessellation" used to name **four unrelated things**, which is exactly why this path was hard to reason
about. S102 retired the word; the only survivor is `LineTessellator`, where "tessellate" now means strictly
**triangulate**.

| Former loose meaning | Now called | Lives in |
|----------------------|-----------|----------|
| the whole decode→mesh chain | **the mesh pipeline** | `TileMeshPipeline`, the `StyledFill/LineTileBuilder`s |
| inserting curvature points (globe) | **Subdivide** | `SubdivideCenterline`, `GlobeFillSubdivideJob` |
| earcut / ribbon-offset | **Triangulate** (the only surviving "tessellate") | `Earcut`, `LineTessellator` (oracle), `LineRibbonJob` |
| "build one whole tile's mesh" (async scheduling) | **mesh build** (worker) + **consume** (main thread) | `TileManager` (`KickMeshBuild`, `MeshBuildTask`, `MaxMeshBuildsPerTick` / `ConsumeMeshBuild`, `MaxConsumesPerTick`) |

That last row is the tile-build loop — two verbs, no "phases":
- **build** the mesh — kick a background task that runs the mesh pipeline off-thread, producing `Mesh.MeshData`.
- **consume** it — upload the built data to a GPU `Mesh` + place the render object, on the main thread.

The verbs used to be backwards: the step that *builds* the mesh was called "tessellation" and the step that
merely *consumes* it was called "build" (`MaxBuildsPerTick`). Now `MaxMeshBuildsPerTick` throttles builds and
`MaxConsumesPerTick` throttles consumes.

## The named responsibilities (inside one mesh build)

- **Decode** — MVT command bytes → integer tile-space rings. (`MvtDecodeJob`.)
- **Assemble** — classify rings into polygons + holes (outer/hole/skip by signed area). (`RingAssemblyJob`.)
- **Subdivide** — insert extra points so a straight edge in tile space follows a *curved* projected surface.
  Driven entirely by `IProjection.MaxRefineAngleRad` (∞ for Mercator ⇒ no split; a small angle for the
  globe). No projection constants leak in — the flat case is the degenerate value of one formula, not a
  branch.
- **Triangulate** — rings/centerline → triangle vertices + indices. Fills use ear-clipping (`Earcut` /
  `EarcutJob`); lines extrude a ribbon (`LineRibbonJob`, with `LineTessellator` as its planar differential
  oracle).
- **Project** — tile-space → geodetic surface (`TileToGeoJob`, projection-independent) → render-space
  `double3` + per-vertex `up`, through the chosen `IProjection` (`ProjectPointsJob<TProj>`).
- **Write** — stream the vertices/indices into a caller-allocated `Mesh.MeshData` on the worker; the main
  thread only **allocates** (at kick) and **applies** (at consume/upload).

## There is no single linear order — fills and lines compose these differently

This is the load-bearing point. **Triangulate-vs-Project is _opposite_ between the two kinds, and Subdivide
sits in a different slot for each.** Baking one universal sequence into the names would be a worse model than
the overload it replaced.

| Kind | Order | Where |
|------|-------|-------|
| **Fill** | Decode → Assemble → **Triangulate** (earcut, flat tile space) → **Project** → *(globe only)* **Subdivide** | `TileMeshPipeline.Schedule` does Decode→Assemble→Triangulate→Project; `StyledFillTileBuilder` adds the globe Subdivide (`GlobeFillSubdivideDispatch`, gated by `!double.IsInfinity(proj.MaxRefineAngleRad)`) then writes the mesh. |
| **Line** | Decode → **Subdivide** (centerline, tile space) → **Project** → **Triangulate** (ribbon) | `StyledLineTileBuilder.WriteInto`: `SubdivideCenterline` → per-point `TileToGeoJob` + `IProjection.ProjectPoint` → `LineRibbonJob` → writes the mesh. |

### Why fills Triangulate *before* Project

Ear-clipping is a **planar 2D algorithm**, and triangle **connectivity is projection-invariant** — which
vertices form a triangle doesn't change when you bend the sheet onto a globe. So earcut runs once in exact
integer tile space (cheapest, most robust), and Project moves the resulting vertices afterward. On the globe
a post-Project **Subdivide** then refines the now-curved triangles.

### Why lines Triangulate *after* Project

The ribbon extrusion needs the **projected 3D centerline plus its `up`** to build the cross-section:
`across = normalize(cross(along, up))`, tying the ribbon width to the same `up` the centerline was projected
with (**winding by construction** — correct front-faces for every projection, no flip; see
[`coordinates-and-projections.md` §7.1](coordinates-and-projections.md) and §8). So the centerline is
subdivided and projected first, then `LineRibbonJob` triangulates in render space.

## Threading & lifetime (both kinds)

The Burst jobs run via `.Run()` **on the mesh-build worker thread**, not the main thread — only
`Mesh.MeshData` **Allocate** (at kick) and **Apply** (at consume) are main-thread. Mesh *data*
(`NativeArray` / `TileMeshBuffers` / `MeshData`) is a value-type struct disposed deterministically at the
apply boundary; the `Mesh` is the single-owner class. Full contract:
[`async-architecture.md`](async-architecture.md) §"Disposal & cancellation contract" and the mesh-ownership
rule in [`conventions-short.md`](conventions-short.md).

## Key types

| Type | Assembly | Role |
|------|----------|------|
| `TileMeshPipeline` | `MapRenderer.Jobs` | Fill: coordinates Decode→Assemble→Triangulate→Project; owns `LayerInput` + `ProjectTileCornerOrigin`. Produces `TileMeshBuffers`. |
| `TileToGeoJob` | `MapRenderer.Jobs` | Project stage part 1: tile-space → geodetic surface (projection-independent). Takes a `TileId`. |
| `LineRibbonJob` | `MapRenderer.Jobs` | Line Triangulate: projection-agnostic 3D ribbon from a `(point, up)` array. |
| `LineTessellator` | `MapRenderer.Core` | The planar differential **oracle** for `LineRibbonJob` (`LineRibbonJobTests`). |
| `StyledFillTileBuilder` | `MapRenderer.Unity` | Fill orchestration: color eval → `TileMeshPipeline` → globe Subdivide → write mesh. |
| `StyledLineTileBuilder` | `MapRenderer.Unity` | Line orchestration: Subdivide → Project → `LineRibbonJob` → write mesh. |
| `MeshDataPayload` / `IRenderLayerPayload` | `MapRenderer.Unity` | The per-`(tile, layer)` mesh handle the consume loop uploads + disposes. |
| `TileManager` | `MapRenderer.Unity` | The tile-build loop: `KickMeshBuild` / `MeshBuildTask` (build) and `ConsumeMeshBuild` (consume), throttled by `MaxMeshBuildsPerTick` / `MaxConsumesPerTick`. |
