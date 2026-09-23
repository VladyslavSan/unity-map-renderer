# Projection / globe track — design

The flat-earth / Mercator couplings that would keep the map from being correct under the **spherical
projection**, and how each is resolved. Companion to `docs/meshing-design.md` ("Render-layer model") and
`docs/per-layer-tile-processing-design.md`; the math is `docs/coordinates-and-projections.md`.

Governing principles: the camera is projection-agnostic; shaders do not assume flat ground — the per-vertex
frame is projection-supplied; reuse the one 3D projection rather than propagating a flat-earth special case.

## Tile geometry curves because every tile layer projects

Every per-tile layer — fill, line, fill-extrusion and the source-less background — projects through the active
`IProjection`. Tile geometry and the background are therefore curved on `SphericalProjection` and flat on
`WebMercatorProjection`, with no Mercator-only gate anywhere. This falls out of the per-layer tile model
(`docs/per-layer-tile-processing-design.md`); it is not globe-specific code.

## Open: the polar caps have no surface

Coverage is the tile cover, and the tile cover *is* the Web-Mercator pyramid (±85.051°) at every zoom, z0
included. A background quad lives in that domain like every other tile, so the **85.05°–90° polar caps get no
surface** on the globe — the camera clear shows through near a pole. The gap is not background-specific: fill
and line have it too. Full-sphere coverage needs geometry **outside** the tile pyramid — a projected polar-cap
surface or a dedicated globe-only background. Not built.

## Symbols: far-side occlusion is a gather-time horizon cull

On a sphere, a symbol whose anchor is on the **far hemisphere** is in front of the camera (the behind-camera
cull does not catch it) but hidden **by the globe itself**, and symbol billboards draw with `ZTest Always`, so
without a cull they leak onto the front of the globe.

- **The test reuses the projection's own horizon predicate.** `HorizonCull.IsHiddenBeyondHorizon` takes the
  occluding sphere from `IProjection.TryGetHorizonOccluder` and hides an anchor when
  `dot(P − centre, cam − centre) < radius²` — a polar-plane test, exact for on-surface points, the `rad = 0`
  case of `FrustumTileSelector`'s tile occlusion algebra. A planar projection has no occluder, and the cull is
  then a no-op.
- **It runs at gather time, before collision.** `CullJob` evaluates it per record in the gather's verdict
  chain, so a far-side symbol never consumes collision budget.
- **It fades, never pops.** The horizon is a fade-out trigger (`GatherTrigger.Horizon`), a peer of the tile,
  distance and departing culls — never a hard drop inside the projection job — so a symbol crossing the
  horizon under globe rotation fades out.
- **Anchors are projection-agnostic.** Anchor world positions come from `IProjection`, so placement already
  resolves the anchor for whichever projection is active.

**Open: line-following labels behind the sphere.** The cull tests one representative anchor per symbol
(`SymbolBatch.RepAnchor`), so a long line-following label that straddles the horizon is kept or faded as a
unit. Per-glyph horizon handling is open; it mirrors the "line-following labels behind buildings" question in
`docs/meshing-design.md` ("Render-layer model → Non-goals / open questions").

## Symbols: no flat-Mercator quantum in cross-tile identity

The cross-tile collected set (symbol dedup identity across tiles) quantizes the render-space (pre-RTC) anchor
to a fixed 4 m grid (`CrossTileSymbolKey.CanonicalGridMeters`), with no `WebMercator.GroundResolution` term, so
it carries no flat-Mercator quantum (`docs/labels-async-reconcile-design.md` § "Invalidation events — what
dirties the state (must be EXHAUSTIVE)").
