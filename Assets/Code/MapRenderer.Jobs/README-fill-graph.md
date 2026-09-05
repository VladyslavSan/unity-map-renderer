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

## The chain

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
        │  FillSizingJob       offset tables for the flat scratch columns;
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
        │  FillAggregateJob    prefix sums, then concat with index rebase
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
written by `FillAggregateJob`; on the curved arm the scatter job fills all six. This is what lets the write
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

## Files

| file | role |
|---|---|
| `FillMeshGraph.cs` | the graph builder — schedules every node, owns the dispose nodes, returns the output |
| `FillGraphOutput.cs` | the output columns + terminal handle, and their disposal |
| `FillGraphCounts.cs` | diagnostic counts and the error codes |
| `RingClipJob` / `RingSelectJob` | produce the ring set the rest of the chain walks |
| `RingAssemblyJob.cs` | signed-area polygon classification with hole containment |
| `FillSizingJob.cs` | offset tables; the capacity guard |
| `FillGatherJob.cs` | the hole sort and the flat-column copy |
| `EarcutJob.cs` / `EarcutBatchJob.cs` | the triangulator, and the batching wrapper (`IJobParallelForDefer`, one polygon per `Execute`) |
| `FillAggregateJob.cs` | concatenation and index rebase |
| `GlobeFillSubdivider.cs` | the curved arm's subdivision (`GlobeFillSubdivideDispatch`) |
| `GlobeFillScatterJob.cs` | scatters a subdivided run into the six output columns |
| `FillTriangulationBuffers.cs` | the flat working columns the sizing/gather/earcut stages share |
| `PolygonDescriptors.cs` | the polygon descriptor columns assembly produces |
| `FillMeshPipeline.cs` | the older synchronous entry point, kept as the parity oracle. **No longer on a production fill path**: since source tiles moved onto the graph, fill reaches `FillMeshGraph` instead, and the only live caller left is the fill-extrusion roof — which leaves too once the roof moves. It survives for the parity teeth, and is retired in the same stage that removes its last caller. |
