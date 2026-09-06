# Line meshing

Turns one style layer's LineString features, as decoded native columns, into the ribbon vertex and index
columns a mesh is written from — entirely in Burst jobs, off the main thread, with no managed allocation on
the path. The ribbon twin of `Fill/README.md`'s graph; the two should be read together.

`LineMeshGraph.Schedule` is the entry point. It returns a `LineGraphOutput` whose `Handle` is **not
completed**: the caller polls it across frames and completes it before reading, mirroring `FillGraphOutput`'s
own contract.

## Why it exists

The managed line builder used to gather, subdivide and tessellate a layer's centerlines inline, in a ring
loop that ran on whichever thread called it — the main thread, or a managed closure handed to a thread pool
where one existed. That loop is retired; `LineMeshGraph` is the sole mesher now, scheduling the identical
"gather → subdivide → project → ribbon" ordering as Burst jobs that reach worker threads on every target this
project ships, the fill graph's own reason (`Fill/README.md` § *Why it exists*). `docs/line-rendering-design.md`
is the design SSOT for the width/join/cap *model* this graph builds toward; this file explains the CPU-side
*shape* of getting there, not that model.

## Pipeline

Every arrow is a container hand-off carried by a `JobHandle` dependency, the same convention `Fill/README.md`
uses and for the same reason: a missing edge is a bug the Editor's job safety system throws on at schedule
time.

```
  LayerInput { Geometry, FeatureSelected, OriginRender, Projection,
               Join, Cap, MiterLimit, RoundLimit, RoundSegments, MaxOutputVertices }
        │
        │  RingGatherJob            this layer's own selection + LineString-kind + length(≥2) gate,
        ▼                           over the BORROWED shared tile geometry
     gathered ── flat tile-space centerline per surviving ring, in source order
        │
        │  TileToGeoJob → ProjectionDispatch     project the ORIGINAL centerline to per-point
        ▼                                        surface `up` — the subdivision metric only
   (original, projected)
        │
        │  SubdivideJob             curvature-subdivides in TILE SPACE; a flat projection's ∞
        ▼                           tolerance is 1 step/segment — the same code, not a branch
    subdivided ── flat tile-space centerline; ring count unchanged, point count grown
        │
        ├──────────────────────────────────────────────┐
        │                                              │
  PROJECT branch                                  SIZE branch
        │                                              │
        ▼                                              ▼
   TileToGeoJob                              RibbonSizingJob
   ProjectionDispatch                        (per-ring offset tables, sized off
   (subdivided centerline →                   THAT ring's own point count;
    render-space — the ribbon's                reads only RingSubOffsets, so
    real input)                                this runs CONCURRENTLY with the
        │                                       projection, not after it)
  (subdivided, projected)                            │
        │                                          sized
        │                                              │
        └──────────────────────┬───────────────────────┘
                                ▼
                         RibbonBatchJob       IJobParallelForDefer, one ring per Execute
                                ▼             call; runs RibbonJob's join/cap/topology,
                            ribboned          RAW and unswapped per ring
        │
        │  RibbonAggregateJob       walks rings IN RING ORDER: winding swap + running-offset
        ▼                           rebase + the overflow stop — order-dependent, so serial
  LineGraphOutput { Vertices, VertexFeatureIdx, Indices, Error, Handle }
```

**Two projections of the same centerline, not two lines.** The graph projects the centerline twice, and
conflating the two passes is the easiest mistake to make here: the first projects the *original* centerline
only far enough to get a per-point surface `up` for the subdivision metric (a `world` column comes along for
free and is never read); the second projects the *subdivided* centerline into the render-space positions the
ribbon actually builds from. Subdivision itself runs in between, in tile space, not after either projection.

**Why sizing → parallel → aggregate.** A ring's true vertex/index count is not analytically predictable — it
falls out of `RibbonJob`'s own join/cap decisions — exactly the earcut's reason for the same three-stage
shape in `Fill/README.md`. `RibbonBatchJob` writes each ring's output at its own offset, ring-local and
unswapped; `RibbonAggregateJob` is where those become the graph's real, globally-offset, winding-correct
output — relocating a formula, not changing it.

## Components

| Type | Role |
|---|---|
| `LineMeshGraph` | the graph builder — schedules every node, owns the dispose nodes, returns the output |
| `LayerInput` | the input descriptor — the line twin of `FillMeshPipeline.LayerInput` |
| `LineGraphOutput` | the output columns + terminal handle, and their disposal |
| `LineGraphCounts` | the error codes threaded through `LineGraphOutput.Error` — a separate type from `FillGraphCounts`, not a value added to it |
| `RingGatherJob` | selection + kind + length gate, gathering this layer's LineString rings off the borrowed shared geometry |
| `SubdivideJob` | curvature-subdivision in tile space, over the per-point surface `up` |
| `RibbonSizingJob` | per-ring offset tables; the capacity guard |
| `RibbonBatchJob` | the parallel, per-ring ribbon build (`IJobParallelForDefer`, deferred over the sizing job's per-ring vertex count) |
| `RibbonJob` | the 3D ribbon builder itself — one ring's join/cap geometry and index topology |
| `RibbonAggregateJob` | winding swap, offset rebase and the overflow stop, applied in ring order |
| `RibbonBuffers` | the flat working columns the sizing/ribbon/aggregate stages share |

## Ownership

The same rules as `Fill/README.md`'s own *Ownership* section, applied to this graph's buffers:

- **No working buffer leaves the graph.** Every scratch column from both projection passes, and the ribbon
  buffers, is freed by a dispose node scheduled inside `LineMeshGraph.ScheduleTyped`; the returned handle
  includes all of them.
- **The caller owns the inputs** (`LayerInput.Geometry`, `.FeatureSelected`) and must not dispose them
  before completing the handle — they are live `[ReadOnly]` job inputs until then.
- **`.AsArray()` is never taken at schedule time**, only `AsDeferredJobArray()` resolved inside `Execute` —
  the identical rule `FillMeshGraph`'s own doc states, carried over verbatim.
- **Errors are a flag, not a throw.** `RibbonSizingJob` and `RibbonAggregateJob` share one
  `NativeReference<int>`, never reset between them; a sizing failure and an aggregate failure are mutually
  exclusive by construction (a sizing failure leaves the per-ring count lists at length 0, which bounds
  aggregate's own loop to zero iterations), not merely by scheduling order.
