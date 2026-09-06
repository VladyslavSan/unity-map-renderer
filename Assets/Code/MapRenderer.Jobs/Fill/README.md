# Fill meshing

Turns one style layer's polygon features, as decoded native columns, into the vertex and index columns a
mesh is written from — entirely in Burst jobs, off the main thread, with no managed allocation on the path.

`FillMeshGraph.Schedule` is the entry point. It returns a `FillGraphOutput` whose `Handle` is **not
completed**: the caller polls it across frames and completes it before reading. Nothing here ever completes
its own handle.

## Why it exists

The same work used to run as a managed closure handed to a thread pool, or inline on the calling thread
where no pool exists — which on the web is the render thread. Burst-compiled jobs reach worker threads on
every target this project ships, so the geometry work belongs in a job graph rather than behind a closure
seam. `docs/job-scheduling-design.md` is the design SSOT and explains the *why*; this file explains the
*shape*.

## Pipeline

Every arrow is a container hand-off carried by a `JobHandle` dependency. A missing edge is a bug the Editor's
job safety system throws on at schedule time — which is why the parity suites schedule the real graph.

```
  LayerInput { Geometry, RingVisitOrder, OriginRender, Projection, Clip }
        │
        │  RingClipJob  (clip enabled — production's default)
        │  RingSelectJob (clip disabled)
        ▼
     derived ── rings, in visit order, clipped to the tile's buffer window
        │
        │  RingAssemblyJob     classify rings into polygons by signed area;
        ▼                      opposite-sign rings are holes only if CONTAINED
    assembled ── polygon descriptors (outer ring + hole list)
        │
        │  SizingJob           offset tables for the flat scratch columns;
        ▼                      sets the error flag rather than throwing
      sized
        │
        │  FillGatherJob       hole sort + outer/hole vertex copy into flat columns.
        ▼                      The comparer's ring-index tie-break makes it a TOTAL
    gathered                   order, so any correct sort gives one deterministic result.
        │
        │  EarcutBatchJob      IJobParallelForDefer, one polygon per Execute call,
        ▼                      slicing the flat columns (stage 6)
  triangulated
        │
        │  AggregateJob        prefix sums, then concat with index rebase
        ▼
   aggregated ─────────────┬─────────────────────────────┐
                           │                             │
                   FLAT arm │                             │ CURVED arm
                           ▼                             ▼
                    TileToGeoJob                GlobeFillSubdivideDispatch
                    ProjectionDispatch          (splits triangles until the
                    (tile → geodetic →           surface-angle budget is met;
                     world positions)            projects internally)
                           │                             │
                           │                     GlobeFillScatterJob
                           │                     (scatters the subdivided
                           │                      run into the six columns)
                           └──────────────┬──────────────┘
                                          ▼
        FillGraphOutput { WorldPositions, VertexUp, VertexEast,
                          TileVertices, VertexFeatureIdx, TriangleIndices,
                          Counts, Error, Handle }
```

**One column set, both arms.** The output does not change shape by projection — the consumer reads the same
six columns either way and has no per-arm branch. On the flat arm `VertexEast` is the constant `(1,0,0)`,
written by `AggregateJob`; on the curved arm the scatter job fills all six. This is what lets the write
step be a single job.

**Which arm** is decided by the projection's refine angle: a projection with a finite `MaxRefineAngleRad`
curves, so it subdivides. The flat arm's geodetic and projection nodes are genuinely absent on the curved
arm — the subdivide job projects internally and never reads world positions.

## Ownership

- **No working buffer leaves the graph.** Every intermediate buffer is freed by a dispose node scheduled inside
  the builder, and the returned handle includes those nodes. Completing the terminal handle guarantees all
  scratch is freed; the caller never enumerates it.
- **The caller owns the inputs** and must not dispose them before completing the handle — they are live
  `[ReadOnly]` job inputs until then.
- **`.AsArray()` is never taken at schedule time**, only inside `Execute`. A list the graph resizes has no
  meaningful length until the job that fills it has run; a schedule-time view captures a stale one.
- **Errors are a flag, not a throw.** Burst cannot throw, so a capacity overrun sets `Error` and the write
  step skips the layer. The flag must be read before anything is allocated.

## Components

| Type | Folder | Role |
|---|---|---|
| `FillMeshGraph` | `Fill/` | the graph builder — schedules every node, owns the dispose nodes, returns the output |
| `FillGraphOutput` | `Fill/` | the output columns + terminal handle, and their disposal |
| `FillGraphCounts` | `Fill/` | diagnostic counts and the error codes |
| `RingClipJob` / `RingSelectJob` | `Geometry/` | produce the ring set the rest of the chain walks (borrowed, not owned by Fill) |
| `RingAssemblyJob` | `Fill/` | signed-area polygon classification with hole containment |
| `SizingJob` | `Fill/` | offset tables; the capacity guard |
| `FillGatherJob` | `Fill/` | the hole sort and the flat-column copy |
| `EarcutJob` / `EarcutBatchJob` | `Fill/` | the triangulator, and the batching wrapper (`IJobParallelForDefer`, one polygon per `Execute`) |
| `AggregateJob` | `Fill/` | concatenation and index rebase |
| `GlobeFillSubdivider` | `Fill/` | the curved arm's subdivision (`GlobeFillSubdivideDispatch`) |
| `GlobeFillScatterJob` | `Fill/` | scatters a subdivided run into the six output columns |
| `TriangulationBuffers` | `Fill/` | the flat working columns the sizing/gather/earcut stages share |
| `PolygonDescriptors` | `Fill/` | the polygon descriptor columns assembly produces |
| `FillMeshPipeline` | `Fill/` | **`Schedule`, the old synchronous entry point, is fully retired** — deleted, not just uncalled, and fenced against a caller reappearing (`FillMeshPipelineRetirementFenceTests`). The fill-extrusion roof already reached `FillMeshGraph.Schedule` (via `FillExtrusionMeshGraph`), same as every other caller. What survives here is what `FillMeshGraph` and its callers still share: `LayerInput` (the input descriptor), `HoleRingComparer` (the hole-sort order `FillGatherJob` uses), and the decode-sizing pair `PrecountRingsAndVertices`/`EnsureCapacity` (called from `MvtGeometryMaterializer`). |
