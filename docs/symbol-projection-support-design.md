# Symbol / label support for arbitrary IProjection (globe-ready labels) — design / SSOT

**Status: design stub, not started.** A large work item: make the symbol/label subsystem render correctly
under **any** `IProjection` (notably `SphericalProjection` — the globe), not just Web Mercator. The mesh
(fill/line) path is already projection-generic via `ProjectPointsJob<TProj>`; symbols are the remaining
Mercator-coupled subsystem.

## The goal

A label placed on the globe should sit on the curved surface at its true geographic anchor, be culled when it
falls behind the horizon, and (for line labels) follow the projected curve — the same correctness the fill/line
mesh path already has under `SphericalProjection`. Today the symbol path assumes a flat Web-Mercator world in
several places.

## Current state — foundation plumbed, real gaps remain

**Already projection-generic (the foundation):** anchor projection routes through `IProjection.Project`, not a
hardcoded Mercator formula —
- `StyledSymbolTileBuilder` takes an `IProjection projection` and threads it through.
- `SymbolLabelBatchBuilder.ProjectCorner` → `projection.Project(new GeoCoordinate { … })`.
- `SyntheticLabelSource` → `Map.Camera.Projection.Project(anchorGeo)`.
- `SymbolFeatureExtractor` anchors via `ToLonLat → projection.Project` (no `MercatorBounds`/flat-earth math).

So anchors already land on the *projected surface*, whatever the projection.

**Still Mercator-coupled (the gaps):**
1. **Hardcoded `WebMercator.GroundResolution(zoom)`** for cross-tile symbol quantization / dedup
   (`SymbolLabelSubsystem.cs:467,484`) and in label placement (`LabelPlacementSystem.cs:477`). Ground
   resolution varies with latitude on the sphere — a single Mercator value is an approximation that will
   misquantize/misdedupe (or mis-scale placement) away from the equator on the globe.
2. **Horizon / back-face culling.** On the globe a label can be on the far side of the planet. The mesh path
   handles this (front-face/normal culling); the symbol screen-projection (`SymbolProjectionJob` → `OutValid`)
   culls behind-camera points but the *behind-the-horizon* case needs validating/handling for labels.
3. **Curved-surface placement & orientation.** Line-label anchor spacing and per-glyph orientation are computed
   in a tile-local planar frame (correct for placement math — see the geometry-IR two-waist model), but the
   billboard/orientation on a *curved* surface (up vector, tangent along a great-circle-ish path) needs
   validating under `SphericalProjection`. Curved-line text along a globe surface is the hardest case.
4. **Collision in screen space.** Collision runs in screen space (projection-agnostic in principle), but must
   be re-validated once anchors/orientation are globe-correct.

## The work (sketch — each likely its own stage)

- **Audit + remove the `WebMercator.GroundResolution` hardcodes**: replace with a projection-supplied
  ground-resolution / quantization scale (from `IProjection`, latitude-aware), or move quantization to a
  projection-independent basis.
- **Horizon culling for labels**: extend the symbol projection/validity path to cull anchors behind the
  globe's limb.
- **Validate placement/orientation under `SphericalProjection`**: eyeball + snapshot the label path on the
  globe; fix billboarding/up-vector/tangent as needed.
- **Line-label curved text on the globe**: the hardest piece; may need per-segment re-projection of the
  centerline.

## Grounding (touch points)

`MapRenderer.Jobs/SymbolProjectionJob` (world→screen, `OutValid` culling); `MapRenderer.Unity/Text/`:
`StyledSymbolTileBuilder`, `SymbolLabelSubsystem` (`WebMercator.GroundResolution` at `:467,484`),
`Placement/SymbolLabelBatchBuilder` (`ProjectCorner` → `IProjection.Project`), `Placement/LabelPlacementSystem`
(`WebMercator.GroundResolution` at `:477`), `Placement/SyntheticLabelSource`; `SymbolFeatureExtractor` (anchor
extraction). `MapRenderer.Core/Geo/`: `IProjection`, `WebMercatorProjection`, `SphericalProjection`, `WebMercator`.

## Relationship to other work

- Depends on / complements the **projection-agnostic** direction (`docs/coordinates-and-projections.md`, the
  globe track) — the mesh path is already there via `ProjectPointsJob<TProj>`; this brings symbols to parity.
- Independent of the **geometry-IR epic** (that's about the geometry *representation*; this is about the
  *projection* of already-extracted anchors) — though both share the principle that tile-local planar frames
  are for placement math and projection happens at a well-defined seam.
