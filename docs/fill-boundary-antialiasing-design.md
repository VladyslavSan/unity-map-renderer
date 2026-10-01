# Fill boundary antialiasing — the outward band

**SSOT for how fill silhouettes are antialiased.** Companion to `docs/fill-parity-design.md` (fill paint) and
`docs/line-antialiasing-design.md` (the line path's straddle, whose ramp expression this reuses verbatim).

---

## The mechanism

A polygon fill's silhouette carries a **one-device-pixel band grown outward from the boundary**, across
which coverage ramps 1 → 0. The band is geometry — two extra vertices per ring vertex and one quad per ring
edge, appended to the same vertex columns and the same index buffer as the interior, per feature — plus a
per-vertex attribute the vertex shader turns into the displacement and the fragment shader turns into
coverage. Earcut never sees it.

The mechanism lives in `FillBandJob` (the geometry and the attribute), `Fill_VertexModify.hlsl` (the
displacement) and `Fill_BandCoverage.hlsl` (the coverage). Each carries its own contract in its header; this
document records the decisions, not the code.

## The rule everything else follows: the ramp lies strictly OUTSIDE the boundary

Both of a ring vertex's band vertices are written at that ring vertex's own tile coordinate, bit-identically;
only the outer one carries a non-zero attribute, and only the vertex shader displaces it. So no vertex of the
filled region moves, coverage reads 1 everywhere the hard fill is, and the ramp occupies a strip the fill
does not otherwise paint.

That is the whole design. **A ramp placed even partly inside the boundary makes two abutting fills each go
partly transparent where they meet**, and `dst = src·a + dst·(1−a)` then shows the background through a
one-pixel trench along every shared edge and every tile seam. At the shipped `FillTileBufferClip: 0`, fills clip
at the tile boundary, so that trench is a **grid**. Three candidate mechanisms are rejected for this reason,
and the rejection is not a preference:

| rejected | why |
|---|---|
| shader alpha-fade eating **inward** from the edge | the trench above, at every shared edge |
| an **inset** band (ring shrunk, ramp inside) | same, and it also changes the polygon's apparent size |
| a **straddle** (±0.5 px) | half the ramp is still inside; the trench is halved, not removed |

The line path uses a straddle and is right to: a line's two long edges have no abutting partner. A fill's do.

**Bounds and depth follow from the same placement.** The interior is untouched, so the frozen geometry
oracle `TileBuildGraphTests.FrozenGoldensSpherical`, captured before the band existed, matches byte-for-byte
through an interior-only filter. That constant is the strongest available evidence that the band moves no
interior vertex, and it must stay a **pre-band** capture — never re-baked.

## Width, and why it is not tunable

`W = 1` device pixel, measured per vertex and per direction so the strip stays a pixel wide under tilt, with
the join's miter factor carried in the attribute's magnitude so it stays a pixel wide *perpendicular to the
edge* through a corner. The coverage gradient is **Euclidean**, never `fwidth` — see
`Fill_BandCoverage.hlsl`'s header for the cost of getting that wrong, and `docs/lessons-learned.md`
§ "Shaders & HLSL" (the "`fwidth` is the WRONG length for an AA ramp" entry) for why a horizontal fixture
cannot see it.

**The ramp is 1 device pixel and is not tunable; the total outward displacement is `1 + outline width`.**
With no `fill-outline-color` the outline width is 0 and nothing changes. With one, the vertex stage widens
the band by the outline width (`_FillOutlineWidthPx`, 1 logical px = `dpr` device px), and the fragment paints
a band fragment in `_FillOutlineColor`. The coverage function is unchanged: it saturates at 1 over the first
`W` px from the fill edge and ramps over the last one, so the rim is solid and the ramp is the same single
pixel. The rim lies outside the boundary, so the fill's area is exact. It reaches one logical px plus the ramp
beyond the edge, where the spec's outline straddles it.

A bindable AA width is not offered. That is a decision, not an omission: a tunable ramp width is how the
line path's removed AA model went wrong (`docs/line-antialiasing-design.md` § "Why a fade inside the band
fails").

## Tile seams: the band stops

A ring edge whose **both endpoints lie on the same clip-window line** gets no quad. The neighbouring tile
carries the mirrored cut and its own fill abuts there, so a band along that edge is ink laid over a fill that
is already present. Exact equality is correct rather than sloppy — `RingClipJob.Intersect` writes the
boundary value verbatim into the clipped axis.

The predicate is the **wider** reading: "both endpoints on one window line" is a superset of
"clip-introduced", so a genuine feature edge that happens to run along the window is also suppressed. That is
the safe direction — the band stops, and the abutting neighbour fills the gap.

A suppressed edge also gives no normal to the miter at its two endpoints. Without that, the band's outer vertex
at a ring vertex on the window bisects the cut edge's normal and leans by the whole displacement across the
seam, so the two tiles' bands overlap in a notch that grows with the outline width. With it, the miter there is
the other edge's own unit normal.

Suppression for an outward band is cheaper than it would be for an inset one: there is no taper to zero, no
half-pixel step and no T-junction to reconcile.

**Limitation:** a window-line edge that is not a server cut gets no outline. It can occur in tile-aligned data. In
decoded OpenFreeMap tiles a border edge always has a mirror in the neighbour tile.

## A band never draws where its fill is hidden

A band vertex is displaced only where its surface faces the camera, tested in view space. Cull Back hides a
fill on the far side of the globe, and the band collapses to zero width there, so it never draws where its fill
does not. `GlobeFillBandRenderTests` pins it: every gained pixel lies within `MiterLimit + 1` px of a triangle of
the drawn, camera-facing mesh.

## A band quad that twists still draws whole

The band is the outline: one outward displacement of `1 + outline width` device pixels, painted in the outline
colour and ramped over its last pixel. A missing triangle is therefore a gap in the outline, not an antialiasing
defect.

Take an edge of render length `L`. Its quad has the edge as its base and two outer vertices at heights `d` over
it. Each outer vertex also leans along the edge by a fraction `s` of its height: the miter's component along the
edge over its outward component. The quad winds backwards, and Cull Back drops a triangle of it, iff
`L < d_end · (s_start − s_end)`. A convex end leans away from the edge, so only a reflex end can satisfy that. The
height `d` is the pixel width of the band, which the mesh does not know, so one mesh must be right at every zoom
and every width.

A smaller height does not fix it. It moves the outer vertices to where the two miter rays cross, which is nearer
than the band is wide, and so thins the outline there. No other triangulation of the four points fixes it either: a
quad whose outer edge crosses reverses one triangle in every triangulation.

The band keeps every vertex where it is and adds one triangle. `FillBandJob` emits the triangle of the quad that
can reverse a second time, wound the other way, for each edge whose ends satisfy `s_start > s_end`. Cull Back draws
exactly one of the pair. When the quad is whole, the original draws. When it crosses, the reverse draws, and it
covers the wedge that the culled triangle would have covered, at the full width. An edge with no reflex end gets no
extra triangle.

The fill band relies on Cull Back. With Cull Off both triangles of the pair draw, and a translucent outline
composites that area twice. `FillBandAttributeTests.ShippedFillMaterials_CullBack_SoATwistCoverDrawsOnce` pins
`_Cull` on the committed fill materials.

**Limitation:** near a sharp reflex spike the cover paints outline over a little of the polygon's interior, so a
translucent outline over a translucent fill composites twice there. No test covers that case.

## A sharp convex tip is round

The miter factor is capped at `MiterLimit`. A corner that turns beyond the cap's angle, about 151 degrees, would
clip its miter. Clipping leaves the sides of the tip thinner than the outline along the edges, and it leaves a
long spike beyond the apex. The outline is as thick as the band is wide, so that is a visible defect.

A convex corner that the cap clips, and a 180 degree spike, get a round tip instead. The two edges end their quads
square, with no lean, and a fan of triangles joins them around the vertex. The fan has up to `MaxFanSegments`
segments of at most 30 degrees each, so a spike gets a semicircle. Each fan vertex is an ordinary band outer
vertex at the ring vertex's own coordinate, with a unit direction and `side = 1`, so the shader sizes it per
direction in device pixels, and the ramp covers its last pixel like any other band vertex.

A spike has no side, so it is read as a needle: the cap is on the far side of the tip. A zero-width inward slit is
also a 180 degree turn, and its cap then lands in the material beyond the slit's end.

A reflex corner keeps a miter. The two walls' offset lines cross at `1/cos(θ/2)` band widths along the bisector, and
the band's outer vertex sits there, so the outline is as wide as the band along both walls.

**Limitation:** a sharp reflex corner keeps the clipped miter (`MiterLimit`), and the outline of each of its two walls
tapers toward the corner.

## The residual rim, accepted (maintainer call)

Two polygons **of the same layer abutting inside one tile** share no such predicate: each one's band ramps
across the other's interior, and the pair composites twice. On a translucent layer that is a **rim of
`f(1−f)`** — over-ink, never a trench, and absent at `α = 1` and on every opaque layer.

An outline widens the residual: bands are emitted after each feature's interior and the layer does not write
depth, so index order is draw order. On an opaque layer the later polygon's interior covers the earlier rim and
one clean rim shows. On a translucent layer both rims show, so a shared edge carries a rim about twice as wide
and the `f(1−f)` over-ink on top of it. This limitation is accepted.

The rim is accepted rather than closed. The alternative is an intra-layer directed-edge hash between ring
assembly and the band node. It costs a new node, a `NativeHashMap` sized to the layer's edge count, a second
pass over every ring and its own tests. It still misses a shared boundary expressed with **different vertex
counts on the two sides**, which is common in real MVT landcover, and every coincident edge in another tile
or another layer. `FillBoundaryBandRenderTests.AbuttingPolygonsInOneLayerLeaveNoBackgroundAlongTheirSharedEdge`
bounds the rim on both sides against the derived `(2−f)×` double-composite value, and asserts the rim is
present so the bound cannot pass over nothing.

## Depth, shadows, and the deferred path

Every **depth-writing** fill pass clips the band out (`clip(side > 0 ? -1 : 1)`) rather than shading it. A
coverage-0 band fragment under `ZWrite On` still writes depth and casts a shadow, which fattens the depth
silhouette and the shadow by a screen-space skirt. Clipping on `side` rather than on coverage keeps that
bit-exact.

**Limitation:** a fill shaded through the deferred GBuffer path gets no boundary antialiasing. Both this and
the depth clip itself are **inert in the shipped configuration** — four of the fill shader's six passes never
rasterise a fill fragment as configured (Forward+, fills at queue 3000, `CastShadows => Off`; see
`docs/lessons-learned.md` § "Four of the fill shader's six passes never rasterise a fill fragment in the
shipped configuration"). The clip is defence-in-depth, correct and load-bearing the moment a fill becomes
opaque-mode or casts shadows. Its instrument is structural until then; a change that makes a fill opaque or
shadow-casting also owns the rendered test.

The forward passes' `ZWrite` is inherited from SubShader level and is `0` on both committed fill materials.
That is a **configuration**, so a fence asserts it rather than leaving the classification to be silently
invalidated.

## `fill-antialias`

The Style Spec's `fill-antialias` is a JSON boolean, default true. It is the one antialiasing switch that
stays implementable **per layer**: MSAA and camera post-process AA are render-target settings and cannot be
turned off for a single fill.

`StyledFillTileBuilder.BuildLayerInput` resolves it at the tile's **build zoom** (so a zoom-varying value
takes effect only on a re-mesh) and sets `FillMeshPipeline.LayerInput.SuppressBoundaryBand` — a layer that
opts out emits no band geometry at all and costs nothing at runtime.

**The `_FillAntialias` uniform is not the consumer.** It stays declared, instanced, bound and read by no
pass. Giving it a shader reader would fake a consumer for a property that is answered in geometry. Retiring
the uniform is a separate change (it touches the CBUFFER, the DOTS bridge, `MapInstanceData` and the
material tests).

## Rejected and deferred alternatives

| item | why |
|---|---|
| **`fill-outline-color` as real line geometry** | a second mesh and draw per fill layer; the recoloured, widened band gives a solid 1 logical px rim with none (`docs/fill-parity-design.md` § 7) |
| **a data-driven `fill-outline-color`** | nothing tells an inner band vertex from an interior one (both carry a zero band value), and the curved arm interleaves them; a constant or zoom colour only |
| **a world-space (metres) SDF variant** | a different mechanism, not a refinement of this one |
| **MSAA / camera post-AA** | ruled out; a per-layer opt-out is unimplementable under either |
| **the intra-layer edge hash** | "The residual rim, accepted" — a partial fix to a partial population, with a known blind spot |
| **`+1V` band via a ring→merged-vertex index map** | halves the band's vertex cost by reusing the interior's boundary vertices, but couples the band node to `EarcutJob`'s bridge duplication. Revisit only if `+2V` measures |
| **`float2` packing of the band attribute** | 8 B instead of 12, using `any(bandDir)` as the `side` discriminator — but a zero-length miter at a spike vertex then reads as interior and leaves a 1 px notch |
| **fill-extrusion** | a separate graph; extruded buildings keep hard silhouettes |
