# Fill-layer parity — design

How this renderer paints the MapLibre Style Spec's `fill` layer beyond a flat `fill-color`: which carrier
holds each property, and why. Semantics come from the public MapLibre Style Spec. Companions:
`docs/fill-boundary-antialiasing-design.md` (the silhouette band behind `fill-antialias`) and
`docs/meshing-design.md` (the fill mesh pipeline).

---

## 1. Carriers — where each property lives

| Property | Carrier |
|---|---|
| `fill-color` | two-carrier split: constant/zoom → `_BaseColor` uniform; data-driven → per-feature bake into the COLOR stream |
| `fill-opacity` | constant/zoom → `_Opacity` uniform; data-driven → multiplied into the baked COLOR alpha |
| `fill-pattern` | the style's sprite sheet, sampled in the fragment over a world-space pattern coordinate |
| `fill-translate`, `fill-translate-anchor` | the fill vertex hook, `MapVertexModify` |
| `fill-sort-key` | a stable per-layer feature order, applied before meshing |
| `fill-antialias` | the outward boundary band's geometry, suppressed per layer when false |
| `fill-outline-color` | parsed and bound to `_FillOutlineColor`, read by no pass — it is line geometry, not fill paint |

A failing colour expression paints white, not black: `StyleProperty.TryEvaluate` returns false, and
`StyledFillTileBuilder` keeps the vertex colour white.

---

## 2. `fill-pattern`

### The spec rule

When `fill-pattern` is present, `fill-color` is **not used at all**, and a layer whose pattern image cannot be
resolved is **not painted**. A pattern layer characteristically declares no `fill-color` (Liberty's
`road_area_pattern` and `landcover_wetland` are two), so falling back to the colour's spec default,
`rgba(0,0,0,1)`, paints opaque black — wrong twice over.

### Where the sprite sheet comes from

`SymbolSubsystem` owns the sheet (`SpriteAtlas`, `IconTexture`) and fetches it fire-and-forget from its own
`SetStyle`, under its per-style cancellation scope. Fill materials are built synchronously in
`MapView.SetStyle`, before the sheet can exist. `MapView.LateUpdate` therefore pulls the pair every frame into
`RenderLayerSet.SetSprites`, which early-outs on an unchanged pair; each pattern-declaring `FillRenderLayer`
resolves its sprite there. A pattern layer starts unresolved (it paints nothing) and resolves when the sheet
lands — the same async posture as icons.

- **The fetch does not depend on the style having symbol layers.** `SymbolSubsystem.SetStyle` starts it
  before its "no symbol layers" early return, so a style with pattern fills and no symbol layers still gets
  its sheet. If the fetch sat behind that return, every pattern layer would clip forever with no error.
- **Rejected: await the sprite fetch before `SetStyle` commits.** It reads clean, but it blocks style commit
  on a PNG, and every EditMode test that sets a sprite-bearing style would block on a live network fetch.
- **Not done: move sheet ownership out of `SymbolSubsystem`.** A new owner must rebuild the fetch's
  cancellation scope, the restyle-race contract and the dispose-once paths. A regression there shows as a
  restyle race, not as a pixel diff, so no rendering test catches it.

### Late resolve is a pure material-uniform change

