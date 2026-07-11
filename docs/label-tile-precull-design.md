# Label tile-coverage pre-cull — design sketch

**Status:** IMPLEMENTED (branch `perf/label-tile-precull`). The cull + telemetry landed; the metric is
`LabelTileCoverage` (Core, tested), corners are stored render-space on `SymbolLabelBatch`, and the per-frame
pass runs in `LabelPlacementSystem.ComputeTileCoverageCull` before gather. **One deviation from the sketch
below:** corners are projected via `TileId.ToLonLat → IProjection.Project` (globe-correct), NOT
`MercatorBounds()` (flat-earth). Still open (deliberately deferred): the green/red debug overlay, tilt-scaling
the threshold, and explicit hysteresis (the A-4 fade softens boundary flicker for v1). Default
`MinTileScreenCoverage = 0.05` — a maintainer eyeball-tunable.

## Idea

Add one coarse step *before* the per-label pipeline: skip a tile's labels entirely when the tile
covers less than ~N% of the screen. Small-on-screen tiles are the horizon pile-up under tilt — most
of their labels get collision-culled anyway, so processing them (gather → project → stage → collide)
is wasted work. Dropping them whole stabilizes per-frame label cost with barely any lost information.

Complements the existing **B-3 per-label distance cull** (`LabelViewDistance`, a horizon *radius*):
this is a per-**tile** *screen-area* metric, which catches tilt-foreshortened slivers the radius keeps.

## Metric (cheap)

Per tile: project its 4 corners to screen, shoelace the quad area, divide by viewport area.

```
coverage = |screenQuadArea(4 projected corners)| / (viewportPx.x * viewportPx.y)
cull the tile  ⟺  coverage < MinTileScreenCoverage      // tunable; start ~0.05–0.10, eyeball it
```

Corner sources are on hand: `TileKey` packs `(z,x,y)` (`SymbolFeatureExtractor.PackTileKey`, `z<<44 | y<<22 | x`);
`TileId.MercatorBounds()` → geo corners; project geo→render via the same `IProjection` that built
`LabelInstance.AnchorRender`; render→screen via `LabelScreenProjection.TryProjectPoint`. If any corner
is behind the camera (`TryProjectPoint` false), treat the tile as visible (don't cull) — it straddles
the near plane.

## Where it lives (the load-bearing constraint)

Coverage is **camera-dependent → per-frame**. It must NOT go into the collect / batch build — that
would make the version-cached `SymbolLabelBatch` rebuild every frame the camera moves and throw away
C-2's win. So:

- **Batch build (once per version):** dedup tiles, store each unique tile's 4 **render-space** corners
  (`projection.Project(geoCorner)`, fixed geometry) + a per-record tile index. `SymbolLabelBatch` grows
  a `TileCorners` array + `RecordTile[]`.
- **Per frame, in `LabelPlacementSystem.Tick`** (it already owns `viewProj` / `sceneOrigin` / viewport):
  a tiny pre-pass projects each unique tile's 4 corners → coverage → a reused `culled` flag per tile
  (dozens of tiles, negligible).
- **Gather** culls record `r` if `culled[batch.RecordTile[r]]`, alongside the existing B-3 test. No Burst
  job change — gather is managed; culled records just aren't gathered/projected/staged.

## Extras

- **Telemetry:** `LastTileCoverageCulledCount` (mirror `LastDistanceCulledCount`) so the effect is visible.
- **Debug viz (separate, optional):** the green/red survived-vs-culled box overlay the maintainer wants —
  would make `MinTileScreenCoverage` easy to tune by eye. Not required for the cull itself.

## Open questions

- Threshold value + whether it should scale with tilt.
- Hysteresis so a tile flickering around the threshold during a zoom/tilt doesn't pop its labels
  in/out (the A-4 fade may already soften this).
