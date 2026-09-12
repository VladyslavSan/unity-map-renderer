# Projection / globe track — design SKETCH (Track B)

**Status: SKETCH, not scheduled.** Companion to the render-layer + tile-pipeline work
(`docs/meshing-design.md` §4, `docs/per-layer-tile-processing-design.md`). Captures the flat-earth /
Mercator couplings that keep the map from being correct under the **spherical projection**, and which of them
are Track B's own vs. which fall out of Epic A.

Related principles (already in the codebase): projection-agnostic camera, "shaders must not assume flat
ground — per-vertex frame is projection-supplied", "unify/reuse the one 3D projection, don't propagate the
flat-earth smell".

## What Epic A already fixes for free (NOT Track B work)

Epic A (tile-pipeline unification) makes every per-tile prepare project through the same `IProjection` as
fill/line. So **background and all tile geometry become curved on the sphere automatically** — no flat `y=0`
quad, and E3's Mercator-only background gate is deleted. That geometry-projection correctness is a *byproduct*
of Epic A; Track B does not own it.

**A2 landed this (2026-07-13), confirming the above.** `TileBackgroundLayerProcessor` projects the
background quad through the same `IProjection` fill/line use (curved on `SphericalProjection`, flat on
`WebMercatorProjection`); `MapView.SetStyle`'s Mercator-only `BackgroundRenderLayer.SetVisible` gate is
DELETED. See `docs/per-layer-tile-processing-design.md`.

### B3 — Full-sphere / polar-cap background surface (new, filed by A2)
A2 makes the *covered* band (the Mercator tile pyramid, ±85.051°) curve correctly, but coverage is still the
tile cover — a synthetic background quad lives in the Web-Mercator domain like every other tile, so the
**85.05°–90° polar caps get no surface** (camera-clear near a pole on the globe). This is **not
background-specific**: fill and line have the identical gap already, because the tile cover *is* the
Mercator pyramid at every zoom (z0 included). Full-sphere polar coverage needs geometry **outside** the tile
pyramid (a projected polar-cap surface / a dedicated globe-only background), which belongs here, not in a
per-layer-tile-processing stage. Not scheduled.

## What Track B owns (does NOT fall out of Epic A)

### B1 — Symbol far-side occlusion (the big one)
On a sphere, a label whose anchor is on the **far hemisphere** is *in front of the camera* (not behind it) but
occluded **by the globe itself**. Today's behind-camera cull (W<0) does not catch it, and the billboards use
`ZTest Always` → far-side labels **leak onto the front** of the globe. Need a **visible-hemisphere / horizon
test** on the anchor before placement.

- **Current state is better than "no globe support":** anchor world positions come from
  `projection.ProjectPoint(GeoCoordinate)` (`SymbolFeatureExtractor.cs:791`) — projection-agnostic by
  construction, since `IProjection` resolves the anchor for whichever projection is active. The per-frame
  projection culls behind-camera anchors (`SymbolPlacementSystem.cs`), and there is a B-3
  horizon/distance cull for the tilted-view pile-up. So placement is projection-aware; **the specific gap is
  far-side occlusion.**
- **Likely fix:** reuse the projection's existing horizon predicate — `IProjection.TryGetHorizonOccluder`
  (already used by E3's background Mercator gate) — as a pre-collision reject on each anchor, OR a per-anchor
  `dot(anchorNormal, viewDir)` visibility test. Pre-collision placement (with the B-3 cull) is the cheap spot:
  far-side labels then never consume collision budget.

### B2 — `WebMercator.GroundResolution` coupling — RESOLVED
The cross-tile **collected set** (symbol dedup identity across a parent+child tile during a zoom) no longer
quantizes by `WebMercator.GroundResolution(zoom)`. `CrossTileSymbolKey.CanonicalGridMeters` replaced it — a
fixed 4.0 m grid over the render-space (pre-RTC) anchor, with no `WebMercator`-specific term, so no
flat-Mercator quantum. See `labels-async-reconcile-design.md` §3.1.

## Relationship to Epic A / sequencing

- B1/B2 are **symbol-only** and touch the label pipeline, not the tile-mesh seam — so they are **largely
  independent of Epic A** and could ship first if globe *labels* are the priority.
- But the *whole* globe story only lands once Epic A also makes background/tiles curved. So the natural order
  is Epic A → Track B, or Track B in parallel if labels-on-globe is urgent.

## Open questions

- **Which horizon test** — reuse `IProjection.TryGetHorizonOccluder` (consistency with the background gate) vs.
  a per-anchor dot product? Lean: reuse the projection predicate.
- **Where** — far-side reject in the projection/placement pass (pre-collision, cheapest) vs. draw time. Lean:
  pre-collision, alongside the B-3 cull.
- **Horizon-crossing fade** — a label crossing the horizon under globe rotation should **fade, not pop**. Does
  it hook the existing A-4 fade state machine, or is that a separate polish item?
- **Line-following (curved) labels behind the sphere** — point labels are the first cut; curved text on the
  far side is a later concern (mirrors `meshing-design.md` §4's "line-following labels behind buildings" note
  for fill-extrusion).