Stream 1 carries the pattern coordinate on every emitted fill vertex, flat and globe-subdivided alike
(`StyledFillTileBuilder.PatternCoord`, written from each vertex's tile position), and the forward pass carries
it to the fragment. Resolving a pattern therefore sets uniforms only: no tile rebuild, no re-mesh, no restyle.

### Uniform contract

Declared in each fill input file and in the DOTS instancing bridge, like every other fill prop:

| Uniform | Meaning |
|---|---|
| `_FillPattern` | `0` = solid (the `fill-color` path), `1` = this is a pattern layer |
| `_PatternMap` | the sprite sheet texture (unbound until the sheet lands) |
| `_PatternRect` | `xy` = sprite top-left in sheet px, `zw` = sprite size in sheet px |
| `_PatternScale` | `xy` = pattern repetitions per world unit (see "Why the pattern coordinate is world-space") |

**Unresolved is a degenerate rect, not a magic third state.** `_FillPattern == 1 && _PatternRect.zw == 0`
means "pattern layer, sprite not resolved" ⇒ the fragment **`clip()`s**. No sentinel constant, no tri-state
enum — a zero-area sprite rect is genuinely unresolvable, so the natural reading is the correct one. It is
also the permanent behaviour for a sprite name absent from the sheet (spec: layer not painted).

`clip()`, not "write alpha 0". Alpha 0 only disappears in a blended queue. The fill is alpha-blended with
`ZWrite Off` today (`BaseTweaker.ApplyBaseContract`), but alpha 0 would start painting black again the day that
blend changes. `clip()` is correct in either queue and expresses "not painted" rather than "painted
invisibly".

`_PatternMap` does **not** reuse stock `_BaseMap`: `_BaseMap` is the base-look slot on the shared lit material,
and the pattern needs its own UV transform. It also cannot collide with a MapLibre style term, per the
`fill-X → _X` styling-bind collision hazard (`_FillPattern` already holds `fill-pattern`).

### Sampling

```
patternUv = input.uv * _PatternScale.xy       // world-unit offset → repeats
sheetUv   = (_PatternRect.xy + frac(patternUv) * _PatternRect.zw) / sheetSize
```

`frac()` across a packed atlas breaks the mip derivative (a full-texture gradient at every repeat seam) and,
under bilinear filtering, bleeds the neighbouring sprite in at the seam. The shader samples with
**`SAMPLE_TEXTURE2D_GRAD`**, passing derivatives taken from the *unwrapped* `patternUv` scaled into sheet
space — the same reason the line shader resolves width in screen space rather than trusting interpolation.

It samples through the inline `sampler_PointClamp`, not the sheet's own sampler, so pattern filtering does not
depend on the shared texture's `filterMode`. The sheet is bilinear for icons, and every sprite carries a
one-texel transparent border (`docs/labels-and-symbols-design.md` § "Sampling the sheet — bilinear + a
one-texel padded repack"). A tiling seam must continue into the opposite edge's pixels, not fade into
that border, so a pattern samples point-filtered inside its content rect and never touches the border.
**Open:** bilinear pattern sampling needs a second, wrap-replicated border role selected per sprite; that
section of the labels doc tracks it.

### Why the pattern coordinate is world-space

A pattern's size on screen must follow the **display** zoom. A per-layer material uniform cannot see a
tile's own zoom, and a tile's zoom is not `floor(displayZoom)`: past the source's maxzoom the same tiles are
stretched (overzoom), and a screen-space tile selector routinely covers the view with mixed-zoom neighbours.
Any tile-space pattern scale corrected from the display zoom is therefore wrong by up to the overzoom factor,
disagrees across mixed-zoom neighbours (a seam), and — if it rounds repeats to whole numbers so tiles meet at
their edges — steps while zooming.

**So the tile is removed from the calculation.** `StyledFillTileBuilder.PatternCoord` writes stream 1 as the
vertex's offset from the tile origin in **world units** (Web-Mercator metres) instead of a 0..1 tile fraction;
the builder knows the tile's real `Z`, so it can bake real distances. `FillPattern.RepeatsPerWorldUnit` then
returns `1 / period`, a function of the display zoom alone, and `FillRenderLayer` pushes it into
`_PatternScale` every frame (the same per-frame delivery as `ApplyLineDashArray`):

| Mode | Period (world distance per repetition) |
|---|---|
| `ScreenRelative` (default) | `spriteLogicalPixels × WebMercator.GroundResolution(displayZoom)` — the Style Spec's meaning: the sprite occupies its authored pixel size on screen |
| `WorldAbsolute` | the configured period, independent of zoom entirely |

Overzoom, mixed-zoom cover and stepping cannot influence a quantity that does not mention a tile. No rounding
is needed, so scaling is continuous.

**Limitation — a phase step at tile edges.** Each tile's pattern starts at **that tile's** origin, so unless
the period divides the tile's span there is a phase step at tile edges. It is a static spatial artefact;
zooming does not make it worse. Removing it needs each tile's GLOBAL origin in the shader — per-tile data that,
at world scale, needs split-precision floats. It is not built: on the real sprites in use the artefact is not
visible enough to justify that machinery.

### Mode selection — `x-fill-pattern-metres`

From the style, via ONE key: `x-fill-pattern-metres`, the tiling PERIOD — the world distance one full
repetition spans (at a period of 1, the pattern coordinate advances by one repetition per world unit). Parsed
into `Fill.PaintProperties` beside `PatternName`, so the style stays the SSOT and `FillRenderLayer` holds no
settable knob.

One key rather than a mode plus a size: world sizing is meaningless without a period and a period is
meaningless under screen sizing, so folding them together makes the invalid combination unrepresentable.
Absent ⇒ `ScreenRelative`, which is also the enum's zero value — a stock MapLibre style has no way to ask for
anything else. The `x-` prefix marks it a non-spec extension that can never collide with a key the spec adds
later; MapLibre ignores unknown keys, so such a style still renders there. An unusable value degrades to
`ScreenRelative` rather than throwing: an unreadable EXTENSION must never cost you the layer.

"World units" are Web-Mercator metres — the units the mesh, tile origins and camera already use. They equal
true ground metres at the equator and diverge by 1/cos(latitude) away from it; expressing the period in
anything else would put the pattern in different units from the geometry it sits on.

### Where the clip runs

The pattern clip runs in the Lit forward and GBuffer passes and in the Unlit forward pass. The depth-only,
depth-normals and shadow-caster passes carry `uv` only under `_ALPHATEST_ON`, which fill materials do not
enable, so clipping there would mean an unconditional varying in all three. Fills run `ZWrite Off` under a
painter's-algorithm draw order (`BaseTweaker.ApplyBaseContract`) and cast no shadows, so an unresolved pattern
layer never reaches those passes in this configuration. Revisit if fills gain depth write, feed SSAO, or cast.

### What every fill uniform must also satisfy

- **The BRG backend packs fill uniforms explicitly.** Every fill prop declared DOTS-instanced needs a field on
  `MapInstanceData`. On the BRG path an unlisted property reads instance metadata nothing wrote, not the
  material — for `_PatternRect` a zero-area rect that clips every pattern fill, while the MeshRenderer-based
  tests stay green.
- **Background clones the fill base material.** A `_FillPattern` of 1 inherited from a base `.mat` would clip
  the whole background quad, so the background bind zeroes the pattern pair, as it zeroes `_FillTranslate`.
- **`ShaderProperties/Fill/PropertyNames` is the CBUFFER set, not "fill shader properties".** The structural
  parity guard enforces registry ↔ CBUFFER equality, and a texture is not a CBUFFER member, so `_PatternMap`
  lives in the sibling `TexturePropertyId` (the same posture as `WorldSymbolRenderer.AtlasPropId`).

## 3. `fill-sort-key`

`StyledFillTileBuilder` orders a layer's features by `fill-sort-key` before meshing. This works because flat
fills run `ZWrite Off`, so coincident coplanar triangles never depth-reject and the last rasterized wins. Draw
order is index-buffer order: the mesh concatenates each polygon's indices in polygon order, and polygon order
follows feature order.

- **Stable.** The declared index is folded in as the tiebreak, because `Array.Sort` is an introsort and not
  stable; features with equal keys keep source order (the spec's implicit ordering).
- **Absent key ⇒ no sort at all.** The builder returns the same instance, so a layer without a key stays
  allocation-free and keeps its triangle order.
- An unevaluable key falls to 0, matching `TryEvaluate`'s contract elsewhere in the builder.

## 4. Data-driven `fill-opacity`

The evaluated per-feature opacity multiplies into the baked COLOR stream alpha — the same loop that bakes a
data-driven colour. **`_Opacity` is bound to 1 on the material whenever opacity is baked**: the fragment does
`alpha *= vColor.a * _Opacity`, so a live uniform would apply it twice. Data-driven line `Width` follows the
same precedent in `MaterialFactory`.

## 5. `fill-translate`

`MapVertexModify` offsets the vertex by a screen-pixel distance measured **per axis** — a single scalar skews
under tilt, where the north axis foreshortens and east barely does — and branches on `fill-translate-anchor`:
`map` rides the map (rotating and tilting with it), `viewport` is fixed to the screen.

The offset is applied in **world** space and converted back through `GetWorldToObjectMatrix()`. An
object-space offset depends on the object→world scale: at the scale the test fixtures use, a 40 px translate
added to `positionOS` moves the geometry by a fraction of a pixel, and at another scale it would be wildly
wrong. The world-space form is scale-independent.

**Limitation:** the per-axis measurement exists for tilt, but no test exercises tilt — the snapshot camera is
top-down orthographic, so only bearing and zoom are checked.

## 6. `fill-antialias`

Fill silhouettes are antialiased by an outward one-device-pixel band grown from the polygon boundary
(`docs/fill-boundary-antialiasing-design.md`). `StyledFillTileBuilder.BuildLayerInput` resolves
`fill-antialias` at build zoom and suppresses the layer's band geometry when it is false — that is what makes
the property per-layer at all.

**The `_FillAntialias` uniform stays inert, permanently.** It is declared, instanced (`MapInstanceData`) and
bound (`MaterialFactory`), and read by no pass. Giving it a shader reader would fake a consumer for a property
that is answered in geometry.

Rejected alternatives that still constrain the mechanism:

- **Couple it to `fill-outline-color`.** The spec coupling is one-directional: the outline is drawn *only
  when* `fill-antialias` is true, but antialiasing does not require the outline. What the spec couples is the
  outline's visibility, not the mechanism that antialiases a silhouette, and a silhouette can be antialiased
  with no outline draw at all.
- **An inset fade.** Fills are alpha-blended with `ZWrite Off`, so a fade inside the styled region lets the
  layer beneath bleed through every shared edge between adjacent polygons. The band grows **outward**, so every
  pixel the layer is meant to paint keeps coverage 1, and `dst = src·a + dst·(1−a)` shows nothing through at
  `a == 1`.
- **MSAA.** A render-target setting cannot be switched off for one layer, so MSAA would make
  `fill-antialias: false` unimplementable. It also costs bandwidth on mobile. (URP takes the sample count
  from a camera's target texture in preference to the pipeline asset, so a snapshot that renders into its own
  render texture measures MSAA only if that texture's `antiAliasing` is set.)

## 7. `fill-outline-color` is line geometry

`fill-outline-color` is not a paint-plumbing property: it means real LINE geometry along the polygon boundary —
what style authors fake with a second `line` layer drawn after the `fill`. It therefore shares the line
tessellator, not the fill paint path, and is separate work. It is parsed (`Fill.PaintProperties.OutlineColor`,
falling back to `fill-color` when absent) and bound to `_FillOutlineColor`, which no pass reads.

`fill-extrusion` is a different layer type and is not covered here.
