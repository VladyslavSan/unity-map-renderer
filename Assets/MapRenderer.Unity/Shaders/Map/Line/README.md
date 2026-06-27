# Map/Line — shader notes

Why this shader looks the way it does, and where it should go. Captured analysis — **not yet acted on**
(tracked as stage S67). Read before "fixing" the pass list.

## Current state

`Line.shader` has exactly **one** pass: `UniversalForward` (forward-lit, `Queue=Transparent` ≥ 2501,
`ZWrite Off`). It has **no** DepthOnly / DepthNormals / ShadowCaster / GBuffer passes — unlike `Map/Fill`,
which has all five.

The shader carries a banner comment telling you not to add those passes "because lines are transparent."
That comment states a *consequence* as if it were a *decision about lines*. It is misleading. See below.

## A line is an opaque feature — same class as a fill

There is **no hierarchy** here. A road, a boundary, a coastline — these are opaque map features, exactly
like a fill polygon. Nothing about a line is conceptually see-through. The ribbon's *interior* is fully
opaque; only the ~1px feathered **edge** is partial-alpha.

The line is `Queue=Transparent` for one reason only: its **anti-aliased edges are drawn with alpha
blending** (the `fwidth`-smoothstep coverage feeds `alpha`). The causal chain is:

```
blended AA edges  →  must be Queue=Transparent (≥2501)  →  URP auto-excludes it from the
                                                            depth / normals / shadow prepasses
```

So the missing passes are a **side effect of an AA-technique choice**, not a statement that lines are
second-class. As the camera tilts / goes 3D (S52 camera-relative work, the deferred globe), a line behind a
fill-extrusion *should* be occluded — it needs to be a depth citizen like everything else.

## The one genuine technical wrinkle

A feathered alpha edge has **no single depth to write**. A pixel that is 40% line / 60% background has no
well-defined `_CameraDepthTexture` value:

- write full depth → a ~1px **depth halo** around every line;
- write no depth → edges don't occlude.

This is the *only* real reason blended geometry is awkward in a depth prepass. It's a "pick a coverage
threshold" problem, not a blocker — but it's what forces the technique decision below.

## The decision (S67) — which AA technique, now that we want depth-correct lines

Not *"should lines have passes"* (yes — equal class). The real fork:

- **Option A — make lines opaque** (`Queue=Geometry`, alpha-clip the ribbon core; AA via the URP asset's
  post-AA (FXAA/SMAA/TAA) or alpha-to-coverage). → full opaque pass set like Fill, depth-correct
  everywhere, simplest model; likely lets the whole transparent-line apparatus **and** its S07
  painter's-algorithm coplanar-ordering special-casing go away. **Leading option.**
  *Verify first:* what anti-aliasing the project's URP asset uses — if it already does screen-space post-AA,
  today's blended edges are redundant and opaque lines look identical; if MSAA-only, lean on
  alpha-to-coverage; if neither, edges regress and Option B is safer.
- **Option B — keep blended edges, add depth/normals passes with a coverage clip** (write depth/normals
  only where coverage ≳ 0.5). → keeps buttery blended edges, gains approximately-correct depth. Trade-off:
  two AA models that can mismatch at the ~1px edge, more complexity, forward pass stays `ZWrite Off` so all
  occlusion depth comes from the prepass.

**Which passes, with a real consumer each** (don't mirror Fill's five reflexively):

| Pass | Real consumer? |
|---|---|
| DepthOnly | `_CameraDepthTexture`: soft particles, fog, DoF, camera-depth fade, **3D occlusion** — yes. |
| DepthNormals | SSAO — marginal for thin ribbons; justify or drop. |
| ShadowCaster | should a road/boundary cast a shadow? Probably no — decide explicitly. |
| GBuffer | only if lines must render in the deferred path. |

## The constraint that makes it non-trivial

The line's geometry is **built in the vertex shader** — world-space lateral extrusion from POSITION +
NORMAL + TEXCOORD0–2 (extrudeN / side+dist / widthScale), plus gap-width, line-offset, dash-U, and the +Y
z-fight lift (see `MapLineForwardPass.hlsl`, or its post-S66 name `Line_LitForwardPass.hlsl`). **Every** new
pass must replay that *same* extrusion in its vertex stage, or it writes depth/normals for the un-extruded
**centerline**, not the actual ribbon silhouette. So this is not "copy Fill's depth passes" — Fill's depth
passes run flat geometry through the shared `MapVertexModify` hook; the line needs its bespoke extrusion
shared across all its passes (a `Line_VertexExtrude.hlsl` helper included by every line pass).

## Status

- **Captured, not implemented.** Sitting here as the rationale for the S67 work item.
- Folder restructure / renames (`MapLine* → Line_*`) are a *separate*, behaviour-neutral stage: **S66**.
  S67 builds on S66's self-contained `Map/Line/` folder so new passes land with the final names.
