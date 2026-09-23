# line-translate — design

`line-translate` shifts a whole line layer by a screen-pixel offset. The line takes the structure of
`fill-translate` (`Fill_VertexModify.hlsl`'s `MapVertexModify`); the translate block lives in
`Line_VertexExtrude.hlsl`. This doc states the contract that block holds and why the "map"-anchor frame is
approximate, not exact.

---

## The contract

`_LineTranslate.xy` is in screen pixels. Each property below is required. A top-down, Mercator, pixel-width
fixture hides the first three, an unrotated camera hides the fifth, and only a signed assertion sees the
fourth:

| # | Property | Why it is not free |
|---|---|---|
| 1 | The offset converts pixels to metres **in both width modes** | `pxToWorld` is a unit conversion that reads `1.0` for a world-unit `line-width`. A translate that reuses it moves a world-width layer by raw METRES, off by orders of magnitude |
| 2 | Each axis is **measured along itself** — two `MapPixelsToWorld` calls, one per translate axis | the line's `across` direction is unrelated to the offset direction; under tilt the two axes foreshorten differently |
| 3 | The offset lies in the **vertex's own tangent plane**, never in hardcoded world XZ | a flat-ground assumption is wrong under a globe projection |
| 4 | `+x` is **east/right**, `+y` is **south/down** | the spec says *"negatives indicate left and up"*. Fill uses the same sign |
| 5 | `_LineTranslateAnchor` is honoured: `0` = "map", `1` = "viewport" | an ignored anchor makes `"viewport"` behave as `"map"` |

The block **early-outs** when `all(abs(_LineTranslate.xy) < 1e-6)`. `[0,0]` is the spec default, so nearly
every layer takes the early-out and skips two projection round-trips per vertex.

The "viewport" anchor takes its axes from the camera: `UNITY_MATRIX_I_V`'s first two basis columns, with
screen-down as `-up`. It is exact under every projection at every zoom. Everything below concerns the "map"
anchor only.

Guards: `LinePaintSnapshotTests.LineTranslate_NonZero_ShiftsRibbonSouthByExpectedPixels` (sign),
`LineTranslate_IsScreenPixels_WhetherWidthIsPixelsOrMetres` (units) and
`LineTranslateAnchor_MapAndViewport_MoveTheRibbonOppositeWays` (anchor). `SnapshotRenderer.Pixels` is
bottom-left origin, so check the origin convention before concluding a direction is wrong.

## Why the line's own axes cannot be used

Expressing the offset in the line's own `across`/`along` axes means *"shift each road perpendicular to
itself"*. That is **`line-offset`**, a different spec property, already implemented. The spec has both
because one is road-relative and the other map-relative. `line-translate` is defined against north; the
line's basis is rotated from north by an arbitrary per-segment angle.

## The east/north frame for the "map" anchor

Fill takes its "map"-anchor frame from the mesh: `normalOS` plus `tangentOS`. On Mercator
`StyledFillTileBuilder` writes a constant tangent; on the globe it writes per-vertex geodetic east from
`GlobeFillSubdivider` (`Projection.TangentBasisAt(geo).c0`). A constant tangent lights and textures a curved
fill wrong, so the globe tangent is real data, not a constant in a vertex stream.

The line has a different gap. It has a complete per-vertex tangent **plane**: `StyledLineTileBuilder` writes
`projection.ProjectPoint(geo).Up` per point, `RibbonJob` builds `across = normalize(cross(along, up))`
against that up, and the shader derives `along`. What it lacks is an **azimuth reference** within that
plane — which way is north. Its `tangentOS` is derived along the line for normal mapping and cannot serve.

Inside a known orthonormal frame, east is one angle, not three floats. So the options are:

| Option | Plane | Azimuth | Cost |
|---|---|---|---|
| **(b) reference east projected onto the vertex plane** — chosen | exact | error `≈ Δλ·sin(φ)` | **free** |
| (a′) store `(dot(east, across), dot(east, along))` = `(cos θ, sin θ)` beside `widthScale` | exact | exact | +8 B/vertex (+12.5%) |
| (a) an east 3-vector stream | exact | exact | +12 B/vertex — dominated by (a′) |
| (c) `cross(polarAxis, up)`, the axis as a projection-emitted uniform | — | exact | free — rejected on convention, below |

### The chosen form

`east ≈ normalize(eastRef − up·dot(up, eastRef))`. Projecting onto the vertex's own up keeps the offset in
the true tangent plane, so the residual is purely azimuthal, with no out-of-plane component.

**`eastRef` is world +X, not a per-tile vector.** `IProjection` documents the tangent basis as columns
`c0 = east, c1 = up, c2 = north`, and the backend rebases every tile by its **transpose** taken at the
camera's look-at point (`SceneTileTree`: the same orientation for every tile). After the rebase, world +X
**is** east at the look-at point (+Y up, +Z north) under both projections; Mercator's rebase is the identity,
so there it is east everywhere. The shader therefore needs no object-space transform, and because one rebase
serves every tile the frame is **continuous across the scene — there is no per-tile seam.**

### The residual

Pure *latitude* separation contributes **zero** error: east at any point on a meridian is perpendicular to
that meridian's plane, and so is the reference east, so the projection lands on true east. All error comes
from longitude, and it is meridian convergence:

> error ≈ Δλ · sin(φ), where Δλ is the vertex's longitude separation from the **camera's look-at point**

The bound is *angular distance from screen centre*, not tile size. It is zero at the centre of the view,
grows toward the limb, and shrinks with the visible extent:

| visible extent (≈ zoom) | Δλ at the screen edge | error @ 60°N | error @ equator |
|---|---|---|---|
| z12 | 0.09° | 0.08° | 0 |
| z8 | 1.4° | 1.2° | 0 |
| z4 | 22.5° | ~19° (≈5 px on a 16 px offset) | 0 |
| z2 | 90° | ~78° | 0 |

So the frame is **exact at screen centre and everywhere on Mercator, negligible from z5 up at any latitude,
and meaningful only toward the limb of a zoomed-out globe at high latitude.** The projection collapses
(up ∥ eastRef) only for a vertex ~90° from the look-at point — the very limb at z0–z1. There world +Z is
perpendicular to up, so it is the in-plane fallback; the guard avoids a NaN rather than buying correctness.

The decision rests on this magnitude, not on which projection is in use. The globe is a shipping
configuration (`OpenStreetMapLiberty.unity` serializes `UseGlobe: 1`), so "exact on Mercator" is not an argument for (b).
(a′) costs 12.5% of every line vertex in every tile to correct a few degrees of direction in a rarely-set
property.

**Upgrade trigger for (a′):** a style that sets `line-translate`, viewed on the globe, at z ≤ 4, at high
latitude — all four together.

**Limitation:** the in-plane property holds exactly and the azimuth only approximately, and no test runs the
"map" anchor on the globe. The translate guards above are Mercator fixtures, where world XZ is the tangent
plane.

### Why not (c), which is exact and free

With the sphere's polar axis as a projection-emitted uniform, `east = normalize(cross(axis, up))` is exact at
zero per-vertex cost, falling back to the reference east for a planar projection. It is rejected on
convention, not correctness: it puts *"surfaces are spheres"* into the shader, whereas fill bakes east into
the vertex stream so the shader knows nothing about the projection ("the per-vertex frame is mesh-supplied;
no projection math in shaders"). Since (b) is adequate, there is no reason to spend that convention here.

## The shared px→world measurement

`MapPixelsToWorld` lives once, in `Shaders/Map/PixelsToWorld.hlsl`, and every carrier (line, fill,
fill-extrusion) includes it as `../PixelsToWorld.hlsl` — `Shaders/README.md` § "Shared px→world include" owns
the mechanism and its fence. Per-layer copies are the alternative this replaces, and they are wrong for a
reason that still applies: hand-synced copies drift silently, and properties 1–4 above are what that drift
looks like. A `Map/Shared/` folder is not an alternative either: it routes around the layer-folder
self-containment rule rather than satisfying it. The one sanctioned include is a measurement helper, not a
general licence to share HLSL across layer folders.
