# Step 0 — Spike: one MVT tile's fills on screen

**Exit criterion:** the `countries` polygons from `Assets/Fixtures/sample-tile.bytes` render as a Unity
`Mesh`. Planar Web Mercator only; fills only; no ECS World, no streaming, no styling.

## Layers (split across two assemblies)
- **`MapRenderer.Core`** — managed library: MVT decode, Web-Mercator / tile math, ring assembly,
  clean-room earcut, fill tessellation. Uses `Unity.Mathematics` (`double2`). Note: in Unity 6
  `double2` is type-forwarded to an engine module, so Core references the engine — a genuinely
  Unity-free Core would need its own vector struct (deferred; portability is a non-goal).
- **`MapRenderer.Jobs`** — Burst + Collections. The per-vertex coordinate-transform job (tile→Mercator
  →origin-relative `float3`). *(Batch 2)*
- **`MapRenderer.Unity`** — MonoBehaviour bootstrap + mesh build. *(Batch 2)*
- **`MapRenderer.Tests.EditMode`** — headless verification.

## Why this split
LibTess→earcut: clean-room earcut is small and can be made Burst-compatible, so the fill pipeline can
eventually be fully jobified. For Step 0, triangulation runs on the main thread; only the coordinate
transform is a Burst job (the genuine "Burst over NativeArrays" content).

## Pipeline
```
sample-tile.bytes ─► MvtDecoder ─► MvtGeometry (command stream → rings, tile space)
                                      │
                                      ▼
                         PolygonAssembler (rings → outer + holes by signed area)   [Batch 2]
                                      │
                                      ▼
                         Earcut (clean-room ear-clipping)  →  vertices + indices    [Batch 2]
                                      │
                          ProjectTileVerticesJob (Burst: tile→Mercator→origin-relative float3)  [Batch 2]
                                      │
                                      ▼
                              MeshBuilder → UnityEngine.Mesh  (MonoBehaviour)        [Batch 2]
```

## Delivery
- **Batch 1:** decode + coordinates + EditMode tests (this commit). Validatable headlessly.
- **Batch 2:** earcut (+ unit tests), Burst projection job, Unity render layer.

## Verification
- **Headless (no GUI):** Window → General → Test Runner → EditMode → Run All. Tests assert: `countries`
  decodes to 239 polygon features; geometry decodes without error; projected Mercator vertices land
  within the z0 tile's world bounds (catches gross scale/parse bugs). Earcut tests (Batch 2) assert
  known triangle counts + area conservation.
- **On screen (Batch 2):** a GameObject with the renderer shows the country fills; an unlit `Cull Off`
  material avoids winding/backface surprises for the spike.

## Step-0 gotchas (see also docs/coordinates-and-projections.md §7)
- Don't pre-flip Y — the tile→lon/lat formula already encodes the top-left origin.
- Read `extent` per layer (don't hardcode 4096).
- `mesh.indexFormat = UInt32` (dense tiles exceed 65535 verts).
- Single-tile precision: subtract the tile-origin Mercator (double), then cast to float.
- Spike renders double-sided (`Cull Off`); fix winding as a deliberate follow-up.
