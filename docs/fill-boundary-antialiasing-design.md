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

Suppression for an outward band is cheaper than it would be for an inset one: there is no taper to zero, no
half-pixel step and no T-junction to reconcile.

## The residual rim, accepted (maintainer call)

Two polygons **of the same layer abutting inside one tile** share no such predicate: each one's band ramps
across the other's interior, and the pair composites twice. On a translucent layer that is a **rim of
`f(1−f)`** — over-ink, never a trench, and absent at `α = 1` and on every opaque layer.

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
| **`fill-outline-color`** | a *differently coloured* boundary is real line geometry, a separate mechanism (`docs/fill-parity-design.md` § "`fill-outline-color` is line geometry") |
| **a world-space (metres) SDF variant** | a different mechanism, not a refinement of this one |
| **MSAA / camera post-AA** | ruled out; a per-layer opt-out is unimplementable under either |
| **the intra-layer edge hash** | "The residual rim, accepted" — a partial fix to a partial population, with a known blind spot |
| **`+1V` band via a ring→merged-vertex index map** | halves the band's vertex cost by reusing the interior's boundary vertices, but couples the band node to `EarcutJob`'s bridge duplication. Revisit only if `+2V` measures |
| **`float2` packing of the band attribute** | 8 B instead of 12, using `any(bandDir)` as the `side` discriminator — but a zero-length miter at a spike vertex then reads as interior and leaves a 1 px notch |
| **fill-extrusion** | a separate graph; extruded buildings keep hard silhouettes |
